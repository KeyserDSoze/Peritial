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
- Metadata PostgreSQL + audit success sono atomici sotto RLS. Se PUT dello storage (anche parziale) o commit PostgreSQL fallisce, FileIngestionService tenta di eliminare la chiave appena generata usando IObjectStore.DeleteAsync (implementato per S3/SeaweedFS e Azure Blob); il cleanup ignora la cancellazione della richiesta in ingresso ed è limitato a 15 secondi. In caso di errore del cleanup, l'eccezione aggregata include errore primario ed errore di cancellazione, senza dichiarare successo. I test xUnit coprono upload parziale, errore DB, cancellazione fallita, request cancellata e retry dopo cleanup.
- Questa compensazione è **best-effort**, non è un outbox. Un crash di processo prima del cleanup lascia comunque un oggetto orfano; un esito di commit DB incerto (ack perso) può rendere pericoloso cancellare senza riconciliare il metadata. Mancano staging/quarantena, antivirus, idempotency key, scansione storage, retry persistente, rate limiting e test reali di entrambi gli adapter Azure/S3 (#5/#21).
- Introduzione di limite globale di concorrenza/stream su endpoint e verifica REST/MCP attraverso test API reali.

## Limite HTTP prima del Business
ASP.NET Core applica `FormOptions.MultipartBodyLengthLimit = 25 MiB` e una soglia di dimensione complessiva HTTP di 25 MiB + 1 MiB di overhead per l'endpoint POST /api/files. Le richieste che superano i limiti multipart sono restituite come HTTP 413; i payload ammessi sono comunque rivalidati in Business anche per tool MCP o altri ingressi.

Il test Playwright autenticato copre upload multipart valido, PDF falsificato, limite 413 e chiamata MCP con file non valido. Il limite effettivo rimane verificato in CI contro Docker Compose.

## Verifica reale della cancellazione S3

La pipeline Compose invoca `S3ObjectStoreContractTests` in un container .NET 10 contro il vero endpoint S3 SeaweedFS: PUT, lettura tramite SDK AWS, DELETE, DELETE ripetuto e verifica HTTP 404 dell'oggetto assente. Lo stesso job avvia Azurite (profilo `azure-test`) e verifica Put/lettura/Delete/Delete ripetuto su un container **privato** tramite AzureBlobObjectStore. Le credenziali Azurite fisse sono solo dati di test, non credenziali cloud. Questi sono test dei protocolli S3/Azure Blob su emulatori, non un deployment su servizi Azure reali né una prova di recupero persistente dopo crash. Non interpretare il test unitario con finti storage come prova di entrambe le piattaforme.

## Test antivirus

`MalwareAdmissionTests` verifica l'ordine scan → PUT → metadata, il blocco di malware, errori scanner e verdetti non riconosciuti senza scrittura storage. `ClamdScannerContractTests` usa ClamAV reale in Compose per Clean/EICAR; verifica inoltre connessione assente, framing binario `zINSTREAM` e risposta invalida. Playwright invia un DOCX/ZIP minimo contenente un file EICAR esatto, con JWT OAuth write valido, e verifica HTTP 422. Un PDF sintetico con EICAR semplicemente accodato non è un test affidabile del parser PDF: ClamAV può ignorare dati non appartenenti alla struttura. Le verifiche approfondite sui formati/archivi restano aperte. La CI controlla la registrazione dell'audit del rifiuto. Vedere ADR 0010.
