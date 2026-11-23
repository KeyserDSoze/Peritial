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

**Catalogo documentale (#11/#12):** oltre all'upload controllato, REST `GET /api/files` (opzionale `periziaId`, `limit` da 1 a 100) e `GET /api/files/{id}`, più tool MCP `list_documents`/`get_document`. Le risposte espongono solo metadati del tenant firmato, mai chiavi di storage o byte; accesso `peritial.read` o `peritial.write`. La PWA mostra gli ultimi 50 metadati in consultazione dopo l'upload, senza link a oggetti né download; può filtrare per perizia tramite GET tenant-scoped sul server (non limitandosi ai 50 già caricati). Il form può associare facoltativamente un file a una perizia presente nell'elenco autenticato; la verifica del tenant resta lato server. **Download REST autenticato disponibile in prima tranche:** `GET /api/files/{id}/download` richiede JWT `peritial.read`/`peritial.write`, verifica tenant via PostgreSQL FORCE RLS, legge esattamente i byte attesi (max 25 MiB) da S3/Azure Blob privati, registra audit prima della risposta e restituisce solo un allegato `application/octet-stream` con `no-store`/`nosniff`. **Non è ancora disponibile il download nella PWA né il trasporto binario MCP**; restano rate limiting, quarantena/retention, verifica cloud reale, versioni, report e ricerca UI completa. [ADR 0094](adr/0094-tenant-scoped-audited-document-download.md). [Contratto e limiti](docs/mcp.md).

**Offline PWA (#13):** la shell installabile può riaprirsi con asset statici già in cache, ma non offre accesso offline alle pratiche: nasconde i dati tenant, non mette in coda mutazioni e richiede un nuovo fetch autorizzato alla riconnessione. Workbox non salva API/MCP; il backend marca le risposte protette `Cache-Control: no-store`. Tema e lingua sono le sole preferenze locali salvate. [Dettagli, limiti e test](docs/development.md).

**Sinistri (#7):** modello DDD `Sinistro`, persistenza additiva **V003** con `ENABLE/FORCE RLS` e CAS, e primo Business `SinistroService` condiviso da REST e MCP: **elenco, dettaglio, creazione e modifica** con `expectedVersion` obbligatoria. Le scritture e l'audit sono atomici; i permessi OAuth sono `peritial.read/write` e i DTO non espongono tenant ID. **Ora disponibile anche la ricerca REST/MCP per prefisso letterale case-insensitive (2–100 caratteri), filtri inclusivi sulle date evento e cap 50, lato SQL prima di LIMIT.** **Catalogo Sinistri paginato:** REST `GET /api/sinistri/page?limit=50&cursor=` e tool MCP `page_sinistri` restituiscono `items` e `nextCursor` senza OFFSET e senza tenant ID, per navigare oltre 50 risultati nel catalogo PWA tramite «Carica altri sinistri». La ricerca per prefisso/date è ora paginata tramite `GET /api/sinistri/search/page` e `search_sinistri_page`, con cursore legato al filtro, senza modificare l'autocomplete esistente ([ADR 0040](adr/0040-sinistri-filtered-keyset-search.md)). **Collegamento Perizie–Sinistri V004:** `POST /api/perizie/{id}/sinistro` e `GET /api/sinistri/{id}/perizie`, più tool MCP equivalenti, con FK composta tenant-scoped, CAS e audit atomico. Una Perizia può essere associata una sola volta per operazione; lo **scollegamento esplicito** richiede `expectedSinistroId` e audit atomico ([ADR 0043](adr/0043-perizia-sinistro-explicit-unlink.md)). La **riassegnazione atomica** REST/MCP/PWA richiede sia il Sinistro osservato sia il target distinto dello stesso tenant; è vietata per Perizie fatturate, duplicate e annullate e non esegue mai un unlink intermedio ([ADR 0044](adr/0044-perizia-sinistro-atomic-reparent.md)). La **PWA offre un selettore Sinistro e lo scollegamento confermato per ogni Perizia**, con ricerca server-side on-demand, lettura del link persistito, conferma, gestione del conflitto 409 senza replay e refetch dopo il commit. Il catalogo Sinistri include inoltre **ricerca esplicita via REST per prefisso letterale e intervallo inclusivo di date evento, ora con pagine da massimo 50 risultati PostgreSQL**, refresh del filtro e reset alla prima pagina del catalogo completo; le ricerche non si limitano alle righe caricate nel browser ([ADR 0038](adr/0038-pwa-sinistri-server-search.md)). Offre inoltre un **catalogo Sinistri apribile su richiesta con lista tenant-scoped (50 voci), creazione e modifica CAS controllata** per utenti con scope di scrittura. L'editor rilegge il record prima del PUT con `expectedVersion`, blocca il salvataggio sul conflitto 409 e richiede un nuovo GET firmato senza sovrascrivere modifiche concorrenti. Nessuna coda offline o successo ottimistico. REST/MCP espongono anche `GET /api/perizie/{id}/sinistro` / `get_perizia_sinistro`. È ora disponibile anche il **DELETE REST/MCP auditato con CAS** per Sinistri **senza Perizie collegate**, via V005 (FK RESTRICT); retention/legal hold e pulsante UI restano da definire ([ADR 0041](adr/0041-sinistri-v005-audited-cas-delete.md)). **Sicurezza hard-delete (#22): disabilitata per default e in tutti gli ambienti non-Development**, richiede entrambi gli scope `peritial.write` e `peritial.sinistri.delete` e un flag esplicito riservato alla demo/CI. Client PWA e writer ordinari non dispongono dello scope distruttivo. Il rollout produttivo resta bloccato in attesa di retention/legal hold/approvazioni ([ADR 0042](adr/0042-sinistri-delete-development-gate.md)). Restano mancanti ricerca avanzata/full-text, indice ottimizzato per grandi cataloghi, relazioni anagrafiche/polizze, reparent con approval/policy estese, import e CRUD Sinistri completo nella PWA, dunque la #7 non è completata. V003, V004 e V005 richiedono **migrazione amministrativa esplicita** su database versionati: Compose demo V001 non le applica; solo la CI temporanea predispone le fixture additive V003, V004 e V005 per test end-to-end. [Contratti, prove e limiti](docs/sinistri-domain.md).

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

## Perizia–Sinistro V006: conflitti ABA e provenienza append-only (#23)

La [migrazione V006](scripts/db/migrations/V006__perizie_link_revision_history.sql)
aggiunge `perizie.sinistro_link_version` (baseline `1` sul preesistente)
e `perizia_sinistro_history` append-only con revisione, Sinistro
precedente/successivo e riferimento all'audit atomico. GET associazione
restituisce `linkVersion`; **POST link, PUT reparent e DELETE unlink
richiedono `expectedLinkVersion` positivo** in aggiunta agli ID
precedentemente richiesti. Ogni successo incrementa la revisione e la
restituisce; le chiamate vecchie senza versione ricevono **400**
(fail-closed), una versione obsoleta **409**, incluso un ciclo ABA
A→B→A. La PWA invia sempre la revisione firmata dal server e richiede
reload dopo conflitti; lo stato di workflow terminale continua a vietare
la riassegnazione. Schema versionato da migrare esplicitamente, Compose
demo V001 resta storico, CI effimera applica fixture V003–V006.
Per la provenienza precedente all'upgrade esiste solo una baseline
(cannot retroactively reconstruct earlier changes); policy privacy,
retention e approvazioni finali ancora aperte in #23.
[ADR 0045](adr/0045-perizia-sinistro-v006-aba-revision-provenance.md).

## Integrità database dei collegamenti (V007, #23)

Un nuovo controllo PostgreSQL **rifiuta modifiche dirette**
alle associazioni Perizia–Sinistro senza revisione monotona,
audit di successo e storia source/target coerente al commit.
La V007 aggiunge trigger di verifica differita e nessuna
nuova API pubblica, preservando la semantica REST/MCP/PWA
della V006. Il release richiede migrazione amministrativa
esplicita e backup verificato; Compose CI predispone
V003–V007 soltanto su DB temporaneo.
[ADR 0046](adr/0046-v007-database-enforced-link-provenance.md).

## Perizie: privilegio DELETE rimosso (V008, #6/#4)

Il ruolo applicativo non ha più il privilegio SQL
`DELETE` su `perizie`: nessun dato viene cancellato
dalla migrazione. Restano intatti link, workflow,
audit e storia. La modifica chiude una via diretta
per bypassare i controlli V007, mentre qualsiasi
cancellazione Perizia futura necessita di un proprio
workflow governato. La nuova release schema richiede
la **V008** e la CI effimera applica V003–V008.
[ADR 0047](adr/0047-v008-revoke-perizia-delete.md).

## Documenti: metadati immutabili per l'app (V009, #5/#6/#11)

La [migrazione V009](scripts/db/migrations/V009__documents_revoke_app_mutations.sql)
rimuove `UPDATE` e `DELETE` diretti su `documents`
dal ruolo `peritial_app` e da `PUBLIC`, senza toccare
record o oggetti. Ingestione e catalogo mantengono rispettivamente
`INSERT` e `SELECT` tenant-scoped. PostgreSQL ne verifica i
privilegi e nega SQL non auditato; la CI Compose effimera
applica V003→V009, mentre le installazioni reali richiedono
il migrator amministrativo esplicito e backup verificato.
Restano aperti ciclo vita metadata, retention, trasporto byte MCP e
pieno rollout controllato, quarantena e recovery in produzione;
la route REST autenticata è in ADR 0094, mentre la PWA offre un
download Bearer in memoria delimitato e annullabile (ADR 0095).
[ADR 0048](adr/0048-v009-immutable-document-metadata-privileges.md).

## Catalogo documenti paginato (ADR 0049, #11/#12/#13)

`GET /api/files/page` e il tool MCP `page_documents`
consentono di percorrere tutto lo storico con pagine da 1–50
metadati, filtro `periziaId` e `nextCursor` opaco vincolato
al filtro. PostgreSQL usa seek ordinato per data creazione
e UUID decrescenti con tenant FORCE RLS. La PWA carica altre
pagine soltanto su richiesta e svuota i dati su cambio JWT,
filtro, offline o errore. `GET /api/files`/`list_documents`
restano retrocompatibili. Il cursore non è un token di accesso
né uno snapshot; la paginazione ADR 0049 è solo metadata.
Il download REST delimitato (ADR 0094) e la sua UI PWA (ADR 0095)
non alterano i cursori dei metadati; sono richieste GET separate,
autenticate e mai cacheate offline. DELETE resta escluso.
[ADR 0049](adr/0049-document-catalog-keyset-pages.md).

## V010: indici ordinati per il catalogo documenti (#11/#6)

La [V010](scripts/db/migrations/V010__documents_catalog_keyset_indexes.sql)
introduce due B-tree per seek tenant e tenant+Perizia ordinati per
`created_at_utc DESC,id DESC`. Il repository usa
un confronto tuple quando `nextCursor` è presente, senza
cambiare API REST/MCP o dati. L'upgrade segue il migrator
versionato con backup e maintenance: `CREATE INDEX`
non-concurrente può bloccare le scritture. Non sono
ancora attestati benchmark reali e piani a scala produttiva.
[ADR 0050](adr/0050-v010-document-keyset-indexes.md).

## Preview XML Classic/Accertatore senza importazione (#10)

La prima tranche del porting degli importatori legacy è
un preflight **read-only** e senza side effect:
`POST /api/imports/xml/preview` (`Content-Type: application/xml`
o `text/xml`) e MCP `preview_legacy_xml(xml)`.
Serve un JWT firmato con `peritial.write`, anche per MCP.

Sono riconosciute le radici esatte Classic,
Classic Base e Accertatore; il parser valida
`NumeroSinistro` e `DataOraSinistro`, riconosce
`DataIncarico` facoltativa e conta al massimo
128 allegati senza estrarre o decodificare alcun file.
Il payload XML è limitato a **1 MiB**, DTD/XXE sono
rifiutati e la risposta contiene solo
`format`, `externalReference`, `occurredOn`,
`reportedOn`, `attachmentCount`.
Nessun XML completo, persona, allegato, documento o
chiave di storage viene restituito, scritto o loggato.
L'anteprima **non crea Sinistri/Perizie**, non esegue
upsert di anagrafiche e **non equivale a un malware scan**.
Importazione effettiva, idempotenza e allegati sicuri
restano nella issue #10. [ADR 0051](adr/0051-legacy-xml-safe-preview.md).

## Piano di riconciliazione XML Classic/Accertatore (#10)

Oltre alla preview, `POST /api/imports/xml/plan` e MCP
`plan_legacy_xml(xml)` eseguono un **dry-run** con
`peritial.write` e RLS del tenant firmato. Dopo la
validazione XML, PostgreSQL cerca **per uguaglianza esatta**
di `NumeroSinistro`, con `LIMIT 2` e senza scegliere
un duplicato arbitrario. Il risultato è
`{preview,status,existingSinistroId}`:

- `new`: nessun riferimento presente;
- `existing`: un solo Sinistro e data evento compatibile
  (data denuncia verificata solo se inclusa nell'XML),
  **solo se l'XML contiene esclusivamente i tre campi base**;
- `review-required`: un singolo riferimento con date compatibili,
  ma l'XML contiene dati di compagnia, polizza, persona,
  allegati, attributi o altri elementi non ancora riconciliati.
  Nessun UUID candidato è restituito;
- `conflict`: un solo riferimento ma date incompatibili;
- `ambiguous`: più di un Sinistro con lo stesso riferimento.

Solo `existing` può riportare `existingSinistroId`;
gli altri esiti non identificano alcun record. La `preview`
contiene anche il solo booleano `hasUnmappedBusinessData`:
non rivela compagnia, polizza, nomi, payload o altri dati.
Il match `existing` è **un suggerimento su numero e date**,
non una prova d'identità o una licenza d'import.
[ADR 0055](adr/0055-legacy-xml-identity-review-gate.md).
Il piano
**non inserisce né modifica Sinistri/Perizie**, allegati,
audit di mutazione o dati assicurativi; `new` non
è un permesso di creazione. Lo schema non ha ancora
l'unicità del riferimento e un futuro import
richiederà riserva/idempotenza/approvazione governate
in transazione. [ADR 0052](adr/0052-xml-legacy-reconciliation-dryrun.md).

## V011 — nucleo interno d'import XML idempotente e atomico (#10/#6/#21)

`LegacyXmlCaseImportService` e `PgLegacyXmlCaseImportStore`
sono ora disponibili **solo nel codice backend**: non sono
registrati in DI e non esiste alcuna API REST/MCP di commit.
La [V011](scripts/db/migrations/V011__legacy_xml_import_idempotency.sql)
aggiunge il ledger tenant-RLS `legacy_xml_import_keys`,
con checksum immutabili di attore/chiave/richiesta e FK
differibile alla riga Sinistro. Un singolo commit PostgreSQL
inserisce ledger + Sinistro + audit `sinistro.import-xml`;
un replay con la stessa chiave e identici byte XML restituisce
lo stesso ID senza creare record/audit aggiuntivi.
Una chiave riutilizzata con XML differente o un riferimento
già esistente causano conflitto e rollback.

Questa tranche accetta **solo XML minimale** con
`NumeroSinistro`, `DataOraSinistro` ed eventualmente
`DataIncarico`; rifiuta **qualsiasi altro campo** (inclusi
persone, polizze e allegati) per evitare import parziali
silenziosi. È un'infrastruttura **non attivata in produzione**:
`POST /api/sinistri` non condivide ancora il lock del
commit XML; va introdotto un gate di concorrenza valido
per tutti i writer, oltre a idempotenza più completa,
consensi e trattamento antivirus degli allegati, prima
di abilitare un endpoint d'import. I Sinistri importati
internamente hanno una FK ledger che impedisce hard
delete senza una nuova policy esplicita.
[ADR 0053](adr/0053-v011-xml-case-only-atomic-idempotency.md).

## V012 — protezione SQL dei Sinistri importati (#24)

La [V012](scripts/db/migrations/V012__imported_sinistri_global_reference_guard.sql)
introduce un trigger **BEFORE INSERT/UPDATE** sulla tabella
`sinistri`: ogni writer PostgreSQL, compresi REST/MCP e
l'adapter d'import V011, usa lo stesso mutex transazionale
tenant+riferimento. Un nuovo inserimento o aggiornamento
che tenti di utilizzare un riferimento già attribuito
a un Sinistro importato viene rifiutato (HTTP `409` tramite
repository); il Sinistro importato rimane non modificabile
finché non esiste un workflow di correzione verificato.
La FK V011 continua a impedirne la cancellazione.

I **duplicati storici** di riferimenti mai importati
restano consentiti: non è stata introdotta una
`UNIQUE(tenant_id,external_reference)` arbitraria.
L'upgrade effettua un controllo delle collisioni
preesistenti: se un duplicato confligge con un import
V011, **fallisce senza riscrivere dati** e richiede
riconciliazione manuale. Il commit dell'import completo
rimane **non esposto**; #24 resta aperta per la corretta
identità composita assicuratore/anno e gli altri gate.
[ADR 0054](adr/0054-v012-imported-reference-global-sql-guard.md).

## Protezione della precisione temporale XML (#25)

Il legacy memorizzava date/ore dei Sinistri come
`DateTime`; il modello attuale conserva soltanto
`DateOnly`. La preview XML accetta i formati canonici
`yyyy-MM-dd` e `yyyy-MM-dd HH:mm:ss`, ma per i valori
con componente oraria (anche `00:00:00`) imposta
`hasUnmappedBusinessData=true`. Con un unico candidato
compatibile, il piano restituisce `review-required`
**senza UUID**, anziché presentare il match come
identità verificata.

Il commit XML interno V011, non collegato ad alcun
endpoint, **rifiuta** date/ore non conservabili,
commenti XML e processing instructions per non
scartare silenziosamente metadati o precisione:
nessun Sinistro, ledger o audit di successo viene
scritto. I contenuti di commenti e PI non sono
restituiti dalla preview. L'import completo
richiederà uno schema e una politica timezone
validati su sorgenti reali; non assumere UTC o
l'anno solare. [ADR 0056](adr/0056-legacy-xml-temporal-precision-fail-closed.md),
[issue #25](https://github.com/KeyserDSoze/Peritial/issues/25).

### Variazioni lessicali dei campi XML (#25)

Per evitare associazioni e importazioni apparentemente valide
ma con normalizzazioni silenziose, la preview rileva anche
spazi iniziali/finali nei valori scalari di `NumeroSinistro`,
`DataOraSinistro` e `DataIncarico`, e un `DataIncarico`
esplicitamente vuoto (`<DataIncarico/>` oppure solo spazi).
L'anteprima continua a mostrare il riferimento/data
normalizzati, ma indica `hasUnmappedBusinessData=true`:
con un singolo candidato compatibile il piano torna
`review-required`, senza UUID. Il commit interno V011
li rifiuta **prima** di creare Sinistro, ledger o audit.
La formattazione XML tra elementi resta innocua. 
[ADR 0057](adr/0057-xml-source-scalar-normalization-review.md).

### Riferimenti XML decodificati e blocco della riscrittura silenziosa (#25)

La preview ora rileva anche CDATA e riferimenti carattere
XML (ad esempio `&#45;` e `&amp;`): il valore restituito
può coincidere con una pratica esistente pur avendo una
rappresentazione sorgente differente. Perciò
`hasUnmappedBusinessData=true` impone
`review-required` senza UUID sul singolo candidato.
Il commit interno case-only V011 li rifiuta prima del DB,
mentre la preview resta disponibile e non divulga
markup XML originale, payload o dati personali.
I test includono UTF-8, UTF-16 LE/BE e parità REST/MCP.
[ADR 0058](adr/0058-xml-character-references-cdata-fail-closed.md).

## Catalogo MCP versionato e guardia di compatibilità (#12)

Il primo snapshot dichiarativo dei tool MCP è
[`docs/mcp-catalog.v1.json`](docs/mcp-catalog.v1.json):
**25 tool**, `format=1`, `version=1.0.0`, con nomi,
parametri obbligatori, scope atteso e distinzione tra
letture e operazioni mutative. Il job
`supply-chain-actions` verifica il manifesto contro
`McpTools.cs` e rifiuta rinominazioni, aggiunte/rimozioni,
modifiche agli argomenti obbligatori e classificazioni
di accesso non documentate. I test full-stack confrontano
il manifesto col vero MCP `tools/list` sotto token
Keycloak firmati `read` e `write`.

Lo snapshot non è un sistema di negoziazione automatica
tra versioni né sostituisce i controlli OAuth nei servizi:
le operazioni mutative restano soggette a scope, RLS,
audit e versioni CAS. `delete_sinistro` necessita
inoltre di uno scope esplicito e del gate di sviluppo.
[ADR 0059](adr/0059-versioned-mcp-tool-contract-gate.md).

### MCP catalog v1.1: firme complete, non solo parametri obbligatori (#12)

Il manifesto `docs/mcp-catalog.v1.json` è stato
esteso a `version=1.1.0`, mantenendo gli
stessi 25 tool. Descrive **tutti i 55 parametri**
pubblici, compresi `cursor`, `limit`,
`occurredFrom/To`, `reportedOn` e
`idempotencyKey`, indicando tipo JSON atteso,
opzionalità e nullabilità C#. Un cambiamento
silenzioso alla firma di un parametro opzionale
fallisce nel gate CI anche quando la lista
`required` è identica. Playwright confronta
con le proprietà e i tipi primari di
`tools/list` generati dall'SDK con JWT
Keycloak read/write. Il passaggio a v1.1
versiona **il manifesto di verifica**, non
aggiunge una route pubblica di negotiation
né modifica l'SDK. [ADR 0060]
(adr/0060-mcp-v1-1-full-parameter-contract.md).

Il contratto MCP v1.1 protegge anche i **default C# letterali**
(`defaultLiteral`): `null` significa nessun default
su parametro obbligatorio; `"null"` significa
default opzionale esplicito; `"50"` e `"10"`
conservano i limiti dichiarati. Una variazione
del valore `limit` non passa il gate neppure
se il parametro rimane opzionale. [ADR 0060]
(adr/0060-mcp-v1-1-full-parameter-contract.md).
