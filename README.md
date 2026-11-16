# Peritial — clean rebuild

La vecchia app è conservata in death come documentazione funzionale. Sviluppo soltanto su main, senza merge.

Backend .NET 10 DDD (Domains / Business / Infrastructures / Applications), API REST e Model Context Protocol con OAuth; frontend React/TypeScript PWA con shadcn-style components, i18n (it/en), dark/light. PostgreSQL + Azure Blob o SeaweedFS (S3); Keycloak locale, Entra ID Azure.

## Esecuzione e test
- docker compose -f docker/compose.yml up --build -d
- PWA: http://localhost:5173
- API liveness: http://localhost:8080/health/live ; readiness parziale PostgreSQL + ClamAV + OIDC + storage S3/Azure: http://localhost:8080/health/ready
- Metriche/trace readiness privacy-safe tramite `Peritial.Readiness` (OTLP opt-in, disabilitato di default; collector **effimero** disponibile solo con overlay Compose, senza dashboard/alert): [runbook](docs/readiness-telemetry.md).
- MCP: http://localhost:8080/mcp
- docker compose -f docker/compose.yml --profile test run --rm tests
- cd src/frontend && npm ci && npx playwright install chromium && npm run test:e2e

**Stato:** primo vertical slice perizie/ingestion, con E2E OAuth Keycloak, API REST e catalogo MCP verificati; NON è una riscrittura funzionalmente completa di death. Configurazione e credenziali Compose solo dimostrative, non per produzione.

**Catalogo documentale (#11/#12):** oltre all'upload controllato, REST `GET /api/files` (opzionale `periziaId`, `limit` da 1 a 100) e `GET /api/files/{id}`, più tool MCP `list_documents`/`get_document`. Le risposte espongono solo metadati del tenant firmato, mai chiavi di storage o byte; accesso `peritial.read` o `peritial.write`. La PWA mostra gli ultimi 50 metadati in consultazione dopo l'upload, senza link a oggetti né download; può filtrare per perizia tramite GET tenant-scoped sul server (non limitandosi ai 50 già caricati). Il form può associare facoltativamente un file a una perizia presente nell'elenco autenticato; la verifica del tenant resta lato server. **Download autenticato, paginazione oltre 100, versioni, report e ricerca UI completa restano da implementare.** [Contratto e limiti](docs/mcp.md).

**Offline PWA (#13):** la shell installabile può riaprirsi con asset statici già in cache, ma non offre accesso offline alle pratiche: nasconde i dati tenant, non mette in coda mutazioni e richiede un nuovo fetch autorizzato alla riconnessione. Workbox non salva API/MCP; il backend marca le risposte protette `Cache-Control: no-store`. Tema e lingua sono le sole preferenze locali salvate. [Dettagli, limiti e test](docs/development.md).

**Sinistri (#7):** modello DDD `Sinistro` e persistenza PostgreSQL additiva **V003** con `ENABLE/FORCE RLS`, versionamento CAS e repository a scritture soltanto auditate (record + audit nello stesso commit), verificati su PostgreSQL reale con due tenant. **Non esistono ancora CRUD REST/MCP, use case Business, collegamento anagrafiche/perizie né UI.** La tabella V003 viene creata **solo dal migrator esplicito**, non dal bootstrap Compose V001. [Dettagli e limiti](docs/sinistri-domain.md).

Documenti: docs/requirements.md, docs/legacy-inventory.md, docs/architecture.md, docs/mcp.md, docs/development.md, docs/sinistri-domain.md, docs/local-deployment.md, docs/database-migrations.md, docs/database-backup-restore.md (adozione legacy V001 o pre-lease solo offline), adr/.

La CI fissa tutte le GitHub Actions a commit approvati, con controllo fail-closed indipendente su `.github/workflows/` e test di regressione: [policy e procedura di aggiornamento](docs/ci-action-pinning.md) (#6). Anche le quattro immagini base nei Dockerfile backend/frontend sono bloccate su digest OCI validati nella CI amd64: [digest, test e limiti multi-arch](docs/oci-base-image-pinning.md). Sono inoltre bloccate sei occorrenze OCI di **PostgreSQL e Prometheus** nei file CI/Compose, con test di regressione e provenienza documentata nello stesso [runbook](docs/oci-base-image-pinning.md). La CI usa inoltre un runner `ubuntu-26.04` e versioni Node/.NET SDK esatte, con gate avversariale ([policy CI](docs/ci-action-pinning.md)); il build della VM GitHub può comunque variare. Restano non fissati numerosi altri servizi Compose, immagini CI, toolchain e dipendenze: il gate non certifica la supply chain completa.

La verifica offline dei riferimenti PostgreSQL↔SeaweedFS S3 con digest SHA-256 è documentata in `docs/object-recovery-checkpoint.md` (ADR 0019): il manifest **non contiene il backup dei byte** e non sostituisce snapshot/coordinamento cross-sistema.

Recovery offline: `docs/object-recovery-checkpoint.md` (checkpoint + export/import HMAC-SHA256 dei byte su nuovo SeaweedFS con chiave esterna; laboratorio con PostgreSQL reale, non backup produttivo coordinato), ADR 0020 e 0021.

Il bundle offline **PostgreSQL + S3 cifrato AES-256-GCM** (con chiavi indipendenti da secret manager) è descritto in `docs/sealed-multiservice-recovery.md`, ADR 0022. È un drill di laboratorio: backup on-prem/Azure gestiti, Keycloak e offsite restano aperti.

Il recovery cifrato richiede ora **staging temporaneo Linux `tmpfs` privato, senza swap** prima di `pg_dump`/export e dopo l'unseal; workflow completo e limiti: `docs/sealed-multiservice-recovery.md`, ADR 0023. Non è un backup automatico o certificato per produzione.

Il bundle AES-256-GCM può ora essere replicato **solo cifrato** su uno storage S3 indipendente, con inventario autenticato e completamento verificato (ADR 0024). Il test usa una terza istanza SeaweedFS e non costituisce un servizio di backup offsite, retention o KMS di produzione; vedere `docs/sealed-multiservice-recovery.md`.

La replica cifrata verso un vero S3 richiede ora **Object Lock COMPLIANCE con versioning e almeno 30 giorni** (gate fail-closed, ADR 0025). Il server locale SeaweedFS della CI usa solo un bypass dimostrativo esplicito: **non** è un deposito WORM/backup di produzione. Runbook in `docs/sealed-multiservice-recovery.md`.

Il **sesto job CI `worm-recovery`** verifica ora S3 Object Lock COMPLIANCE con un provider compatibile che lo implementa (PGSTY SILO, immagine per digest): la cancellazione di una versione protetta viene respinta, mentre un controllo non protetto è cancellabile. Restano aperti AWS S3/IAM, restore delle versioni storiche come insieme, KMS e retention offsite produttiva (ADR 0026).

È disponibile anche `pull-historic` per il **recupero esplicito dell'intero bundle cifrato da VersionId S3 COMPLIANCE selezionati** in caso di delete marker, senza rimuoverli o pubblicare dati parziali (ADR 0027). Richiede conferma separata e inventario di versioni completo; non seleziona versioni automaticamente né sostituisce KMS/offsite di produzione.

La CI WORM usa ora **due identità S3 distinte e limitate al prefisso cifrato**: uploader senza alcun GET (le letture sono affidate al reader separato), e reader corrente/storico senza PUT/DELETE. Il provider SILO può mostrare i VersionId al writer sotto `ListBucket`; IAM gestito AWS ancora da verificare. Modelli IAM riproducibili in `scripts/recovery/render_worm_iam.py` e [runbook](docs/sealed-multiservice-recovery.md) (ADR 0028); restano da completare IAM role/STS, KMS e verifiche AWS in produzione.
