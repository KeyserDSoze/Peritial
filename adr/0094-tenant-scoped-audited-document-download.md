# ADR 0094 — Download documenti REST tenant-scoped, delimitato e auditato

- Data: 23 novembre 2026
- Stato: implementato su `main`, gate di produzione ancora aperti
- Issue: #5 sicurezza documenti, #11 gestione documenti, #19 audit/operazioni
- `death`: storico, non modificato

## Problema

La precedente API esponeva soltanto metadati documentali tramite
`GET /api/files`, `GET /api/files/page`, `GET /api/files/{id}`
e tool MCP `list_documents/page_documents/get_document`. Il
backend aveva uno storage privato S3/Azure per ingestion, ma non
esisteva una route per restituire il contenuto verificando
l'autorizzazione tenant, senza consegnare chiavi o URL del provider.
Una risposta `GetObject` inviata direttamente al client senza
controlli di dimensione/audit avrebbe esposto a download non
autorizzati, memoria non delimitata e successi senza traccia.

## Decisione

1. Nuovo `GET /api/files/{id:guid}/download`, protetto dalla policy
   OAuth read (`peritial.read` oppure `peritial.write`) e dal
   `DocumentDownloadService` che ricontrolla lo scope firmato.
   Mancanti e UUID di altro tenant sono **404 indistinguibili**.
2. L'unica lettura interna di `storage_key` è
   `PgDocumentCatalogRepository.FindForDownloadAsync`: WHERE
   `tenant_id=@tenant AND id=@id` in `TenantDbScope` FORCE RLS,
   con il tenant preso esclusivamente dal contesto JWT.
   I contratti metadata pubblici continuano a proiettare solo
   colonne allowlist e non contengono `storage_key`.
3. Il servizio verifica `id`, lunghezza positiva e **≤25 MiB**,
   filename persistito in forma canonica (senza percorso o caratteri
   di controllo) e uguaglianza esatta di `storage_key` con
   `DocumentKeys.For(tenant,id,fileName)`. Una chiave incoerente
   non viene nemmeno letta dal provider.
4. I nuovi `IDocumentBlobReader` su `MinioObjectStore` e
   `AzureBlobObjectStore` riutilizzano l'account privato di ingestion,
   senza URL presigned, SAS, redirect o GET direttamente dal browser.
   `BoundedDocumentRead` restituisce in memoria **solo** il numero
   dichiarato di byte e verifica il byte successivo/EOF; il provider
   S3 verifica anche ContentLength. Un oggetto troncato, più lungo,
   mancante o non disponibile produce HTTP 503 senza dettagli dei
   provider. La risposta non espone stream vivi senza bound.
5. Il Business appende `security_audit` (`document.download`,
   `succeeded`, tenant, subject e document UUID) **dopo** la lettura
   verificata e **prima** di rendere i byte al layer HTTP. Se l'audit
   fallisce, non restituisce il buffer. Diniego scope nel Business
   genera audit `denied` con resourceId null; un 403 bloccato
   anticipatamente dalla policy ASP.NET genera il distinto evento
   generale `authorization.forbidden`, come gli altri endpoint.
6. HTTP forza `application/octet-stream`,
   `Content-Disposition: attachment`, `nosniff`,
   `Content-Security-Policy: sandbox`, `Cache-Control: no-store`
   e disabilita `Range`. Non restituisce il MIME originale come
   contenuto eseguibile o renderizzabile inline.

### Cosa significa l'audit di successo

`document.download/succeeded` significa **bytes pronti e
trasferimento autorizzato** al confine del Business. Non
garantisce che il browser abbia ricevuto il corpo completo: una
disconnessione successiva al commit dell'audit non è un
trasferimento verificato. Contabilizzare download effettivi o
completati richiede un protocollo separato con ricevuta e
retention; non si falsifica tale evidenza.

## Prove

- [`DocumentDownloadTests.cs`](../tests/backend/Peritial.Business.Tests/DocumentDownloadTests.cs):
  successo con scope read/audit, scope mancante audit denied senza
  query, 404 tenant esterno/ID assente senza storage, chiave non
  canonica, file name non canonico, troppo grande, lunghezza
  difforme e failure di audit prima del ritorno dei byte.
- [`DocumentCatalogRepositoryTests.cs`](../tests/backend/Peritial.Infrastructures.Tests/DocumentCatalogRepositoryTests.cs):
  lookup del locator privato attraverso `peritial_app` e
  PostgreSQL FORCE RLS reale, cross-tenant e connection pool
  senza persistenza del contesto sessione.
- [`S3ObjectStoreContractTests.cs`](../tests/backend/Peritial.Infrastructures.Tests/S3ObjectStoreContractTests.cs)
  e [`AzureBlobObjectStoreContractTests.cs`](../tests/backend/Peritial.Infrastructures.Tests/AzureBlobObjectStoreContractTests.cs):
  integrazione SeaweedFS/Azurite con contenuti esatti e
  mismatch lunghezza in entrambe le direzioni.
- [`authenticated.spec.ts`](../tests/frontend/authenticated.spec.ts):
  Keycloak reale grant writer/read-only/tenant B/no-scope,
  POST multipart pulito, GET documento con lettura byte-esatta
  ed header difensivi, 401/403/404 e confronto con UUID
  sconosciuto, `get_document` MCP soltanto metadati.

### CI

Il primo commit test-only
[`635272ee`](https://github.com/KeyserDSoze/Peritial/commit/635272eef813a6996ef48f52e96b84d8a1fe4c26)
introduce interfacce non esistenti, quindi l'errore di build
è un gate di compilazione, non una prova RED di fuga di dati.
I successivi commit introducono Business, repository, adapter,
route, test infrastrutturali ed E2E. Il codice con E2E REST è
[`d68780a7`](https://github.com/KeyserDSoze/Peritial/commit/d68780a72e9467f076b0a70839698c546997c1f6).
La suite backend al commit test E2E precedente
[`9ce1de63`](https://github.com/KeyserDSoze/Peritial/commit/9ce1de6341f568f8ea252bcfffb1a18ddad7cb08)
ha già superato **364/364 test** (23 Domain, 144 Business, 197 Infrastructure)
in [GitHub Actions #45128692682](https://github.com/KeyserDSoze/Peritial/actions/runs/45128692682).
La verifica 9/9 dell'ultimo HEAD e il test E2E del nome
canonico vanno attestati solo dopo i rispettivi run conclusi.

## Limiti e non-obiettivi

- Il download REST **non** equivale al completamento dell'issue #5:
  quarantena durevole, antivirus/riscan in download, retention e
  legal hold, rate limiting per subject/tenant, cap di memoria
  aggregato multi-utente, timeout e circuit breaker cross-replica,
  distribuzione Azure/Entra reale e TLS/secret management restano
  da validare. Il materiale in PostgreSQL è stato ammesso dal
  ClamAV prima della PUT, ma non ha una scan-revision durevole.
- L'interfaccia PWA mostra ancora i metadati; un download browser
  con Bearer/OIDC, annullamento e UX accessibile è una tranche
  distinta (#11/#13). I tool MCP rimangono metadata-only:
  non si introduce il trasporto di byte in JSON-RPC per
  evitare oversharing e base64 illimitato.
- L'audit autorizza la preparazione del corpo, non certifica
  l'effettiva consegna. Nessun endpoint di cancellazione
  metadata/documento è stato aggiunto; V009 preserva la
  loro immutabilità lato app.
- Nessuna migrazione schema necessaria: `storage_key`,
  `security_audit` e grant SELECT tenant-RLS esistevano;
  non si allargano i privilegi PostgreSQL.

`main` è l'unico branch modificato; `death` resta archivio storico.
