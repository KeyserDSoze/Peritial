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
- Antivirus e quarantena, validazione profonda del contenuto e protezione da zip bomb.
- Metadata PostgreSQL + audit success sono atomici sotto RLS. Se PUT dello storage (anche parziale) o commit PostgreSQL fallisce, FileIngestionService tenta di eliminare la chiave appena generata usando IObjectStore.DeleteAsync (implementato per S3/SeaweedFS e Azure Blob); il cleanup ignora la cancellazione della richiesta in ingresso ed è limitato a 15 secondi. In caso di errore del cleanup, l'eccezione aggregata include errore primario ed errore di cancellazione, senza dichiarare successo. I test xUnit coprono upload parziale, errore DB, cancellazione fallita, request cancellata e retry dopo cleanup.
- Questa compensazione è **best-effort**, non è un outbox. Un crash di processo prima del cleanup lascia comunque un oggetto orfano; un esito di commit DB incerto (ack perso) può rendere pericoloso cancellare senza riconciliare il metadata. Mancano staging/quarantena, antivirus, idempotency key, scansione storage, retry persistente, rate limiting e test reali di entrambi gli adapter Azure/S3 (#5/#21).
- Introduzione di limite globale di concorrenza/stream su endpoint e verifica REST/MCP attraverso test API reali.

## Limite HTTP prima del Business
ASP.NET Core applica `FormOptions.MultipartBodyLengthLimit = 25 MiB` e una soglia di dimensione complessiva HTTP di 25 MiB + 1 MiB di overhead per l'endpoint POST /api/files. Le richieste che superano i limiti multipart sono restituite come HTTP 413; i payload ammessi sono comunque rivalidati in Business anche per tool MCP o altri ingressi.

Il test Playwright autenticato copre upload multipart valido, PDF falsificato, limite 413 e chiamata MCP con file non valido. Il limite effettivo rimane verificato in CI contro Docker Compose.
