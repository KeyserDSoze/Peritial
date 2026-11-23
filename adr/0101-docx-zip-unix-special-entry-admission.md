# ADR 0101 — Rifiutare entry speciali Unix nei DOCX ZIP

- Data: 23 novembre 2026
- Stato: implementato su `main`; CI full-stack finale da attestare
- Issue: #5 P0 document security, #4 P0 REST/MCP OAuth, #21 audit atomico
- `death` esclusivamente archivio storico e non modificato

## Problema

Il validatore DOCX controlla oggi percorsi, doppi nomi,
numero di entry, budget ZIP e XML/.rels pericolosi (ADR 0100).
Tuttavia `ZipArchiveEntry.ExternalAttributes` può dichiarare
nella directory centrale **un symlink Unix** (mode tipo
`0120000`), FIFO, socket o device, anche sotto il nome
di un file regolare. Il backend non estrae oggi ZIP su disco;
tuttavia consentire questi archivi a storage e ad altri
processori/renderer futuri creerebbe una superficie
di traversal e dereferenziazione a valle.

## Decisione

Controllare per ogni membro del DOCX la categoria del tipo Unix:

```csharp
var unixType = ((uint)entry.ExternalAttributes >> 16) & 0xF000u;
if (unixType != 0 && unixType != 0x8000u && unixType != 0x4000u)
    throw new ArgumentException("Unsupported DOCX ZIP entry type.");
```

- `0x8000`: file regolare, ammesso;
  `0x4000`: directory, ammessa;
  `0`: nessun tipo Unix dichiarato, ammesso per
  retrocompatibilità con ZIP/DOCX provenienti da Windows.
- `0xA000`: symlink; `0x1000`: FIFO;
  `0x2000`: char device; `0x6000`: block device;
  `0xC000`: socket; altri tipi inattesi: rifiutati.
- Il cast a `uint` evita errori di segno sui 16 bit alti.
  Non vengono aperti/dereferenziati i link;
  i nomi continuano ad essere validati normalmente,
  prima della decompressione.
- Il controllo è nella validazione Business condivisa
  tra REST multipart e MCP base64, **prima** di ClamAV,
  intenti, storage, metadata e audit `succeeded`.
  La scansione ClamAV rimane obbligatoria per i DOCX ammessi.
  Nessuna migrazione/schema/nuovo endpoint o cambiamento
  di `death`.

## Evidenze TDD

- RED:
  [2b7ea3a](https://github.com/KeyserDSoze/Peritial/commit/2b7ea3ac5f38db2b3195cd47f716a6a51fbd05ce);
  [CI #45170039456](https://github.com/KeyserDSoze/Peritial/actions/runs/45170039456).
  5 test Business nuovi falliti **come previsto**
  per symlink, FIFO, character/block device, socket;
  168 Business (incluso il nuovo test file regolare Unix)
  e 201 Infrastructure verdi.
- Fix:
  [b0e323b](https://github.com/KeyserDSoze/Peritial/commit/b0e323b2413d366f18880fc649971842c400f7ee)
  aggiunge il controllo del tipo Unix ZIP in
  `UploadedDocumentValidation.ValidateDocx`.
- E2E:
  [3e8f103](https://github.com/KeyserDSoze/Peritial/commit/3e8f103e4b339735ddec81d274977c196b82fe25)
  prova con JWT Keycloak un DOCX ZIP valido a tre
  entry, con symlink relativo dichiarato su
  `word/media/link.bin`. REST deve rispondere HTTP 400
  e MCP `isError:true`, senza divulgare il target.

## Limiti

La guardia blocca **attributi ZIP che dichiarano**
esplicitamente tipi Unix speciali, non identifica contenuti
che simulano un link senza metadata, path semantici OOXML,
oggetti embedded o relazioni remote. Non è una verifica
di quarantena/antivirus produttiva. Rimangono da completare
quarantena persistente, FreshClam e alerting su infrastrutture
reali, retention/legal hold, rate limiting e test Azure/on-prem
prima della chiusura della #5.
