# ADR 0010 — ClamAV fail-closed prima della persistenza documentale

Stato: adottata per il vertical slice in sviluppo (2026-11-15); quarantena persistente non ancora adottata.

## Contesto
Il precedente FileIngestionService verificava solo dimensione, estensione/MIME e magic bytes, poi scriveva direttamente su S3/Azure Blob. Nessun engine antivirus controllava i byte reali. È un rischio P0 condiviso da REST multipart e MCP base64.

## Decisione
- Mantenere gli upload nel buffer di memoria limitato a 25 MiB dopo le verifiche strutturali. Prima di PUT e di qualunque metadata/audit di successo, inviare gli stessi byte al `IFileMalwareScanner` obbligatorio.
- Adapter infrastrutturale `ClamdFileMalwareScanner`: protocollo ufficiale `zINSTREAM\0`, frames a 4 byte big-endian, chunk 64 KiB e frame finale zero. Accettare **solo** risposta NUL-terminata esattamente `stream: OK`; `stream: ... FOUND` è infetto, qualunque altro esito (ERROR, dimensioni eccedute, risposta troncata/unknown) è indisponibilità.
- File infetto: HTTP 422 e audit `document.scan` denied senza contenuto, filename o nome della firma. Scanner assente, non configurato, timeout o esito ambiguo: blocco fail-closed HTTP 503, senza file/metadata.
- Le eccezioni di scan precedono la generazione della storage key e non attraversano la logica di compensazione, perché non è stato caricato alcun oggetto.
- Clamd su TCP 3310 esclusivamente in rete privata Docker; il protocollo non offre autenticazione o cifratura. Negli ambienti cloud occorrono controlli di rete e gestione delle firme.

## Evidenza
- Test Red di Business `MalwareAdmissionTests` prima dell'implementazione; Green in `FileIngestionService`, ClamdFileMalwareScanner e Compose.
- Test ClamAV reale con EICAR, Clean, errore di protocollo e scanner down, Playwright upload EICAR con token JWT OAuth e audit denied; CI obbligatoria.

## Rischi residui
- Il buffer in memoria è una forma di isolamento *pre-storage*, non una quarantena persistente e non garantisce retention, deep inspection, protezione archivi/zip bomb o risposta alle versioni obsolete delle firme. La demo Compose usa firme precaricate e disabilita gli aggiornamenti automatici; **vietato replicare in produzione**.
- Il recupero da crash fra PUT S3/Blob e COMMIT PostgreSQL, l'outbox e i retry idempotenti rimangono aperti (#21); download tenant-scoped e limiti di concorrenza/retention (#5/#11/#19), provisioning antivirus Azure/On-prem e aggiornamento continuo firme (#17/#18).
