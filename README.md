# Peritial — clean rebuild

La vecchia app è conservata in death come documentazione funzionale. Sviluppo soltanto su main, senza merge.

Backend .NET 10 DDD (Domains / Business / Infrastructures / Applications), API REST e Model Context Protocol con OAuth; frontend React/TypeScript PWA con shadcn-style components, i18n (it/en), dark/light. PostgreSQL + Azure Blob o SeaweedFS (S3); Keycloak locale, Entra ID Azure.

## Esecuzione e test
- docker compose -f docker/compose.yml up --build -d
- PWA: http://localhost:5173
- API: http://localhost:8080/health/live
- MCP: http://localhost:8080/mcp
- docker compose -f docker/compose.yml --profile test run --rm tests
- cd src/frontend && npm ci && npx playwright install chromium && npm run test:e2e

**Stato:** primo vertical slice perizie/ingestion, con E2E OAuth Keycloak, API REST e catalogo MCP verificati; NON è una riscrittura funzionalmente completa di death. Configurazione e credenziali Compose solo dimostrative, non per produzione.

Documenti: docs/requirements.md, docs/legacy-inventory.md, docs/architecture.md, docs/mcp.md, docs/development.md, docs/local-deployment.md, docs/database-migrations.md, docs/database-backup-restore.md (adozione legacy V001 o pre-lease solo offline), adr/.

La verifica offline dei riferimenti PostgreSQL↔SeaweedFS S3 con digest SHA-256 è documentata in `docs/object-recovery-checkpoint.md` (ADR 0019): il manifest **non contiene il backup dei byte** e non sostituisce snapshot/coordinamento cross-sistema.

Recovery offline: `docs/object-recovery-checkpoint.md` (checkpoint + export/import HMAC-SHA256 dei byte su nuovo SeaweedFS con chiave esterna; laboratorio con PostgreSQL reale, non backup produttivo coordinato), ADR 0020 e 0021.

Il bundle offline **PostgreSQL + S3 cifrato AES-256-GCM** (con chiavi indipendenti da secret manager) è descritto in `docs/sealed-multiservice-recovery.md`, ADR 0022. È un drill di laboratorio: backup on-prem/Azure gestiti, Keycloak e offsite restano aperti.
