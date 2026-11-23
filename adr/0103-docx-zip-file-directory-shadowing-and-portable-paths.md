# ADR 0103 — DOCX ZIP: bloccare file/directory shadowing e alias di path

- Data: 23 novembre 2026
- Stato: implementato su `main`; Compose e CI terminale dell'HEAD da attestare
- Issue: #5 sicurezza documentale; #4 condivisione del confine Business REST/MCP
- Branch legacy `death`: solo storico, non modificato

## Problema

Il controllo DOCX in `UploadedDocumentValidation` già rifiuta entry ZIP
speciali Unix, nomi con controlli, traversal, duplicati case-insensitive,
XML non sicuro e dimensioni ZIP eccessive (ADR 0100–0102).
Due ambiguità restavano possibili senza essere rilevate:

1. Un file `word/media/picture.bin` può essere presente insieme alla
   entry `word/media/picture.bin/child.dat`. Non possono coesistere
   come file regolare e directory su un filesystem ordinario.
   L'ordine delle entry non deve influenzare l'ammissione.
2. Un segmento `word/media/report.` o `word/media/report `, anche
   intermedio, può essere risolto con semantica differente da
   estrattori e filesystem che normalizzano punti/spazi finali.

Il backend attuale non estrae DOCX sul filesystem; tuttavia
l'ingestione deve evitare di accettare ambiguità sfruttabili
da converter, renderer o strumenti successivi.

## Decisione

Durante la prima passata della directory centrale, prima di
decompressione, antivirus e persistenza:

- rifiutare ogni segmento ZIP che termina con `.` o spazio ASCII;
- mantenere un insieme `filePaths` dei file e un insieme
  `directoryPaths` dei path che sono directory esplicite o
  implicitamente richieste dalla presenza di figli;
- per ogni entry nuova, rifiutare il caso in cui sia un file
  coincidente con una directory implicita;
- rifiutare anche se uno dei parent della nuova entry è già
  registrato come file;
- confrontare i nomi con `StringComparer.OrdinalIgnoreCase`
  come per la protezione contro i duplicati esistente;
- ammettere directory ZIP esplicite coerenti con i figli.
  Nessuna estrazione, path locale creato, cambio di schema
  o modifica ai limiti ZIP.

L'errore è un generico `ArgumentException`, restituito
come HTTP 400 REST / errore MCP prima di ClamAV, S3/Azure,
metadata e audit `document.ingest/succeeded`.

## Evidenza TDD e verifiche

- RED: [32818ed](https://github.com/KeyserDSoze/Peritial/commit/32818edc8d52e22ce7fc88c2e98185a491e78088);
  [CI #45182651035](https://github.com/KeyserDSoze/Peritial/actions/runs/45182651035).
  **6 fallimenti Business previsti**: file-parent prima/dopo il
  figlio e quattro nomi con punto/spazio terminale, sia leaf sia
  segmento intermedio. **178 Business preesistenti**, 23 Domains
  e 201 Infrastructure verdi. La prova di directory normale
  con figlio è rimasta verde.
- Fix: [896260d](https://github.com/KeyserDSoze/Peritial/commit/896260d5c44fe9f665b0ec057977c0ab12b6ca7f);
  [CI #45182762546](https://github.com/KeyserDSoze/Peritial/actions/runs/45182762546):
  backend **408/408 PASS** (23 Domains + 184 Business + 201 Infrastructure).
  Questo è l'esito del job backend, non la certificazione della
  pipeline full-stack terminale.
- E2E aggiunto: [63a9a5a](https://github.com/KeyserDSoze/Peritial/commit/63a9a5a93413fbc258ceb815f4faa7a26488831b),
  il test Keycloak REST/MCP trasforma un ZIP sintetico valido
  in un membro con punto finale mantenendo lo stesso nome nel local
  header e central directory. Il membro viene marcato come
  file Unix regolare; REST deve rispondere 400 e MCP
  `isError:true`, senza disclosure del contenuto.
  Il passaggio Compose va confermato sui job terminali.

## Residui

Questi controlli fanno parte dell'ammissione, non costituiscono
un parser semantico OOXML né un antivirus. Non verificano ancora
collisioni dovute a normalizzazione Unicode specifica del filesystem,
oggetti incorporati/relazioni remote, firme e producer ZIP non dichiarati.
Restano bloccanti in #5 quarantena duratura, riscan e approvazioni,
retention/legal hold, aggiornamenti ClamAV verificati sul provider,
budget/rate limit multi-replica e test di deployment Azure/on-prem.
Il commit import XML pubblico resta disabilitato secondo #10/#24.
