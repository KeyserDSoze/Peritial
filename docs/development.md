# TDD e processo

Ogni use case: test rosso (xUnit / Playwright), implementazione minima, test verde, refactoring, aggiornamento docs/ e adr/.

- Backend completo: docker compose -f docker/compose.yml --profile test run --rm tests
- Frontend: cd src/frontend && npm install && npx playwright install chromium && npm run build && npm run test:e2e
- CI: xUnit unitari e con PostgreSQL reale, build frontend, Playwright e smoke Docker Compose.
- Lavorare solo su main. Non fondere death.
- Debiti bloccanti produzione: lockfile npm e npm ci, quarantena persistente e signature freshness dello scanner, limitazione chiamate, audit completo, idempotenza, backup/migrazioni, provisioning e rotazione dei privilegi RLS in produzione, test OAuth/MCP su client reali, deploy automatizzato su Azure, secret vault.

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
