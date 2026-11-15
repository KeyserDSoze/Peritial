# Esecuzione locale

Prerequisiti: Docker Engine, Docker Compose v2.

- Avvio: docker compose -f docker/compose.yml up --build -d
- PWA http://localhost:5173 ; REST http://localhost:8080/api/perizie ; MCP http://localhost:8080/mcp
- Health: http://localhost:8080/health/live
- Keycloak: http://localhost:8081 ; SeaweedFS S3: http://localhost:8333
- Utente solo locale: localuser / local-only-user; admin Keycloak locale: localadmin / local-only-admin.
- Test backend PostgreSQL reale: docker compose -f docker/compose.yml --profile test run --rm tests
- Installazione frontend riproducibile: `cd src/frontend && npm ci` (richiede Node.js 22 e il `package-lock.json` versionato); non usare `npm install` per build/CI di rilascio.

ATTENZIONE: i segreti dimostrativi e il singolo tenant hardcoded nel realm Keycloak sono esclusivamente per il computer di sviluppo. Non distribuire questa configurazione in produzione. Servono HTTPS, provider OAuth e claim tenant reali, secret manager, backup, migrazioni versionate, scansione file e security tests.

## Login OAuth end-to-end

Il browser e i token usano `http://localhost:8081/realms/peritial`, l'issuer configurato in Keycloak. Per l'API, che gira in un container, `localhost` indica il container stesso: solo nell'ambiente Development il `OidcDevelopmentBackchannelHandler` reindirizza le richieste di retrieval JWKS verso `http://keycloak:8080`, lasciando intatto l'issuer e la validazione di firma, audience e scadenza.

Le richieste di metadata OpenID partono dall'indirizzo interno configurato in `Auth:MetadataAddress`. Il rewrite è rifiutato in Production.

Test E2E reale: `E2E_FULL_STACK=1 E2E_BASE_URL=http://localhost:5173 npm run test:e2e` da `src/frontend` dopo avvio Compose. Questa verifica fa parte della CI Compose.

In produzione usare sempre OIDC HTTPS raggiungibile sia dai client che dall'applicazione, e vietare i rewrite di sviluppo.

Il realm dimostrativo importa intenzionalmente soltanto gli scope `peritial.read`, `peritial.write` e il meta-scope OIDC `openid`; `profile`/`email` non sono disponibili in questa configurazione. Il frontend richiede soltanto gli scope supportati per default; in altri ambienti personalizzare `VITE_OIDC_SCOPES` durante la build.

## Database multi-tenant con RLS

Il database viene inizializzato con `scripts/db/001_init.sql` (tabelle e policy `ENABLE/FORCE RLS`) e, **solo nella configurazione locale**, con `scripts/db/002_dev_app_role.sql` per l'account `peritial_app` a password di esempio. L'API usa il ruolo applicativo ristretto, non il superuser `peritial`. `Database:Bootstrap` resta disattivato nell'API.

Dopo upgrade di un volume PostgreSQL preesistente, gli script in `docker-entrypoint-initdb.d` **non vengono rieseguiti**. Applicare lo schema e il provisioning del ruolo da un amministratore con backup prima dell'upgrade; cancellare i volumi solo se contengono dati effimeri. Il nuovo runner versionato per **database vuoti** è documentato in `docs/database-migrations.md` (ADR 0015); l'adozione di volumi persistenti già inizializzati è tuttora bloccata in sicurezza e resta nella issue #6.

## Scansione antivirus locale

Compose avvia un demone ClamAV `clamd` con immagini/firme pre-caricate, connessione TCP solo interna (`clamav:3310`) e health check obbligatorio prima dell'avvio dell'API. Non è pubblicata alcuna porta scanner. I file PDF/JPG/PNG/XML/DOCX validati sono analizzati in memoria con ClamAV **prima** di raggiungere SeaweedFS/PostgreSQL; se ClamAV non è disponibile il caricamento è bloccato (503), un EICAR viene rifiutato (422). La demo disattiva `freshclam` per evitare aggiornamenti esterni non deterministici in CI: non replicare questa configurazione in produzione. L'immagine ufficiale può richiedere una quantità significativa di RAM. Quarantena persistente, worker di recovery e aggiornamento firme sono ancora aperti (#5/#21).

## Recupero durevole degli upload locali (#21)

Lo schema crea `upload_intents` e due login **distinti** solo in sviluppo: `peritial_app` per le richieste e `peritial_recovery` per il worker. `UploadRecovery__ConnectionString` deve usare il secondo ruolo con privilegi ristretti, mai le credenziali amministrative. Il worker controlla gli intenti più vecchi di un'ora una volta al minuto (max 20 per ciclo), usa lock PostgreSQL per evitare doppi cleanup e verifica che non esista già un documento referenziante. In dev si può controllare *solo il conteggio* dei pendenti con `docker compose -f docker/compose.yml exec -T postgres psql -U peritial -d peritial -Atc 'SELECT count(*) FROM upload_intents;'`. Un volume preesistente richiede migrazione e aggiornamento del ruolo prima di avviare l'API: non eliminare volumi contenenti dati reali.

## Lease e restart del worker

Il bootstrap locale è compatibile con schema preesistente e aggiunge `lease_expires_at_utc` a `upload_intents`, con grant `UPDATE (lease_expires_at_utc)` riservato al ruolo applicativo. Prima dell'upgrade di una istanza con volume persistente, usare una migrazione verificata e backup del DB, senza cancellare i dati: la base delle migrazioni versionate esiste per DB nuovi, ma la baseline verificata e l'upgrade dei volumi legacy non sono ancora supportati (#6; `docs/database-migrations.md`). In produzione monitorare rinnovi falliti e backlog del worker, non ridurre l'ora di grazia senza verificare PUT lunghi. In CI Compose viene eseguito un riavvio forzato SIGKILL con seed dell'orfano per verificare la persistenza.

## Fault injection interruzione PUT (#21)

La pipeline CI usa un overlay Compose separato, `docker/compose.chaos.yml`, che instrada il PUT S3 tramite un proxy Python capace di inoltrare i primi byte e bloccare il trasferimento. Solo dopo il segnale di byte inoltrati viene inviato SIGKILL al processo API; poi la stack standard viene riavviata ed è verificata la pulizia PostgreSQL/SeaweedFS. L'overlay è esclusivamente di **test**, non abilitarlo su installazioni operative e non esporre il proxy. ADR 0014.
