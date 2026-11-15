# Ingestion documentale — primo hardening (#2)

REST: POST /api/files (multipart file, periziaId opzionale); MCP: tool ingest_file con base64 fino a 4 MiB. Entrambi invocano FileIngestionService nel layer Business.

## Ammissione file
- OAuth peritial.write e tenant firmato obbligatori; ID perizia legato al tenant e controllato prima di leggere lo stream.
- Lunghezza dichiarata positiva, massimo 25 MiB; lettura delimitata alla lunghezza dichiarata + 1 byte anche su stream non seekable. Il payload è bufferizzato in memoria (concorrenza/rate limiting da aggiungere).
- Il numero di byte effettivi deve coincidere esattamente con la lunghezza dichiarata prima di scrivere oggetti o metadata.
- Estensione/MIME consentite: PDF, JPEG, PNG, XML, DOCX. Il nome è normalizzato per percorsi Windows e Unix; il key storage usa UUID e tenant, mai il nome del file.
- Controlli di ammissione: magic bytes per PDF/JPEG/PNG; XML parsato con DTD proibite e XmlResolver null; DOCX richiede ZIP e file strutturali di base. I controlli non certificano che un file sia integro o privo di malware.
- Input non valido genera 400; file non autorizzato 403; perizia fuori dal tenant 404.
- Prima che tutti i controlli passino, nessuna chiamata a IObjectStore e IDocumentRepository.

## Test
- tests/backend/Peritial.Business.Tests/FileIngestionSecurityTests.cs verifica file falsi, DTD/XXE, dimensione troncata/extra, nomi Windows, successo con stream non seekable, scope.
- Tests frontend con OAuth in tests/frontend/authenticated.spec.ts.
- I test CI devono essere verdi prima di chiudere #2.

## Residui bloccanti (#5 e #2)
- Scansione antivirus ClamAV `INSTREAM` ora obbligatoria **in memoria prima di PUT**, con fail-closed: risultati Clean consentono la persistenza, Infected bloccano con HTTP 422 e generano audit document.scan/denied senza filename/payload; assenza, errore, risposta sconosciuta o timeout dello scanner bloccano con HTTP 503. REST e MCP condividono lo stesso Business use case. Questo **non** è ancora quarantena persistente e non sostituisce la validazione profonda e la protezione zip bomb.
- ClamAV TCP ascolta solo sulla rete privata Compose; il protocollo TCP non autentica né cifra. In altri ambienti usare scanner raggiungibile soltanto tramite rete privata/protezioni aggiuntive, mai internet pubblico. L'immagine Compose `clamav/clamav:1.5` include firme pre-caricate con aggiornamento `freshclam` disattivato nella demo: in produzione configurare l'aggiornamento regolare delle firme e monitorarne la freschezza.
- Metadata e audit del successo sono atomici sotto RLS. Dopo verifica strutturale e ClamAV, il servizio scrive prima su PostgreSQL `upload_intents` (tenant_id, document_id, storage_key, timestamp), poi effettua PUT. Se PUT non termina con successo, tenta DELETE e cancella l'intento con timeout di 15 secondi indipendente dal client; se la cancellazione fallisce, l'intento resta per il worker.
- In caso di errore **dopo PUT ma durante la scrittura/il commit PostgreSQL**, NON cancellare immediatamente il blob: l'ACK del commit potrebbe essere stato perso. L'intento resta pendente; il worker dedicato, dopo almeno un'ora, usa row lock `FOR UPDATE SKIP LOCKED`, verifica i metadata del documento sotto il ruolo tecnico ristretto e cancella il blob solo se nessun documento lo referenzia. La scrittura del documento deve consumare lo stesso intento nella propria transazione, impedendo commit dopo cleanup. Se l'eliminazione dello storage fallisce, la transazione del worker non consuma l'intento e riprova in seguito.
- Questo risolve il recovery di upload con un **intento PostgreSQL già scritto**, anche dopo riavvio. Restano fuori dalle garanzie l'idempotenza HTTP/MCP (un retry client può creare un secondo documento), le prove kill/restart full-stack, i PUT bloccati oltre la finestra di un'ora, gli errori non recuperabili della connessione recovery, retention/monitoring/alert, quarantena persistente e migrazioni versionate (#5/#6/#21).
- Introduzione di limite globale di concorrenza/stream su endpoint e verifica REST/MCP attraverso test API reali.

## Limite HTTP prima del Business
ASP.NET Core applica `FormOptions.MultipartBodyLengthLimit = 25 MiB` e una soglia di dimensione complessiva HTTP di 25 MiB + 1 MiB di overhead per l'endpoint POST /api/files. Le richieste che superano i limiti multipart sono restituite come HTTP 413; i payload ammessi sono comunque rivalidati in Business anche per tool MCP o altri ingressi.

Il test Playwright autenticato copre upload multipart valido, PDF falsificato, limite 413 e chiamata MCP con file non valido. Il limite effettivo rimane verificato in CI contro Docker Compose.

## Verifica reale della cancellazione S3

La pipeline Compose invoca `S3ObjectStoreContractTests` in un container .NET 10 contro il vero endpoint S3 SeaweedFS: PUT, lettura tramite SDK AWS, DELETE, DELETE ripetuto e verifica HTTP 404 dell'oggetto assente. Lo stesso job avvia Azurite (profilo `azure-test`) e verifica Put/lettura/Delete/Delete ripetuto su un container **privato** tramite AzureBlobObjectStore. Le credenziali Azurite fisse sono solo dati di test, non credenziali cloud. Questi sono test dei protocolli S3/Azure Blob su emulatori, non un deployment su servizi Azure reali né una prova di recupero persistente dopo crash. Non interpretare il test unitario con finti storage come prova di entrambe le piattaforme.

## Test antivirus

`MalwareAdmissionTests` verifica l'ordine scan → PUT → metadata, il blocco di malware, errori scanner e verdetti non riconosciuti senza scrittura storage. `ClamdScannerContractTests` usa ClamAV reale in Compose per Clean/EICAR; verifica inoltre connessione assente, framing binario `zINSTREAM` e risposta invalida. Playwright invia un DOCX/ZIP minimo contenente un file EICAR esatto, con JWT OAuth write valido, e verifica HTTP 422. Un PDF sintetico con EICAR semplicemente accodato non è un test affidabile del parser PDF: ClamAV può ignorare dati non appartenenti alla struttura. Le verifiche approfondite sui formati/archivi restano aperte. La CI controlla la registrazione dell'audit del rifiuto. Vedere ADR 0010.

## Idempotenza dei retry REST/MCP (#21)

Le chiamate REST multipart possono inviare l'header `Idempotency-Key`; il tool MCP `ingest_file` accetta il parametro opzionale `idempotencyKey`. Sono consentiti valori di 16–128 caratteri ASCII alfanumerici, punto, trattino, underscore o due punti. Una prenotazione della chiave è legata a **tenant e subject JWT firmati**: vengono memorizzati solo SHA-256 del subject, della chiave e dell'impronta canonica di nome file, tipo MIME, periziaId e byte, non la chiave né il file in chiaro.

Prima di PUT la riga `upload_request_keys` è inserita con indice unique PostgreSQL, serializzando richieste concorrenti. Un retry dopo commit recupera il documento originale e non ripete PUT/audit (REST 201 con lo stesso UUID, MCP stesso DTO). Una chiave ancora pendente o riutilizzata con payload/metadati diversi produce HTTP 409 / errore MCP. Il completamento è **nella stessa transazione** di documents, audit e consumo dell'intento. Dopo PUT fallito e cleanup riuscito la chiave viene liberata; dopo un commit incerto resta pendente finché il worker non decide se preservare il documento o eliminare il blob e rilasciare la chiave. Le richieste senza key continuano a funzionare ma non sono idempotenti.

Le prenotazioni completate restano nel database fino a una policy esplicita di retention: eliminandole prematuramente si perderebbe la deduplicazione di retry tardivi. La sicurezza contro kill/restart reale, la scadenza delle prenotazioni, le lease degli upload lunghi e i mutatori non documentali richiedono ulteriori sviluppi; vedere ADR 0012 e #21.

## Lease rinnovabili durante upload lenti (ADR 0013)

`upload_intents.lease_expires_at_utc` è impostato da PostgreSQL a `now()+2 minuti` e aggiornato ogni 20 secondi durante PUT. Non è il timestamp della richiesta HTTP: il rinnovo è transazionale e tenant-scoped. La scrittura documenti/audit consuma l'intento **solo con lease valida**; una perdita del lease annulla il token di PUT e blocca il commit. Il worker di recovery agisce soltanto su intenti creati più di un'ora fa **e** con lease scaduta secondo PostgreSQL. Il ruolo `peritial_app` può aggiornare solo `lease_expires_at_utc`; non sono consentiti aggiornamenti indiscriminati alle colonne delle intenzioni.

La CI testa lease, scadenze e concorrenza su PostgreSQL. Il test Compose SIGKILL interrompe l'API, prepara un orphan su S3/PG dopo l'arresto, riavvia l'API e verifica che il worker lo ripulisca con idempotency key liberata. Non simula ancora un PUT HTTP vivo interrotto a metà, per il quale rimane aperta la fault injection e il failover multi-replica della #21.
