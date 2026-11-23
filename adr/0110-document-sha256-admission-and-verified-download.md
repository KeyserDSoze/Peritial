# ADR 0110 — SHA-256 privato di documento, commit atomico e download verificato

- Data: 23 novembre 2026
- Stato: prima tranche implementata su `main`, test backend/Compose dell'ultimo HEAD da attestare
- Issue: #27 (gate principale), #5 (sicurezza), #11 (download), #21 (audit/storage)
- Branch `death`: archivio storico, nessuna modifica

## Contesto e decisione

ADR 0109 valida MIME/estensione, magic e struttura XML/ZIP in
download, ma **non prova l'identità dei byte**: un PDF valido della
stessa lunghezza può essere sostituito da un secondo PDF valido.
Gli hash di backup (#6) tutelano il restore e NON ogni download online.

Implementare un digest **SHA-256 di 32 byte**, separato dalle chiavi
di idempotenza e senza affidarsi ad ETag di S3/Azure.

### Ingestione

Dopo il verdetto **Clean** di ClamAV, prima del PUT, `DocumentDigest.Compute`
calcola SHA-256 sui byte del buffer già validati. Il valore è soltanto
interno al `DocumentRecord`; `PgDocumentRepository.AddAuditedAsync`
rifiuta un digest assente/lungo diversamente da 32 byte **prima**
di consumare l'upload intent. `InsertAsync` scrive il campo
`content_sha256` nella stessa transazione PostgreSQL di
metadata, `security_audit` succeeded e completamento idempotency.
Il byte string originale e il digest non entrano nel DTO REST/MCP.

### Schema forward-only V013

`V013__documents_content_sha256_integrity.sql` aggiunge la colonna
`documents.content_sha256 bytea NULL` con CHECK che consente
solo NULL legacy oppure **esattamente 32 byte**. La migrazione
non aggiorna i record preesistenti e non dichiara verificato
il loro contenuto. Trigger `trg_reject_document_digest_rewrite`
nega riscritture successive del digest; il ruolo applicativo
ha già UPDATE/DELETE revocati da V009 e FORCE RLS è invariato.
Il migrator include V013 nel journal SHA-256 forward-only,
senza editare gli script V001–V012. Il bootstrap V001 dei soli
test crea anche V013 a meno che si stia testando l'adozione
strettamente equivalente del legacy.

**Limite noto:** il vincolo SQL consente ancora nuovi INSERT con
NULL via un writer raw privilegiato; l'invariante obbligatorio
per gli upload di produzione è oggi imposto dal repository
`AddAuditedAsync`. Il gate di enforcement per ogni possibile
writer SQL e i processi di backfill/approvazione legacy restano
tracciati e **non completati** in #27.

### Download

`PgDocumentCatalogRepository.FindForDownloadAsync` legge la
colonna solo tramite lookup privato tenant+ID sotto FORCE RLS:
`DocumentSummaryDto` e `DocumentDto` non contengono il digest.
`DocumentDownloadService` nega senza fetch un digest NULL o
di lunghezza errata. Dopo byte budget, verifica della lunghezza
e parsers ADR 0109, confronta SHA-256 tramite
`CryptographicOperations.FixedTimeEquals`. Un mismatch
restituisce `DocumentContentUnavailableException` senza
byte, key, digest o audit `document.download/succeeded`.

L'audit succeeded resta *prima* dell'invio HTTP; non è prova
di consegna al client. Il checksum non sostituisce antivirus,
quarantena, legal hold o provenance immutabile dello storage.

## Regressioni / prove

- Test iniziali sul download: [d0c3ee6](https://github.com/KeyserDSoze/Peritial/commit/d0c3ee6b1edf0c1eeb03ee91c1409befd907ed84),
  [3523f86](https://github.com/KeyserDSoze/Peritial/commit/3523f86cefa0c27222004a4b862eff64093932e6):
  sostituzione PDF formalmente valido e di uguale lunghezza,
  digest NULL di documento legacy, checksum 31 byte, controprove
  su PDF/XML/DOCX/JPEG/PNG validi.
- SHA-256 e business: [5eec8aa](https://github.com/KeyserDSoze/Peritial/commit/5eec8aad8d43824de57fc53a8f9d1df6550c3e35),
  [bf0cd26](https://github.com/KeyserDSoze/Peritial/commit/bf0cd26563529f040c249c462282486e348799c1),
  [8019cbe](https://github.com/KeyserDSoze/Peritial/commit/8019cbe1672857ec3cebff461c36be8c617af15d).
- DB V013: [bb3017c](https://github.com/KeyserDSoze/Peritial/commit/bb3017c1bf562e00f25612bada38560eb736f5c5),
  [d1aabf7](https://github.com/KeyserDSoze/Peritial/commit/d1aabf72df34dc613ca934f663e92ffd6017947b),
  [5a4ad4e](https://github.com/KeyserDSoze/Peritial/commit/5a4ad4eccccc1a7a025c2a6a4f3e073385fb9832).
- Test di migrazione, legacy-adoption, atomicità upload e recovery
  aggiornati a 13 versioni, fixture digest-aware.
  Alcune CI intermedie erano **rosse** per aspettative ferme a
  V012 e per i vecchi fixture `DocumentRecord` senza checksum;
  non equiparare queste CI a un verde terminale.
- Esito CI dell'ultimo HEAD: verificare con GitHub Actions;
  **nessuna attestazione di full-stack** finché Compose non è concluso.

## Residui critici

- Gate fail-closed in produzione per legacy NULL con percorso esplicito
  di backfill solo dopo ri-scan, approvazione e verifica del contenuto;
- Enforcement DB di non-NULL per ogni nuovo writer non autorizzato;
- Test fault injection a cavallo S3 PUT/commit ACK, replay e recovery
  con digest e verifiche su S3/Azurite;
- Matrice di retention/legal hold, revoche, versioning, protezione
  cryptographic provenance del digest da amministratori e workflow
  Azure/on-prem; test release finali.

La issue #27 resta **aperta** fino ai criteri completi.
