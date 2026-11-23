# ADR 0104 — DOCX ZIP: bloccare nomi DOS riservati e directory con dati

- Data: 23 novembre 2026
- Stato: implementato su `main`; CI full-stack HEAD da attestare
- Issue: #5 document security, #4 REST/MCP auth/shared Business
- `death`: esclusivamente archivio storico, nessuna scrittura

## Problema

L'ammissione DOCX controlla già dimensioni ZIP, directory implicite,
nomi duplicati, traversal, caratteri di controllo, tipologie Unix,
parti XML sicure e relazioni `.rels` (ADR 0100–0103).
Tuttavia un membro come `word/media/COM1.bin` o `word/media/NUL`
non è un file normale su Windows: i nomi riservati dei dispositivi
DOS possono essere interpretati diversamente dai renderer e dagli
estrattori futuri, con o senza estensione e indipendentemente dalle
maiuscole. Anche un membro chiamato come directory (`word/media/`)
che contiene byte può essere interpretato in modi differenti.

L'attuale server non estrae l'archivio sul filesystem e ClamAV
resta obbligatorio, ma la persistenza di queste strutture
ambigue non è necessaria per i documenti ammessi.

## Decisione

Durante la prima passata della directory centrale ZIP, prima di
decomprimere, scansionare o scrivere:

- rilevare, in **ogni segmento** ZIP, il nome base prima del primo
  punto; rifiutare `CON`, `PRN`, `AUX`, `NUL`, `CONIN$`,
  `CONOUT$`, `COM1`–`COM9` e `LPT1`–`LPT9`
  (case-insensitive), anche con estensioni;
- includere i numeri apice `¹`, `²`, `³` nelle forme
  `COM`/`LPT`, riconosciute anche come cifre da Windows;
- lasciare ammessi nomi non riservati come `COM0`, `LPT10`
  e `consolidato`;
- per un membro il cui percorso termina in `/`, imporre
  `entry.Length == 0`: una directory non deve trasportare
  dati ZIP propri;
- continuare a verificare le lunghezze reali decompresse nella
  seconda passata; i metadati central-directory non bastano per
  attestare l'assenza di contenuti.

La validazione è nel layer Business `UploadedDocumentValidation`,
condiviso da upload REST e `ingest_file` MCP. Un nome o tipo
ambiguo provoca `ArgumentException` prima di antivirus, intent,
PUT S3/Blob, metadati e audit di successo; nessun payload o nome
ostile compare nel messaggio. Nessuna modifica di autorizzazioni,
schema, migrazioni, limite 25 MiB o branch storico.

## Evidenza test-first

- Commit test RED
  [6a39fe5](https://github.com/KeyserDSoze/Peritial/commit/6a39fe584833f7a85524559485ad871ca0f770c6),
  [CI #45189987946](https://github.com/KeyserDSoze/Peritial/actions/runs/45189987946):
  **10 test Business FAIL previsti** sul vecchio validatore.
  Un test di `AUX.xml` passava erroneamente già prima della
  correzione perché i byte di prova erano XML malformato:
  non usarlo come prova RED del controllo del nome.
  I 189 test Business già soddisfatti, 23 Domain e 201
  Infrastructure continuavano a passare.
- Correzione
  [fc11727](https://github.com/KeyserDSoze/Peritial/commit/fc11727e34c249b8b5f706c4e1c52d046448ebc3),
  [CI #45190093248](https://github.com/KeyserDSoze/Peritial/actions/runs/45190093248):
  job backend **423/423 PASS** (23 Domains, 199 Business,
  201 Infrastructure), con 10 nomi DOS e un membro directory
  che porta byte rifiutati, più quattro nomi innocui ammessi.
- Test `AUX.xml` corretto in
  [cf7ea0e](https://github.com/KeyserDSoze/Peritial/commit/cf7ea0e8b4c2f9ac3e89abbbecd84eff21f607e5):
  il contenuto del membro XML è valido, così il test dimostra
  la regola sul nome e non il parser XML preesistente.
- E2E OAuth Keycloak REST/MCP in
  [c003fc5](https://github.com/KeyserDSoze/Peritial/commit/c003fc5caf675356cdf00e09e3b97401c697e982):
  DOCX ZIP a tre entry con nome `COM1.bin` alterato nei local
  e central header, regular file Unix, dimensioni/CRC invariati.
  Il test richiede REST HTTP 400 e MCP `isError:true`,
  senza divulgazione del contenuto. Verificare job Compose
  terminale prima di attestare questo E2E.

## Limiti e operazioni successive

Questa è una regola di **ammissione**, non estrazione o scansione
malware, e non certifica sicurezza completa OOXML o behavior
di converter terzi. Restano aperti quarantena persistente,
approval/riscan e retention, attestazione reale FreshClam/Clamd
su Azure/on-prem, rate limiting e memoria aggregata multi-replica,
validazione di relazioni esterne e oggetti incorporati.
La issue #5 deve rimanere aperta; non creare duplicati per questi
sottoinsiemi già tracciati.
