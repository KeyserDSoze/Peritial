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

## Fixture CI Compose V013

La fixture Compose locale era intenzionalmente su V001 non
versionata con provision CI esplicita V003–V012. Con l'introduzione
della colonna di digest, **i test full-stack non possono fare upload
senza V013**. Il workflow
[69698d9](https://github.com/KeyserDSoze/Peritial/commit/69698d9fd36aa97210778309e7a76e8dc087eb16)
aggiunge V013 con `psql --single-transaction -f /dev/stdin`
prima del test con JWT Keycloak e della fixture import V012,
**solo** nel database effimero Compose. Non crea un journal e non
abilita alcuna migrazione automatica in produzione. Gli ambienti
persistenti restano sottoposti a backup e migrator esplicito.
Full-stack [CI #45212051268](https://github.com/KeyserDSoze/Peritial/actions/runs/45212051268)
da attestare fino a terminale; non chiamare verde in anticipo.


## SeaweedFS e Azurite: sostituzione reale a lunghezza invariata

La prima tranche V013/V014 verificava la sostituzione di un PDF
di lunghezza identica contro il `DocumentDownloadService` con
blob reader simulato. La [CI #45215521980](https://github.com/KeyserDSoze/Peritial/actions/runs/45215521980)
ha confermato **8/9 job verdi** ma Compose **fallito**: il
contratto `ObjectRecoveryCheckpointContractTests` inseriva due
nuovi documenti direttamente in PostgreSQL senza SHA-256, ormai
vietato dalla migration V014. La fixture è stata corretta in
[10347f3](https://github.com/KeyserDSoze/Peritial/commit/10347f3060ce42333ff93327601385291e873402)
usando il digest del contenuto PDF originale (non un hash fittizio).

Il nuovo
[RealDocumentDigestDownloadContractTests](https://github.com/KeyserDSoze/Peritial/blob/main/tests/backend/Peritial.Infrastructures.Tests/RealDocumentDigestDownloadContractTests.cs)
(provenienza: [commit 2182981](https://github.com/KeyserDSoze/Peritial/commit/21829813ee9bfcfebba9703655e4b99e0f67e34f))
esercita **due provider veri** nella Compose usa-e-getta:
SeaweedFS tramite `MinioObjectStore` e Azurite tramite
`AzureBlobObjectStore`. Ogni prova:

1. scrive metadata con digest SHA-256 persistito nel database vero;
2. verifica il lookup con `peritial_app` e FORCE RLS
   (un tenant estraneo non può leggere il documento);
3. scarica il PDF originale e verifica un solo audit succeeded;
4. sovrascrive lo **stesso** blob con un altro PDF valido, stessa
   lunghezza e MIME, ma SHA-256 diverso;
5. richiede un download e attende il rifiuto generico, **nessun
   secondo audit succeeded** e nessuna chiave storage nel messaggio;
6. ripristina i byte originari, verifica download positivo e secondo
   audit succeeded. I blob del test vengono eliminati.

Il workflow
[commit d7b5fb9](https://github.com/KeyserDSoze/Peritial/commit/d7b5fb9cd307514b04325caeeb477a5c635f2439)
aggiunge le nuove prove al passo Compose di verifica dei veri
provider S3/Azurite, senza saltare le prove esistenti. L'esito
full-stack va attestato con terminale della pipeline successiva:
non presumere che il solo compile backend certifichi S3/Azurite.

Restano aperti i fault drill COMMIT/PUT ACK, provenienza
crittografica indipendente del database, policy approvata per
`NULL` legacy e verifica reale Azure/on-prem; gli emulatori
Compose non costituiscono certificazione cloud.


## Checkpoint offline: legame fra digest persistito e inventario S3

Dopo l'introduzione di V014 i checkpoint di backup avevano un limite:
`ObjectRecoveryCheckpoint.CaptureAsync` generava un nuovo SHA-256
sui byte S3 ma non confrontava quel valore con il digest
`documents.content_sha256` dei byte ammessi in upload. Una
manomissione **precedente alla prima acquisizione** poteva quindi
essere inglobata in un nuovo manifest, anche quando i bytes
corrispondevano a un PDF valido di uguale lunghezza.

- [Test 731e631](https://github.com/KeyserDSoze/Peritial/commit/731e6318f6eeefe785cdcc9c3e9f84c84305ff25):
  test negativo SeaweedFS S3 reale: sovrascrive l'oggetto prima
  del primo capture e attende rifiuto; ripristina poi i byte corretti.
- [Fix 99112b1](https://github.com/KeyserDSoze/Peritial/commit/99112b1cd24dd3fd09b51566b16502fd5d8f2948):
  il reader privato del checkpoint con credenziali BYPASSRLS legge
  anche `content_sha256`, verifica il digest dei byte reali in
  `CaptureAsync` e verifica la corrispondenza DB/manifest in
  `VerifyMetadataAsync`, prima dell'import. Confronto tramite
  `CryptographicOperations.FixedTimeEquals`; mismatch con errore
  generico, senza filename, chiavi o digest.
- [Test 9fb929a](https://github.com/KeyserDSoze/Peritial/commit/9fb929a4a39e440e24e6359bcf51b5a6858481b2):
  un checkpoint formalmente valido, con SHA di un secondo PDF
  di uguale lunghezza, è negato anche quando i riferimenti tenant,
  ID, chiavi e byte del DB restano coerenti.
- Il vincolo della V014 resta abilitato; la fixture di export/import
  dei due server SeaweedFS/PostgreSQL è stata adeguata in
  [56bedbc](https://github.com/KeyserDSoze/Peritial/commit/56bedbc7fb0a4c45d066e480bbcfc7af6c0e819e)
  per usare l'SHA-256 dei byte originali senza inventare hash.
- I documenti legacy `NULL` mantengono la possibilità di
  archiviazione ma il checkpoint dimostra **solo i byte acquisiti
  in quell'istante**, non che siano stati ammessi da una scansione
  attendibile precedente. I download restano negati finché non
  vengono ri-scansionati con approvazione (#27).
- La prova terminale del test Compose sui nuovi commit va
  verificata: un job backend verde non attesta un test con
  SeaweedFS reale. Non equiparare la HMAC del manifest a una
  firma indipendente dell'archivio.

Vedere [runbook checkpoint](../docs/object-recovery-checkpoint.md).
