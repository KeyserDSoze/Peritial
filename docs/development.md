# TDD e processo

Ogni use case: test rosso (xUnit / Playwright), implementazione minima, test verde, refactoring, aggiornamento docs/ e adr/.

- Backend completo: docker compose -f docker/compose.yml --profile test run --rm tests
- Frontend: cd src/frontend && npm ci && npx playwright install chromium && npm run build && npm run test:e2e
- CI: xUnit unitari e con PostgreSQL reale, build frontend, Playwright e smoke Docker Compose.
- Lavorare solo su main. Non fondere death.
- Debiti bloccanti produzione: pin digest delle immagini e supply-chain, quarantena persistente e signature freshness dello scanner, limitazione chiamate, audit completo, idempotenza, backup/restore verificati e adozione legacy delle migrazioni, provisioning e rotazione dei privilegi RLS in produzione, test OAuth/MCP su client reali, deploy automatizzato su Azure, secret vault.

La build iniziale CI ha guidato la correzione delle dipendenze e la scelta dell'immagine S3 open-source disponibile sul registry.

La CI Docker Compose esegue ora un test Playwright autenticato reale (Keycloak, creazione perizia, tool MCP) sia desktop sia mobile, e fallisce su errori OAuth o sulle chiamate non autorizzate. Dopo il ciclo Red→Green si eliminano stampe temporanee di pagine e credenziali dai test (Refactor).

## Verifica OAuth/MCP

`tests/frontend/security.spec.ts` effettua test negativi reali contro il realm Keycloak demo in Docker Compose: discovery RFC 9728, assenza `tenant_id`, audience errata, scope insufficiente, `401` vs `403`, tool MCP non autorizzati. I client Keycloak `peritial-test-*` con password grant servono **esclusivamente** per i test isolati locali, non per i client reali. La PWA continua con Authorization Code + PKCE. Vedere ADR 0007.

## Test audit

SecurityAuditUseCaseTests verifica tenant, subject, azione ed esito senza riferimenti o payload. SecurityAuditRepositoryTests usa PostgreSQL reale e il ruolo INSERT-only per verificare RLS. La CI Docker Compose verifica audit realmente persistito dopo REST e MCP. Le fixture DDL/ruoli PostgreSQL usano un advisory lock per impedire corse del catalogo tra test paralleli. Vedere docs/audit.md.

## Transazioni atomiche audit/metadata (#21)

PeriziaAtomicAuditTests prova su PostgreSQL reale che INSERT/UPDATE perizie e audit si committano insieme, che un audit duplicato annulla CREATE o UPDATE, e che gli eventi cross-tenant non modificano dati. DocumentAtomicAuditTests applica gli stessi controlli ai metadata dei file. Tutte le mutazioni REST/MCP usano i metodi AddAuditedAsync/UpdateAuditedAsync delle porte Business; il ruolo DB resta soggetto a FORCE RLS. La lettura degli oggetti esterni e la loro compensazione in caso di errore restano fuori dalla transazione PostgreSQL: ADR 0008 e issue #5/#21.

## Fault injection documentale (#5/#21)

FileIngestionCompensationTests esercita lo storage fake con upload parziale, errore metadata dopo PUT, cleanup fallito, cancellazione HTTP a metà commit, retry dopo errore e successo senza cancellazione. L'adapter S3 e quello Azure implementano DeleteAsync, con cancellazione idempotente. Le prove reali contro entrambi i provider e il recupero persistente dopo crash non sono ancora completati: ADR 0009.

I test in Peritial.Infrastructures.Tests riutilizzano un database PostgreSQL condiviso: oltre al lock di bootstrap SQL, la suite xUnit è serializzata a livello di assembly. Il lock da solo non impedisce che DDL del test successivo interferisca con la transazione RLS di un test in esecuzione. La serializzazione riguarda solo l'assembly di integrazione, non i test Business o Domain.

## Scanner documentale (#5)

L'API richiede `Malware:Clamd:Host` e porta TCP privata (default 3310), con timeout configurabile 1–120 s. Nessun fallback permissivo: `IFileMalwareScanner` restituisce `Clean` solo per la risposta esatta `stream: OK`. In CI `MalwareAdmissionTests`, `ClamdScannerContractTests` e Playwright EICAR verificano che i file infetti/non verificati non raggiungano la persistenza. Per produzione, gestire update firme e quarantena persistente: ADR 0010.

## Upload recovery con intenzioni durevoli (#21)

`UploadIntentOrderingTests` verifica che l'intento sia persistito prima di PUT e che un errore metadata non avvii una cancellazione rischiosa. `UploadRecoveryTests` usa PostgreSQL reale e ruoli separati: crash simulato, cleanup ritentato dopo errore storage, preservazione blob referenziato quando il commit ACK è incerto, consumo atomico dell'intento con metadata+audit, rispetto del grace period e accessi recovery a colonne ristrette. In CI il job Compose controlla che, dopo Playwright REST/MCP, non rimangano intenti pendenti. Le prove reali di kill/restart multi-replica e l'idempotenza HTTP restano aperte (#21).

## E2E idempotenza upload (#21)

`UploadIdempotencyTests` usa PostgreSQL reale con richieste concorrenti, identità separate, payload mismatch, replay con audit unico, rilascio su errore e worker recovery. `UploadFingerprintTests` verifica il digest di contenuto e metadati. Nel test Compose Playwright usa la medesima chiave in REST upload, retry REST e MCP ingest_file: stesso UUID, nessuna seconda riga audit; un payload differente riceve 409. L'asserzione SQL CI verifica due documenti/azioni di audit per i due browser, non sei eventi da retry.

## Upload lease e prova riavvio reale (#21)

`UploadLeaseTests` prova FORCE RLS e lease valida/scaduta usando PostgreSQL; `UploadLeaseHeartbeatTests` prova il rinnovo periodico durante uno storage PUT bloccato e l'annullamento fail-closed se non è più possibile estendere il lease. Nel job Compose `UploadRestartContractTests` ha due fasi: `docker compose kill --signal SIGKILL api`, seed di un oggetto reale SeaweedFS e di un intento PostgreSQL scaduto mentre API è ferma, poi `docker compose start api`, polling finché l'intento non viene rimosso e verifica finale che il blob non esista e che la key idempotente sia stata liberata. Nessun reset dei volumi per il recovery. Non equiparare questa prova a un HTTP upload spezzato *durante* PUT. Vedi ADR 0013.

## Prova reale mid-PUT con proxy di caos (ADR 0014)

Il job Compose esegue `bash tests/chaos/run_mid_put.sh` dopo le normali prove OAuth/MCP, storage e scanner. Un file PDF di test viene caricato con token Keycloak firmato e idempotency key locale; l'API usa il normale adapter AWS SDK ma punta temporaneamente a un **proxy TCP S3 di test**. Il proxy inoltra l'intestazione e un prefisso del **corpo HTTP PUT** al SeaweedFS reale, segnala l'avvenuto trasferimento e blocca il flusso. La pipeline verifica sia il segnale sia che la chiamata upload non sia terminata, invia `SIGKILL` all'API, fa scadere l'intento del processo morto, riavvia l'API standard e controlla che il worker abbia eliminato intent e chiave idempotente. Un test .NET verifica inoltre con SDK S3 autenticato il 404 del blob, l'assenza del documento e di audit di successo. Il file `docker/compose.chaos.yml` non va usato in produzione; la prova NON rappresenta Azure Blob, multi-replica o timeout DB reali.

## Migrazioni PostgreSQL versionate (#6, ADR 0015)

`SchemaMigrationTests` prova installazione V001/V002, re-run, drift checksum, schema preesistente non tracciato e concorrenza in PostgreSQL reale. Il migrator esplicito `scripts/db/Peritial.DbMigrator` non parte insieme all'API e richiede credenziali DDL indipendenti. Per il runbook e i limiti della demo Compose consultare `docs/database-migrations.md`. Non equiparare i test di schema nuovo a una prova di upgrade/restore del database legacy.
