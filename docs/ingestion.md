# Ingestion documentale — primo hardening (#2)

REST: POST /api/files (multipart file, periziaId opzionale); MCP: tool ingest_file con base64 fino a 4 MiB. Entrambi invocano FileIngestionService nel layer Business.

## Ammissione file
- OAuth peritial.write e tenant firmato obbligatori; ID perizia legato al tenant e controllato prima di leggere lo stream.
- Lunghezza dichiarata positiva, massimo 25 MiB; lettura delimitata alla lunghezza dichiarata + 1 byte anche su stream non seekable. Il payload è bufferizzato in memoria (concorrenza/rate limiting da aggiungere).
- Il numero di byte effettivi deve coincidere esattamente con la lunghezza dichiarata prima di scrivere oggetti o metadata.
- Estensione/MIME consentite: PDF, JPEG, PNG, XML, DOCX. Il nome è normalizzato per percorsi Windows e Unix; il key storage usa UUID e tenant, mai il nome del file.
- Controlli di ammissione: magic bytes per PDF/JPEG/PNG; XML parsato con DTD proibite e XmlResolver null; DOCX richiede ZIP e file strutturali di base. **Hardening DOCX ZIP prima dello scanner e dello storage:** massimo 1024 entry, 25 MiB decompresse per parte, 64 MiB per archivio, rapporto di espansione massimo 200:1 per entry di almeno 1 MiB, percorsi assoluti/traversal/backslash e nomi duplicati senza distinzione maiuscole/minuscole rifiutati; tutte le entry sono effettivamente decompresse con limiti, senza fidarsi dei soli metadati ZIP. I controlli non certificano che un file sia integro o privo di malware.
- Input non valido genera 400; file non autorizzato 403; perizia fuori dal tenant 404.
- Prima che tutti i controlli passino, nessuna chiamata a IObjectStore e IDocumentRepository.

## Download REST tenant-scoped (prima tranche #5/#11)

`GET /api/files/{id}/download` richiede OAuth `peritial.read` oppure
`peritial.write` sul JWT, risolve il documento e la chiave privata
**solo** tramite un SELECT PostgreSQL tenant-scoped con FORCE RLS e
verifica la chiave canonica `tenant/UUID.estensione`. ID mancanti/
di altri tenant restituiscono 404; token assente 401; scope assente
403. La risposta è un allegato `application/octet-stream`, mai
inline o un redirect/presigned URL S3/Azure, con cache disabilitata,
`nosniff` e sandbox CSP.

SeaweedFS S3 e Azure Blob/Azurite sono letti con API private del
provider; il server rifiuta il file se i byte non coincidono con
`documents.length_bytes` o superano i **25 MiB**. Anche il filename
persistito deve essere canonico prima di usarlo negli header.
Prima di consegnare il buffer, il server scrive un audit append-only
`document.download/succeeded` con tenant/subject/UUID e **senza**
nome file, storage key, byte o URL. Un fallimento dell'audit blocca
l'invio. Il successo dell'audit significa **autorizzazione e byte pronti**
prima della risposta HTTP, non prova che il client abbia ricevuto
interamente il corpo.

La PWA ora offre il pulsante di download per ciascun documento presente
nella proiezione autenticata: richiede il JWT via fetch (mai in URL), una
sola richiesta per volta per scheda, massimo 25 MiB in memoria, verifica
MIME e lunghezza effettiva, annulla download su logout/offline/filtro
o refresh e non offre alcun link verso S3/Azure. Il browser salva un
Blob locale temporaneo; l'audit document.download/succeeded resta una
prova di preparazione del corpo, non della consegna completa al browser.

Restano aperti: rate limiting **server-side**, budget aggregato di memoria
tra schede/client e repliche, governance retention/erasure/quarantena/riscan,
verifica reale Azure/Entra, privacy dei file scaricati dall'utente e
policy di revoca durante una risposta già autorizzata.
MCP continua a fornire soltanto metadata. Vedere
[ADR 0094](../adr/0094-tenant-scoped-audited-document-download.md),
`DocumentDownloadTests`, `DocumentCatalogRepositoryTests`,
contratti S3/Azurite e Playwright autenticato.

## Test
- tests/backend/Peritial.Business.Tests/FileIngestionSecurityTests.cs verifica file falsi, DTD/XXE, dimensione troncata/extra, nomi Windows, successo con stream non seekable, scope e la nuova ammissione DOCX valida/blocco zip bomb, traversal e nomi duplicati **prima di scanner, PUT e metadata**.
- Tests frontend con OAuth in tests/frontend/authenticated.spec.ts.
- I test CI devono essere verdi prima di chiudere #2.

## Residui bloccanti (#5 e #2)
- Scansione antivirus ClamAV `INSTREAM` ora obbligatoria **in memoria prima di PUT**, con fail-closed: risultati Clean consentono la persistenza, Infected bloccano con HTTP 422 e generano audit document.scan/denied senza filename/payload; assenza, errore, risposta sconosciuta o timeout dello scanner bloccano con HTTP 503. REST e MCP condividono lo stesso Business use case. Questo **non** è ancora quarantena persistente: il limite di espansione e struttura ZIP riduce alcuni zip bomb DOCX, ma non sostituisce il parsing OOXML completo, protezioni contro documenti malformati diversi da DOCX o aggiornamento delle firme antivirus.
- ClamAV TCP ascolta solo sulla rete privata Compose; il protocollo TCP non autentica né cifra. In altri ambienti usare scanner raggiungibile soltanto tramite rete privata/protezioni aggiuntive, mai internet pubblico. L'immagine Compose `clamav/clamav:1.5` include firme pre-caricate con aggiornamento `freshclam` disattivato nella demo: in produzione configurare l'aggiornamento regolare delle firme e monitorarne la freschezza.
- Metadata e audit del successo sono atomici sotto RLS. Dopo verifica strutturale e ClamAV, il servizio scrive prima su PostgreSQL `upload_intents` (tenant_id, document_id, storage_key, timestamp), poi effettua PUT. Se PUT non termina con successo, tenta DELETE e cancella l'intento con timeout di 15 secondi indipendente dal client; se la cancellazione fallisce, l'intento resta per il worker.
- In caso di errore **dopo PUT ma durante la scrittura/il commit PostgreSQL**, NON cancellare immediatamente il blob: l'ACK del commit potrebbe essere stato perso. L'intento resta pendente; il worker dedicato, dopo almeno un'ora, usa row lock `FOR UPDATE SKIP LOCKED`, verifica i metadata del documento sotto il ruolo tecnico ristretto e cancella il blob solo se nessun documento lo referenzia. La scrittura del documento deve consumare lo stesso intento nella propria transazione, impedendo commit dopo cleanup. Se l'eliminazione dello storage fallisce, la transazione del worker non consuma l'intento e riprova in seguito.
- Questo risolve il recovery di upload con un **intento PostgreSQL già scritto**, anche dopo riavvio. Il retry idempotente è disponibile se il client invia una chiave REST/MCP (ADR 0012), mentre resta non idempotente senza chiave. Il recovery e le lease sono verificati anche con prove SIGKILL (ADR 0013/0014); restano outage PostgreSQL prolungati, failover multi-replica, retention/monitoring/alert, quarantena persistente e migrazioni versionate (#5/#6/#21).
- Introduzione di limite globale di concorrenza/stream su endpoint e verifica REST/MCP attraverso test API reali.

### Firme ClamAV: controllo dell'età (P0 #5)

Fuori da `Development` l'API richiede **`Malware__Clamd__MaxSignatureAgeHours`** (intero da 1 a 168, ad esempio `48` da tarare sul proprio servizio). L'assenza o un valore invalido impedisce l'avvio. Prima di trasmettere file a `INSTREAM`, lo scanner richiede `zVERSION` sul TCP privato e controlla versione positiva e timestamp del database caricato: database oltre soglia, date future di oltre 5 minuti, risposta malformata/`COMMAND UNAVAILABLE`, errore o timeout **bloccano l'upload prima del file scan**, con HTTP 503 e senza PUT/metadata. La readiness, dopo PING, controlla anch'essa VERSION e riporta HTTP 503 se le firme sono vecchie; la liveness rimane separata.

Il daemon clamd **deve usare timezone UTC** (`TZ=UTC`): la data di VERSION non include un offset. Il comando prova solo la **data del database caricato** e non aggiorna le firme, non dimostra che la distribuzione remota sia raggiungibile o che non vi siano manipolazioni. In un deployment reale servono FreshClam periodico, reload verificato di clamd, volume firme aggiornabile e persistente, osservabilità/alert sulla frequenza delle verifiche e restrizioni di rete su TCP 3310. L'alert ClamAV di #19 è applicabile ma non rappresenta di per sé un aggiornamento riuscito.

Il **laboratorio opt-in con FreshClam periodico, volume persistente, UTC e soglia di età attiva** è descritto in [clamav-signature-updates.md](clamav-signature-updates.md); il test CI dell'overlay è configurativo e non scarica firme da Internet. Non confondere la prova di un database caricato recente con l'attestazione di un aggiornamento remoto riuscito.

La Compose locale usa firme precaricate e `CLAMAV_NO_FRESHCLAMD=true`: **solo in Development** è consentito non impostare la soglia (controllo freshness disabilitato per la demo, non antivirus bypass). I test unitari e i contratti TCP reali del mock scanner verificano parsing e blocco prima di INSTREAM; non equivalgono a un aggiornamento FreshClam o a un test di deploy produzione. Quarantena e contenuti annidati, download PWA/MCP binario, governance e limiti di traffico rimangono aperti; esiste ora una prima route REST autorizzata e auditata (vedi sopra).

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

La CI testa lease, scadenze e concorrenza su PostgreSQL. Il test Compose SIGKILL interrompe l'API, prepara un orphan su S3/PG dopo l'arresto, riavvia l'API e verifica che il worker lo ripulisca con idempotency key liberata. Il nuovo test Compose (ADR 0014) intercetta invece un vero PUT firmato dell'API, inoltra i primi byte del corpo verso SeaweedFS, interrompe l'API con SIGKILL mentre la richiesta è bloccata, e verifica cleanup dopo restart. Restano multi-replica, outage database e fault injection su Azure Blob reale.


## DOCX: XML annidato e relazioni OPC validati prima dell'upload (ADR 0100)

Il controllo DOCX è applicato nella fase di ammissione dei
byte a `FileIngestionService`, **prima** di chiamare ClamAV,
lo storage e il repository PostgreSQL. I precedenti limiti ZIP
(1024 entry, fino a 25 MiB espansi per parte, 64 MiB complessivi,
compression ratio massimo 200 per parti ≥1 MiB) restano invariati.
Dopo la verifica delle dimensioni reali decompresse, ogni entry il
cui nome termina in `.xml` o `.rels` (case-insensitive) viene
letta con un `XmlReader` che proibisce DTD, non risolve entità
esterne e richiede documento completo XML con root. Il buffer
delle parti XML è temporaneo, uno alla volta, e non contiene
più dei byte permessi per la singola entry. Le altre entry
continuano a essere decompresse con limite ma non interpretate.

In caso di XML malformato o DTD, l'ingestione fallisce con
`ArgumentException` senza echo di URL, XML o dati del documento
nel messaggio verso REST/MCP; scanner, PUT e metadati non vengono
avviati. Ciò non prova la semantica OOXML, la sicurezza di
relazioni `TargetMode=External`, o che un documento valido sia
pulito da malware. ClamAV fail-closed resta obbligatorio per
file ammessi, la quarantena persistente resta da implementare (#5).
Test Business coprono DTD in `word/header1.xml`, `.rels`,
malformazione XML e maiuscole dell'estensione, più DOCX
valido. La CI Compose verifica il comportamento HTTP 400 REST
e `isError` MCP con JWT firmato e nessuna fuoriuscita
del marker della URI esterna.
[ADR 0100](../adr/0100-docx-opc-xml-admission-before-malware-scan.md).


## DOCX: blocco di symlink, FIFO, socket e device in entry ZIP (ADR 0101)

Dopo i controlli di percorso e budget DOCX, il validatore
esamina gli `ExternalAttributes` presenti per ogni entry
del central directory ZIP. Nei bit Unix del tipo file
(accennati dai 16 bit alti) ammette solo un file regolare
(`0x8000`), una directory (`0x4000`) o l'assenza di
informazione sul tipo (`0`, tipica di alcuni ZIP Windows).
Tipi speciali `0xA000` symlink, `0x1000` FIFO,
`0x2000` char device, `0x6000` block device,
`0xC000` socket e qualsiasi altro tipo Unix inatteso
vengono rifiutati **prima** di scanner, blob e metadata.
L'archivio non è estratto e la protezione serve anche per
futuri converter/renderer che potrebbero farlo.

I test in `FileIngestionSecurityTests` creano ZIP reali
marcando le entry con mode Unix nella directory centrale,
provano cinque rifiuti senza alcuna scan/PUT/DB e
l'accettazione di un file normale. Il test OAuth Keycloak
esercita le route REST multipart e MCP base64 con lo stesso
DOCX sintetico symlink: HTTP 400 / `isError:true`, senza
rivelare il target del link. Restano non verificati
l'interpretazione di tutti i formati ZIP, i link esterni OOXML
e gli scenari produttivi; i requisiti di #5 non sono conclusi.
[ADR 0101](../adr/0101-docx-zip-unix-special-entry-admission.md).

## DOCX: nomi ZIP controllati e tipo Unix coerente (ADR 0102)

I nomi delle entry ZIP nel DOCX non possono includere TAB, CR, LF o
altri caratteri di controllo. Il tipo file Unix eventualmente dichiarato
deve corrispondere al percorso: un file regolare non può terminare con
`/` e una directory Unix non può essere dichiarata su un nome senza
`/`. Rimangono accettati i tipi non dichiarati dai writer ZIP legacy,
senza indebolire i controlli precedenti su traversal, XML o dimensioni.

I test Business verificano tre nomi con TAB/CR/LF e un file marcato
directory, con scanner, storage, metadata e audit successo mai invocati.
La prova Keycloak REST/MCP usa un DOCX ZIP sintetico con metadati Unix
contraddittori e controlla HTTP 400 / `isError:true`. Questo non
copre la semantica OOXML completa né elimina i gate produttivi #5.
[ADR 0102](../adr/0102-docx-zip-entry-name-control-and-unix-type-consistency.md).
