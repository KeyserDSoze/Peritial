# ADR 0106 — DOCX ZIP: path Unicode canonically equivalent

- Data: 23 novembre 2026
- Stato: implementato su `main`; CI completa del commit HEAD da confermare
- Issue: #5 sicurezza documentale; #4 contratti REST/MCP autenticati
- Branch `death`: solo consultazione storica, nessuna modifica

## Contesto e rischio

L'ammissione del DOCX ZIP rifiuta già nomi duplicati
case-insensitive, percorsi traversal, directory implicite oscurate
da file, nomi DOS riservati, segmenti con trailing dot/space e
voci ZIP speciali (ADR 0100–0105). Mancava però una nozione di
equivalenza Unicode: `word/media/résumé.bin` e
`word/media/re\u0301sume\u0301.bin` sono stringhe diverse in
un confronto `OrdinalIgnoreCase`, ma Unicode normalizza la
seconda (sequenze con combining marks) alla prima.

Un filesystem o convertitore successivo può associare i due nomi
allo stesso file. Il problema si applica anche alla directory
richiesta implicitamente da un figlio e all'entry directory esplicita.
Il backend corrente non estrae ZIP sul filesystem; il controllo
impedisce di conservare una struttura ambigua per consumer futuri.

## Decisione

Nella prima passata di `UploadedDocumentValidation.ValidateDocx`,
prima di decompressione, antivirus e scrittura:

- mantenere il percorso ZIP originale per i controlli di sintassi,
  lettura degli entry e verifica della struttura OOXML;
- derivare un solo `canonicalPath` con
  `name.TrimEnd('/').Normalize(NormalizationForm.FormC)`;
- riutilizzare questo valore **sia** nell'insieme `names`
  (duplicati, `OrdinalIgnoreCase`) **sia** in `filePaths` e
  `directoryPaths` (parent e directory implicite/esplicite);
- rifiutare un file/directory se collide con una forma composta
  o decomposta precedente, indipendentemente dall'ordine ZIP;
- mantenere ammissibile un unico nome decomposto non ambiguo.
  Non riscrivere, estrarre, rinominare o normalizzare il payload
  o i member effettivi, né cambiare i limiti di ZIP, schema DB,
  autorizzazioni o policy antivirus.

In caso di collisione l'ammissione lancia un generico
`ArgumentException` prima di `Clamd`, object storage,
metadata e audit `succeeded`. Non include i nomi ostili in
risposte REST/MCP o log.

## Verifica test-first

- RED [6ea6991](https://github.com/KeyserDSoze/Peritial/commit/6ea6991df2709ed2fd9b984478b6bab3ff2c98fc),
  [CI #45195168628](https://github.com/KeyserDSoze/Peritial/actions/runs/45195168628):
  **6 fallimenti Business attesi** sul codice precedente
  per doppioni NFC/NFD (due ordini), file padre vs figlio sotto
  directory Unicode equivalente (due ordini), directory esplicita
  vs file Unicode equivalente (due ordini).
  **214 Business**, **23 Domains** e **201 Infrastructure** passati;
  la controprova con un solo nome Unicode decomposto passa.
- Fix [3721aa2](https://github.com/KeyserDSoze/Peritial/commit/3721aa26503295866b9902938fe4eee75152ccd0),
  [CI #45195240325](https://github.com/KeyserDSoze/Peritial/actions/runs/45195240325).
  Verificare il risultato terminale del job backend prima di
  dichiarare GREEN.
- Test E2E con token firmato Keycloak REST multipart e
  `ingest_file` MCP:
  [19f3f02](https://github.com/KeyserDSoze/Peritial/commit/19f3f0220589bb0aa3b29fc227edaa646eb2fab4),
  ZIP DOCX sintetico con 4 member, inclusi i due nomi Unicode
  canonici equivalenti. REST deve rispondere 400 e MCP deve
  restituire `isError:true`. Esito Compose ancora da attestare.

## Limiti e gate ancora aperti

La normalizzazione Unicode NFC non prova la portabilità completa
su tutti i filesystem (casefold Unicode locale, compatibilità
NFKC, collazioni specifiche), né la sicurezza semantica OOXML.
Il server non dovrebbe comunque estrarre parti OOXML in percorsi
non sandboxati o seguire risorse esterne. Rimangono in #5
quarantena/approvazione/rescan persistenti, FreshClam realmente
gestito su Azure/on-prem, retention/legal hold, limiti aggregati
multi-replica, contenuto attivo e rischio dei converter futuri.
Nessuna chiusura prematura della P0.
