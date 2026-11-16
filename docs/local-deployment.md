# Esecuzione locale

Prerequisiti: Docker Engine, Docker Compose v2.

- Avvio: docker compose -f docker/compose.yml up --build -d
- PWA http://localhost:5173 ; REST http://localhost:8080/api/perizie ; MCP http://localhost:8080/mcp
- Liveness (processo attivo): http://localhost:8080/health/live
- Readiness **parziale** (PostgreSQL + ClamAV + OIDC + metadati S3): http://localhost:8080/health/ready
- Keycloak: http://localhost:8081 ; SeaweedFS S3: http://localhost:8333
- Utente solo locale: localuser / local-only-user; admin Keycloak locale: localadmin / local-only-admin.
- Test backend PostgreSQL reale: docker compose -f docker/compose.yml --profile test run --rm tests
- Installazione frontend riproducibile: `cd src/frontend && npm ci` (richiede Node.js 22 e il `package-lock.json` versionato); non usare `npm install` per build/CI di rilascio.
- Backup/restore **solo PostgreSQL** in `scripts/db/{backup,restore}.sh` con checksum e prova CI su due server: `docs/database-backup-restore.md`. Questi script NON salvano documenti S3/Blob né costituiscono un backup completo.

ATTENZIONE: i segreti dimostrativi e il singolo tenant hardcoded nel realm Keycloak sono esclusivamente per il computer di sviluppo. Non distribuire questa configurazione in produzione. Servono HTTPS, provider OAuth e claim tenant reali, secret manager, backup, migrazioni versionate, scansione file e security tests.

## Login OAuth end-to-end

Il browser e i token usano `http://localhost:8081/realms/peritial`, l'issuer configurato in Keycloak. Per l'API, che gira in un container, `localhost` indica il container stesso: solo nell'ambiente Development il `OidcDevelopmentBackchannelHandler` reindirizza le richieste di retrieval JWKS verso `http://keycloak:8080`, lasciando intatto l'issuer e la validazione di firma, audience e scadenza.

Le richieste di metadata OpenID partono dall'indirizzo interno configurato in `Auth:MetadataAddress`. Il rewrite è rifiutato in Production.

Test E2E reale: `E2E_FULL_STACK=1 E2E_BASE_URL=http://localhost:5173 npm run test:e2e` da `src/frontend` dopo avvio Compose. Questa verifica fa parte della CI Compose.

In produzione usare sempre OIDC HTTPS raggiungibile sia dai client che dall'applicazione, e vietare i rewrite di sviluppo.

Il realm dimostrativo importa intenzionalmente soltanto gli scope `peritial.read`, `peritial.write` e il meta-scope OIDC `openid`; `profile`/`email` non sono disponibili in questa configurazione. Il frontend richiede soltanto gli scope supportati per default; in altri ambienti personalizzare `VITE_OIDC_SCOPES` durante la build.

## Database multi-tenant con RLS

Il database viene inizializzato con `scripts/db/001_init.sql` (tabelle e policy `ENABLE/FORCE RLS`) e, **solo nella configurazione locale**, con `scripts/db/002_dev_app_role.sql` per l'account `peritial_app` a password di esempio. L'API usa il ruolo applicativo ristretto, non il superuser `peritial`. `Database:Bootstrap` resta disattivato nell'API.

Dopo upgrade di un volume PostgreSQL preesistente, gli script in `docker-entrypoint-initdb.d` **non vengono rieseguiti**. Applicare lo schema e il provisioning del ruolo da un amministratore con backup prima dell'upgrade; cancellare i volumi solo se contengono dati effimeri. Il nuovo runner versionato per **database vuoti** è documentato in `docs/database-migrations.md` (ADR 0015); per lo schema legacy V001 **esatto** è disponibile `adopt`, per la **sola variante senza colonna/indice lease** è disponibile `adopt-prelease`. Entrambi richiedono stop dei writer e backup verificato (`docs/database-migrations.md`). Altre divergenze e upgrade persistenti completi non sono supportati (#6).

## Scansione antivirus locale

Compose avvia un demone ClamAV `clamd` con immagini/firme pre-caricate, connessione TCP solo interna (`clamav:3310`) e health check obbligatorio prima dell'avvio dell'API. Non è pubblicata alcuna porta scanner. I file PDF/JPG/PNG/XML/DOCX validati sono analizzati in memoria con ClamAV **prima** di raggiungere SeaweedFS/PostgreSQL; se ClamAV non è disponibile il caricamento è bloccato (503), un EICAR viene rifiutato (422). La demo disattiva `freshclam` per evitare aggiornamenti esterni non deterministici in CI: non replicare questa configurazione in produzione. L'immagine ufficiale può richiedere una quantità significativa di RAM. Quarantena persistente, worker di recovery e aggiornamento firme sono ancora aperti (#5/#21).

## Recupero durevole degli upload locali (#21)

Lo schema crea `upload_intents` e due login **distinti** solo in sviluppo: `peritial_app` per le richieste e `peritial_recovery` per il worker. `UploadRecovery__ConnectionString` deve usare il secondo ruolo con privilegi ristretti, mai le credenziali amministrative. Il worker controlla gli intenti più vecchi di un'ora una volta al minuto (max 20 per ciclo), usa lock PostgreSQL per evitare doppi cleanup e verifica che non esista già un documento referenziante. In dev si può controllare *solo il conteggio* dei pendenti con `docker compose -f docker/compose.yml exec -T postgres psql -U peritial -d peritial -Atc 'SELECT count(*) FROM upload_intents;'`. Un volume preesistente richiede migrazione e aggiornamento del ruolo prima di avviare l'API: non eliminare volumi contenenti dati reali.

## Lease e restart del worker

Il bootstrap locale è compatibile con schema preesistente e aggiunge `lease_expires_at_utc` a `upload_intents`, con grant `UPDATE (lease_expires_at_utc)` riservato al ruolo applicativo. Prima dell'upgrade di una istanza con volume persistente, usare una migrazione verificata e backup del DB, senza cancellare i dati: le baseline manuali per **V001 esatta** e **pre-lease riconosciuta** sono supportate da `adopt` e `adopt-prelease` e testate in CI; upgrade automatici/rollback di volumi legacy divergenti non sono ancora supportati (#6; `docs/database-migrations.md`). In produzione monitorare rinnovi falliti e backlog del worker, non ridurre l'ora di grazia senza verificare PUT lunghi. In CI Compose viene eseguito un riavvio forzato SIGKILL con seed dell'orfano per verificare la persistenza.

## Fault injection interruzione PUT (#21)

La pipeline CI usa un overlay Compose separato, `docker/compose.chaos.yml`, che instrada il PUT S3 tramite un proxy Python capace di inoltrare i primi byte e bloccare il trasferimento. Solo dopo il segnale di byte inoltrati viene inviato SIGKILL al processo API; poi la stack standard viene riavviata ed è verificata la pulizia PostgreSQL/SeaweedFS. L'overlay è esclusivamente di **test**, non abilitarlo su installazioni operative e non esporre il proxy. ADR 0014.

## Liveness e readiness applicativa (#19)

`GET /health/live` risponde HTTP 200 quando il processo HTTP funziona, **anche** se una dipendenza è indisponibile. `GET /health/ready` (senza token) controlla entro un **budget comune di 3 secondi**:
1. `SELECT 1` usando il ruolo PostgreSQL applicativo, senza bypass RLS;
2. `zPING` su ClamAV e risposta esatta `PONG`;
3. Discovery OIDC e JWKS (issuer pubblico corrispondente, chiave di firma strutturalmente valida, HTTP bounded, senza redirect);
4. **Metadati dello storage documentale** scelto da `Storage:Provider`: `HeadBucket` per S3/SeaweedFS oppure `GetContainerProperties` per Azure Blob. Il probe non effettua PUT, DELETE, LIST degli oggetti o GET dei byte dei documenti. Bucket/container inesistenti, rete indisponibile e risposta non 200 implicano `not_ready`.

La risposta è HTTP 200 `{"status":"ready"}` o HTTP 503 `{"status":"not_ready"}`, senza hostname, chiavi, filename, metadata delle dipendenze o errori interni.

### Identità storage di sola lettura

Fuori da `Development` le credenziali di readiness **devono essere diverse** da quelle per upload e cleanup. Se mancano, l'API non deve avviarsi; non attivare `Storage__Readiness__AllowSharedDevelopmentCredentials` in produzione.

- **S3**: `Storage__Readiness__S3__AccessKey` e `Storage__Readiness__S3__SecretKey` sono obbligatorie e non devono corrispondere entrambe alla coppia `Storage__Minio__AccessKey/SecretKey`. La policy del principal deve consentire soltanto `s3:ListBucket` sul bucket specifico (per HeadBucket), senza `s3:GetObject`, `PutObject`, `DeleteObject` o enumerazione di oggetti; sul provider effettivo verificare l'applicazione IAM e i limiti operativi. La configurazione condivisa di Compose usa l'account locale di test solo con opt-in e non dimostra isolamento IAM.
- **Azure Blob**: usare `Storage__Readiness__Azure__ManagedIdentityClientId` con `Storage__Azure__ServiceUri` HTTPS e RBAC limitato alla lettura dei metadati del container, **oppure** `Storage__Readiness__Azure__ConnectionString` riferita a una credenziale/SAS di sola lettura diversa dalla connection string del writer. Il contenitore è `Storage__Azure__Container` (default `documents`). Le prove Azurite verificano l'operazione, **non** l'assegnazione RBAC della managed identity o l'effettiva granularità SAS in Azure.
- **Demo locale**: `Storage__Readiness__AllowSharedDevelopmentCredentials=true` è ammessa *solo* con `ASPNETCORE_ENVIRONMENT=Development`. Condivide credenziali note in Docker Compose e non va copiata in on-prem o Azure.

La CI verifica la configurazione fail-closed, i metadati S3 e Azure su SeaweedFS/Azurite reali, e in Compose l'arresto/ripristino di Keycloak e SeaweedFS nonché l'arresto di ClamAV: readiness diventa 503 mentre liveness resta 200.

**Limiti:** la riuscita di `HeadBucket`/`GetContainerProperties` non dimostra possibilità di upload, permessi di scrittura/lettura dei byte, correttezza dei documenti, integrità cross-servizio o latenza sostenuta. Sono ancora aperti prove AWS IAM e Azure managed identity reali, freschezza/rotazione JWKS e firme ClamAV, worker recovery, metriche e backlog, multi-replica, rate limiting e alerting. Non considerare `/health/ready` una certificazione di produzione né usare `/health/live` come gate di traffico.
