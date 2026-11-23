# ADR 0102 — DOCX ZIP: nomi senza controlli e coerenza Unix file/directory

- Data: 23 novembre 2026
- Stato: implementato su `main`; esito CI del commit finale da attestare
- Issue: #5 (sicurezza ingestion); REST/MCP condividono il Business
- `death` rimane storico, non modificato

## Problema

Il validatore DOCX limita già numero, dimensioni e rapporto di espansione
ZIP, verifica i nomi contro traversal/backslash/duplicati case-insensitive,
parsa in modo sicuro le parti XML/.rels e rifiuta symlink e special file Unix
(ADR 0100–0101). Restano due ambiguità nella directory centrale ZIP:

1. un nome di entry può contenere TAB, LF o CR, rendendo ambiguo il
   riferimento per logging, visualizzatori o estrattori futuri;
2. il tipo Unix `0040000` (directory) può essere applicato a un
   nome di file senza slash finale; il tipo `0100000` (file regolare)
   può essere attribuito a un nome con slash finale.

Non eseguiamo estrazioni sul server attuale, ma non dobbiamo ammettere
metadati che possano essere interpretati diversamente da componenti a valle.

## Decisione

Durante la validazione di ciascuna entry, prima di qualsiasi lettura
compressa o chiamata allo scanner:

- rifiutare `char.IsControl` in qualsiasi carattere del nome ZIP;
- se il tipo Unix dichiarato è directory, richiedere slash finale;
- se il tipo Unix dichiarato è file regolare, vietare slash finale;
- mantenere accettati i tipi Unix assenti (`0`, ZIP Windows/legacy),
  le directory coerenti e i file regolari coerenti;
- non modificare l'allowlist di estensioni, i budget ZIP, la gestione
  XML/OPC, i limiti REST/MCP o i ruoli PostgreSQL.

Restano fail-closed `ArgumentException`, HTTP 400 REST e errore MCP,
prima di ClamAV, storage, `documents` e audit di successo. Nessun
nome controllato viene usato per creare un path nel filesystem.

## Regressioni

- Test-first `7a42ea342bf545a01704a083202ac4c6cf090da4`:
  tre esempi con TAB, LF e CR nei nomi ZIP e un file con
  `ExternalAttributes` Unix da directory; test verificano anche
  zero scanner, zero PUT, zero metadata e zero audit di successo.
- Fix `221d6ce7ab9d41d8865bfa98ab346078a50a0f41`:
  verifica nome senza controlli e confronto path/tipo Unix.
- E2E OAuth Keycloak `99610c54ca452cbef16ba699874570b71d6fd5ff`:
  REST multipart e tool MCP `ingest_file` rifiutano una entry
  `word/media/link.bin` marcata directory Unix nel central directory;
  il test accerta HTTP 400, `isError:true` e nessuna disclosure del
  target. Non cambia il contenuto compresso né i CRC.
- I test preesistenti continuano a coprire symlink, device, FIFO,
  socket, normali file Unix e DOCX minimo valido.
- CI test-first e CI fix:
  https://github.com/KeyserDSoze/Peritial/actions/runs/45181292924
  e https://github.com/KeyserDSoze/Peritial/actions/runs/45181379072
  (verificare stato terminale prima di dichiararle PASS/FAIL).

## Limiti

È un controllo di ammissione e non uno scanner malware né un parser OOXML
semantico completo. Non garantisce l'assenza di macro/ActiveX, URL
esterni, oggetti OLE, polyglot ZIP, metadata non dichiarati, archivi
corrotti non ancora identificati o vulnerabilità di converter futuri.
ClamAV fail-closed resta obbligatorio; quarantena persistente, firma
delle definizioni realmente aggiornata in produzione, retention,
rate limiting distribuito e test Azure/on-prem restano aperti in #5.
