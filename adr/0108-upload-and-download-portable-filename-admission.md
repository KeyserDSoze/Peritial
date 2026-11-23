# ADR 0108 — File name admission: safe persisted metadata and download disposition

- Data: 23 novembre 2026
- Stato: implementato su `main`; full-stack CI sull'HEAD documentale da verificare
- Issue: #5 (sicurezza documentale), #11 (download controllato), #4 (REST/MCP shared admission)
- `death`: archivio storico, mai modificato

## Problema

`UploadedDocumentValidation.CanonicalFileName` eliminava percorsi e
caratteri di controllo ma accettava alcuni nomi non portabili e
potenzialmente ambigui, ad esempio `CON.pdf`, `COM1.pdf`,
`invoice:alternate.pdf` e nomi con Unicode bidirectional overrides.
L'attributo `file_name` è persistito in PostgreSQL, compare nei
cataloghi e viene usato come nome dell'allegato scaricato tramite
`Content-Disposition`. Un percorso ZIP DOCX sicuro non equivale
a un nome documento sicuro.

Il download ricontrollava già la forma canonica in
`DocumentDownloadService.ReadAsync`, per impedire a metadati
corrotti o legacy di raggiungere object storage e intestazioni.
Era importante applicare la stessa policy sia alla nuova ingestione
sia ai documenti persistiti in passato.

## Decisione

Mantenere la compatibilità intenzionale con i path ricevuti da
client Windows (ad es. `C:\\temp\\rapporto.pdf` diventa
`rapporto.pdf`), ma controllare il **solo basename** risultante:

- rifiutare i nomi riservati di device DOS/Windows (`CON`,
  `PRN`, `AUX`, `NUL`, `CONIN$`, `CONOUT$`,
  `COM1`–`COM9`/`LPT1`–`LPT9`, comprese cifre in apice),
  anche con estensione e indipendentemente dal case;
- rifiutare `:`, `<`, `>`, `|`, `?`, `*`, `"`
  e il punto o spazio finale; `:` può designare alternate
  data streams su NTFS;
- rifiutare i controlli Unicode espliciti di bidirectional override,
  embedding e isolamento (U+061C, U+200E/U+200F,
  U+202A–U+202E e U+2066–U+2069), oltre ai control character
  già rifiutati;
- continuare ad ammettere nomi come `consolidato.pdf`,
  `COM0.pdf`, `LPT10.pdf` e `résumé.pdf`;
- mantenere lo stesso `CanonicalFileName` come guardia
  di `DocumentDownloadService`: un record con nome
  non conforme è rifiutato **prima della lettura del blob e
  prima dell'audit di download-success**, con un errore generico
  `DocumentContentUnavailableException`.

Nessuna modifica ai bytes del file, al MIME consentito,
alle chiavi storage tenant-scoped, allo schema PostgreSQL,
all'atomicità audit, al limite di upload, ai claim JWT
o al branch legacy. Nessun nome ostile viene restituito negli errori.

## Evidenza test-first e integrazione

- TDD RED upload:
  [63b839a](https://github.com/KeyserDSoze/Peritial/commit/63b839a39ec79d6f5b4e83b569c6741cd3493659),
  [CI #45201500326](https://github.com/KeyserDSoze/Peritial/actions/runs/45201500326):
  **17 regressioni Business fallite** sul precedente validatore;
  246 Business, 23 Domain e 201 Infrastructure preesistenti verdi.
  Quattro controprove con nomi legittimi restano ammesse.
- TDD RED download:
  [c16628b](https://github.com/KeyserDSoze/Peritial/commit/c16628b50e795a20c4794c04c3c8bc63d64b5671),
  [CI #45201551136](https://github.com/KeyserDSoze/Peritial/actions/runs/45201551136):
  **22 regressioni Business fallite** (le 17 upload +
  5 record download invalidi), mentre 246 Business,
  23 Domain e 201 Infrastructure continuano a passare.
- Fix:
  [003a480](https://github.com/KeyserDSoze/Peritial/commit/003a4805c0cd495db1b19bf8111e9efd7a16f93e),
  [CI #45201739432](https://github.com/KeyserDSoze/Peritial/actions/runs/45201739432):
  backend **492/492 PASS** (23 Domain, 268 Business,
  201 Infrastructure).
- Test JWT Keycloak end-to-end REST multipart e MCP `ingest_file`
  aggiunti in
  [1e935cc](https://github.com/KeyserDSoze/Peritial/commit/1e935ccf48b265c7c3ecc5d49c28e9921ff25427):
  rifiuto HTTP 400 / `isError:true` di `CON.pdf`,
  `invoice:alternate.pdf` e nome con U+202E.
  L'esito Compose del commit finale va attestato
  prima di dichiarare l'intera pipeline verde.

## Limiti di sicurezza / prossime azioni

Questo filtro non elimina le minacce da file malevoli, non
completa il lavoro di quarantena/riscan/approvazione né
risolve policy di retention/legal hold, aggiornamento ClamAV,
rischio converter o rate limiting distribuito (#5).
Una pipeline di download resta un'operazione non atomica con
l'effettiva consegna della risposta (#11/#21). Nessuna
issue P0 viene chiusa per questo sottocontrollo.
