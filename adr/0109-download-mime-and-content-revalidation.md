# ADR 0109 — Revalidare MIME e byte dell'oggetto prima dell'audit download

- Data: 23 novembre 2026
- Stato: implementato su `main`; verificare la pipeline finale dell'HEAD
- Issue: #11 gestione documentale, #5 sicurezza ingestione/download, #21 semantica audit
- Branch `death`: esclusivamente archivio storico, mai modificato

## Contesto

Il download autenticato usa tenant firmato, PostgreSQL FORCE RLS,
una chiave storage derivata da tenant/UUID/estensione, una lettura
con lunghezza massima dichiarata e l'audit append-only di successo
**prima** di restituire i byte. La validazione già rifiuta metadati
con nomi non canonici e lunghezze false (ADR 0095/0108).

La sola lunghezza, tuttavia, non distingue una sostituzione del blob
di dimensione identica (ad esempio PDF rimpiazzato con byte non PDF,
XML malformato o ZIP DOCX troncato) né un ContentType persistito
incoerente con l'estensione del nome. La barriera d'ingestione non
impedisce che un record storico o uno storage successivamente alterato
abbia metadati/byte contraddittori.

## Decisione

Il servizio di download `DocumentDownloadService.ReadAsync` continua
a imporre scope read/write, isolamento tenant e chiave storage esatta.
Prima della lettura object-store confronta la coppia
**estensione/MIME persistito** con l'allowlist condivisa
`UploadedDocumentValidation.IsSupportedContentType`
(la stessa usata ora da `FileIngestionService`):

- PDF application/pdf;
- XML application/xml oppure text/xml;
- JPG/JPEG image/jpeg; PNG image/png;
- DOCX MIME OOXML standard.

Un MIME non riconosciuto è un errore generico
`DocumentContentUnavailableException`, senza scaricare il blob
o emettere `document.download/succeeded`.

Dopo la lettura bounded, la verifica di lunghezza e **prima**
dell'evento di audit `succeeded`, la stessa
`UploadedDocumentValidation.ValidateContent` dell'ingestione
controlla la signature e la struttura minima ammessa.
Per XML sono attivi DTD prohibited, resolver null e limite caratteri;
per DOCX valgono tutti i budget ZIP ed XML/OPC già stabiliti.
Il risultato resta con MIME forzato application/octet-stream;
i fallimenti sono generici, non restituiscono byte, nome ostile
o URL/chiavi object-store.

L'evento audit success resta **pre-invio HTTP**: la consegna effettiva
non è transazionalmente dimostrata, né questa modifica aggiunge
un tentativo fallito all'audit. Non mutare lo schema, i permessi,
la chiave storage, il controllo idempotenza o il branch storico.

## Evidenze TDD

- Test prima della correzione:
  [428f6af](https://github.com/KeyserDSoze/Peritial/commit/428f6afd3e33c8a896df68d075a26d673a86d1c2),
  fixture ZIP corretta
  [d22d8b1](https://github.com/KeyserDSoze/Peritial/commit/d22d8b1d347a11d82803287f231239fc631f6d61).
- [CI RED #45205439992](https://github.com/KeyserDSoze/Peritial/actions/runs/45205439992):
  **7 nuovi test Business falliti come previsto**, 270 Business
  preesistenti verdi, più 23 Domain e 201 Infrastructure.
  Quattro casi MIME persistito incoerente, tre casi
  byte malformati con lunghezza coerente; due XML validi
  usati come controprove positive.
- Policy MIME comune:
  [5d758f0](https://github.com/KeyserDSoze/Peritial/commit/5d758f05076ab738769ff229a861beba893344cd)
  e refactor ingestione
  [17a7a02](https://github.com/KeyserDSoze/Peritial/commit/17a7a027b33e90905593a6f5488931e0826d574e).
- Download fail-closed:
  [2ec2417](https://github.com/KeyserDSoze/Peritial/commit/2ec2417a6f043800d134da9f58064f3057ad9b3a);
  [CI #45205691088](https://github.com/KeyserDSoze/Peritial/actions/runs/45205691088).
  Attestare l'esito terminale backend e Compose sul commit
  finale prima di affermare che la regressione è chiusa.

## Limiti e follow-up

La validazione di tipo è **un controllo strutturale**, non
una garanzia di integrità crittografica o provenienza.
Un altro file PDF/XML/DOCX valido e della stessa lunghezza
può ancora sostituire l'oggetto. Per coprire questo scenario
servono hash/checksum immutabili per oggetto con provenienza
e verifica in download, versione/retention/legal hold, più
scansione/re-scan e quarantena durevole secondo #5/#11.
Il parser PDF verifica attualmente la signature, non la
conformità semantica o una firma digitale. La consegna
effettiva HTTP e la semantica audit restano separate (#21).
Nessun claim production-ready per Azure/on-prem.
