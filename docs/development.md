# TDD e processo

Ogni use case: test rosso (xUnit / Playwright), implementazione minima, test verde, refactoring, aggiornamento docs/ e adr/.

- Backend completo: docker compose -f docker/compose.yml --profile test run --rm tests
- Frontend: cd src/frontend && npm install && npx playwright install chromium && npm run build && npm run test:e2e
- CI: xUnit unitari e con PostgreSQL reale, build frontend, Playwright e smoke Docker Compose.
- Lavorare solo su main. Non fondere death.
- Debiti bloccanti produzione: lockfile npm e npm ci, scansione antivirus/validazione magic bytes, limitazione chiamate, audit completo, idempotenza, backup/migrazioni, provisioning e rotazione dei privilegi RLS in produzione, test OAuth/MCP su client reali, deploy automatizzato su Azure, secret vault.

La build iniziale CI ha guidato la correzione delle dipendenze e la scelta dell'immagine S3 open-source disponibile sul registry.

La CI Docker Compose esegue ora un test Playwright autenticato reale (Keycloak, creazione perizia, tool MCP) sia desktop sia mobile, e fallisce su errori OAuth o sulle chiamate non autorizzate. Dopo il ciclo Red→Green si eliminano stampe temporanee di pagine e credenziali dai test (Refactor).

## Verifica OAuth/MCP

`tests/frontend/security.spec.ts` effettua test negativi reali contro il realm Keycloak demo in Docker Compose: discovery RFC 9728, assenza `tenant_id`, audience errata, scope insufficiente, `401` vs `403`, tool MCP non autorizzati. I client Keycloak `peritial-test-*` con password grant servono **esclusivamente** per i test isolati locali, non per i client reali. La PWA continua con Authorization Code + PKCE. Vedere ADR 0007.
