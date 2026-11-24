# Recovery bundle cifrato PostgreSQL + SeaweedFS S3 — laboratorio

**Ambito:** tranche #6, ADR 0022 e ADR 0023 (staging volatile obbligatorio). Questo procedimento è un **drill offline** su componenti locali: non fornisce backup schedulati, storage offsite, procedura di failover produttivo, snapshot DB+S3 sotto traffico né backup Keycloak/Azure Blob. Si lavora esclusivamente su `main`; `death` è storia.

## Formato e proprietà verificate

Il comando `seal` richiede un dump PostgreSQL **custom PGDMP** con file adiacente `.sha256`, e un archive S3 creato da `Peritial.RecoveryCheckpoint export` con `manifest.json`, `manifest.hmac` e `objects/*.blob`. Prima della cifratura controlla digest PostgreSQL, MAC del manifest S3 e SHA-256 di ogni blob rispetto al manifest autenticato.

L'output è una **nuova directory privata** con `index.json`, `index.hmac` e file `payload/00000000.enc` ecc. `index.json` rivela *solo* i percorsi tecnici e la dimensione dei file, non identificativi tenant o storage key. L'index è autenticato con HMAC-SHA256 da una sottochiave derivata dalla chiave AES esterna e da salt casuale (32 byte); non è cifrato. I byte dei file sono cifrati con **AES-256-GCM**, per blocchi da 1 MiB e tag da 16 byte. La chiave di ciascun file è derivata da HMAC-SHA256 con salt di bundle, contesto di protocollo e indice file; il nonce è il numero progressivo del blocco (unico per chiave derivata). L'AAD autentica il digest dell'index, l'indice file e il blocco. La verifica rifiuta file mancanti o extra, troncamenti, MAC non valido, lunghezze inattese, path non canonici e ciphertext modificato.

`unseal` **non pubblica** una destinazione anche parzialmente decifrata quando fallisce la verifica. Decifra in una nuova directory temporanea privata e la rinomina nella destinazione richiesta soltanto dopo controllo AES-GCM di tutti i blocchi, SHA-256 del PostgreSQL e HMAC del manifest S3. La directory decifrata **contiene dati personali e documenti in chiaro**. Non usare destinazioni condivise o permanenti.

### Preflight capacità del bundle sigillato (ADR 0116)

Dopo autenticazione HMAC dell'indice esterno, `unseal` e la verifica cifrata
usata dal trasferimento offsite controllano **prima di decifrare** la quota
S3 già applicata dal bundle interno: massimo 10.000 documenti e 512 MiB
complessivi dei soli blob, con lunghezze positive. Verificano anche i limiti
dei sidecar e del manifest. Le directory radice e payload vengono enumerate
al massimo fino alla prima entry eccedente. Non cambia il formato v1.
Questi limiti non includono il PGDMP e non costituiscono una garanzia di
capacità complessiva del tmpfs. Il CLI checkpoint consente override, ma
il bundle sigillato continua a usare i limiti default.

## Vincolo obbligatorio sul plaintext: Linux tmpfs privato e swap disabilitato (ADR 0023)

Le operazioni `ObjectRecoveryArchive.ExportAsync`, `ObjectRecoveryArchive.ImportAsync`, `SealedRecoveryBundle.SealAsync` e `SealedRecoveryBundle.UnsealAsync` ora **rifiutano** file sorgenti e destinazioni temporanee in chiaro quando non risiedono su un vero mount **`tmpfs` Linux**, in una directory privata (permessi `0700` o più restrittivi), senza componenti symlink e con **nessuno swap attivo** visibile in `/proc/swaps`. Il tipo di filesystem viene ricavato dal mount **più specifico** di `/proc/self/mountinfo`: una directory chiamata `/dev/shm` non basta da sola. Se il sistema operativo non è Linux, la modalità plaintext fallisce chiusa finché non è disponibile un backend scratch crittografato specificamente verificato.

Preparare una directory privata **prima di eseguire pg_dump o esportare S3**, non dopo che il dump è già stato scritto su disco:

```bash
# Su host di manutenzione Linux sotto controllo dell'amministratore:
# rendere inaccessibile lo swap durante l'intera finestra.
cat /proc/swaps                    # nessun dispositivo dopo l'intestazione
test "$(stat -f -c %T /dev/shm)" = tmpfs
umask 077
mkdir -m 0700 /dev/shm/peritial-recovery-001
export PERITIAL_BACKUP_FILE=/dev/shm/peritial-recovery-001/peritial.dump
export PERITIAL_ARCHIVE_DIR=/dev/shm/peritial-recovery-001/objects
```

Il comando `scripts/db/backup.sh` è utilizzabile **anche autonomamente** per dump in chiaro su filesystem persistente: il suo comportamento non è stato modificato. **Per l'intera procedura cifrata, scegliere prima il percorso tmpfs**, poi sigillare con `seal`, e conservare durevolmente soltanto il bundle cifrato. Nel restore creare un'altra directory privata `tmpfs` di destinazione e passarla a `PERITIAL_BUNDLE_UNSEAL_DIR`. Non usare `/tmp` persistente o una directory di lavoro Git. Il bundle *cifrato* (`PERITIAL_BUNDLE_DIR`) può stare su un disco durabile, purché gestito con policy di backup/offsite.

In CI lo swap del runner effimero viene disattivato esplicitamente; il test usa una directory `mktemp` privata sotto il `tmpfs` host, bind-mounted nel container SDK. La copia del PGDMP da `0700` richiede privilegi CI senza abbassare le ACL. Un `tmpfs` di dimensione insufficiente produce errore di spazio e **non** autorizza fallback su disco; pianificare RAM/capacity per il set completo. Lo swap potrebbe essere riattivato da un amministratore **dopo** il controllo: è necessario mantenerlo disabilitato per tutta la finestra. Questo modello non difende da root, memory dumps, host compromise, processi concorrenti o perdita delle chiavi e non prova sicurezza produttiva.

## Prerequisiti: due chiavi distinte, sempre esterne al bundle

- Chiave indipendente di **32 byte casuali** per AES-GCM, in file segreto di sola lettura/scrittura owner `0600` o più restrittivo: `PERITIAL_BUNDLE_AES_KEY_FILE`.
- Chiave indipendente di **32 byte casuali** per HMAC del manifest S3, in altro file segreto `0600`: `PERITIAL_ARCHIVE_HMAC_KEY_FILE`. **Non riutilizzare la stessa chiave**; i due comandi la rifiutano.
- Conservare/rotare le chiavi tramite KMS/secret manager fuori dal backup (su destinazione e periodo di retention). Se una chiave è persa, il backup diventa irrecuperabile; se è compromessa, confidenzialità/autenticità del bundle sono compromesse. Non committare o copiare chiavi con l'archive, non passarle su argv.
- `PERITIAL_BUNDLE_CONFIRM=WRITERS_STOPPED` durante la sigillatura e `ISOLATED_RESTORE` durante apertura. Queste conferme **non provano** che i writer siano davvero fermati. Fermare REST/MCP, recovery worker, job, upload S3 e scritture DB prima di ottenere il dump e l'archive.
- In laboratorio si possono creare i file con `umask 077; openssl rand -out /secure/keys/pg-s3-bundle.key 32` e un comando analogo per `s3-manifest.key`. In produzione serve KMS/secret manager; **non** gestire le chiavi in chiaro in Docker Compose demo.

## Seal (lavoro in ambiente fermo)

Prima eseguire `scripts/db/backup.sh` e `Peritial.RecoveryCheckpoint export` con il corretto ruolo PostgreSQL audit e credenziali S3 di sola lettura (vedere i relativi runbook). Conservare con il dump il file `.sha256`.

```bash
export PERITIAL_BUNDLE_CONFIRM=WRITERS_STOPPED
export PERITIAL_BUNDLE_POSTGRES_DUMP=/dev/shm/peritial-recovery-001/peritial.dump
export PERITIAL_ARCHIVE_DIR=/dev/shm/peritial-recovery-001/objects
export PERITIAL_BUNDLE_DIR=/secure/backup-sets/recovery-001.sealed
export PERITIAL_BUNDLE_AES_KEY_FILE=/secure/keys/pg-s3-bundle.key
export PERITIAL_ARCHIVE_HMAC_KEY_FILE=/secure/keys/s3-manifest.key

dotnet run --project scripts/recovery/Peritial.RecoveryBundle -- seal
```

Il percorso `PERITIAL_BUNDLE_DIR` **non deve esistere**: non vengono mai sovrascritti backup preesistenti. L'operazione salva file 0600 su Unix e directory 0700. Spostare il set cifrato su un supporto offsite immutabile secondo il piano di backup; **questa funzionalità non esegue upload, retention né gestione offsite**.

**Attenzione ai file temporanei:** il dump PGDMP e l'archive S3 **restano in chiaro** finché non si rimuove lo scratch `tmpfs` (anche dopo `seal`). Non conservarli oltre la finestra e non copiarli su storage persistente. Lo stesso vale per l'output di `unseal` dopo il restore. Evitare swap, coredump e capture di memoria in manutenzione; la cancellazione di uno scratch su SSD non è garantita. Solo il bundle sigillato può essere archiviato su storage durabile senza esporre i byte in chiaro.

## Unseal e restore su destinazioni realmente vuote

Su un host Linux isolato, con lo swap disabilitato, predisporre prima la directory privata con `mkdir -m 0700 /dev/shm/peritial-restore-001` e verificare il tipo `tmpfs`; quindi, con le due chiavi originali disponibili tramite secret manager e la directory cifrata non alterata:

```bash
export PERITIAL_BUNDLE_CONFIRM=ISOLATED_RESTORE
export PERITIAL_BUNDLE_DIR=/secure/backup-sets/recovery-001.sealed
export PERITIAL_BUNDLE_UNSEAL_DIR=/dev/shm/peritial-restore-001/output
export PERITIAL_BUNDLE_AES_KEY_FILE=/secure/keys/pg-s3-bundle.key
export PERITIAL_ARCHIVE_HMAC_KEY_FILE=/secure/keys/s3-manifest.key

dotnet run --project scripts/recovery/Peritial.RecoveryBundle -- unseal
```

I file risultanti hanno percorsi canonici e privati:

- `/dev/shm/peritial-restore-001/output/postgres/peritial.dump` e `.sha256`
- `/dev/shm/peritial-restore-001/output/s3/manifest.json`, `manifest.hmac` e `objects/*.blob`

Su **nuovo** PostgreSQL 17 con ruoli preprovisionati usare `scripts/db/restore.sh` e `PERITIAL_RESTORE_FILE` diretto al dump decifrato, conferma `EMPTY_DATABASE`. Sul **nuovo** bucket S3 vuoto usare `Peritial.RecoveryCheckpoint restore-objects` con `PERITIAL_ARCHIVE_DIR` diretto a `.../s3`, la chiave HMAC separata, `PERITIAL_CHECKPOINT_CONFIRM=WRITERS_STOPPED` e `PERITIAL_RESTORE_OBJECTS_CONFIRM=EMPTY_DESTINATION_BUCKET`. Solo dopo verificare i conteggi tenant, journal delle migrazioni, FORCE RLS, audit, hash S3, Keycloak/ACL e policy e scegliere l'eventuale switch di traffico.

Non ripetere un restore su database o bucket non vuoti. Un errore dopo una PUT può lasciare **storage parzialmente popolato**: scartare il bucket e ripetere su uno nuovo. Se `unseal` fallisce, la directory finale non viene pubblicata e la stage privata viene rimossa.

## Copertura test e ciò che resta aperto

`SealedRecoveryBundleTests` in backend usa dati multi-MiB per collaudare AES-GCM multiblocco, chiavi errate/identiche, SHA-256 PGDMP, sidecar HMAC e rifiuto di indice/ciphertext alterati senza pubblicare plaintext. La pipeline Compose usa invece **PGDMP reale e blob SeaweedFS reali**, verificando che le directory plaintext siano `tmpfs` privato con swap spento (suite `SecureRecoveryStagingTests`), generando chiavi effimere, eseguendo `seal`/`unseal`, verificando rifiuti di chiave assente o group-readable e importando il dump e gli oggetti **dal solo output del bundle decifrato** su due destinazioni separate. Il test è isolato e usa credenziali dimostrative.

Non sono coperte: unità di transazione PostgreSQL↔S3 durante upload live, S3 versioning e oggetti orfani/multipart, Azure Blob, Keycloak e secret backup, scheduling automatico, retention e cifratura end-to-end senza plaintext stage, verifica di ripristino a un punto RPO/RTO definito, audit KMS e rotazione chiavi, disaster recovery in produzione. **#6, #18 e #21 rimangono aperte.**

## Trasferimento crittografico verso deposito S3 indipendente (ADR 0024)

Dopo `seal`, il bundle contiene **solo ciphertext AES-256-GCM**, un index HMAC e metadati tecnici. Per replicare questo bundle cifrato in un bucket S3 dedicato su altro server/account, usare `Peritial.RecoveryBundle push-sealed`, seguito eventualmente da `pull-sealed` su un host di recupero. Nessun PGDMP/documento in chiaro viene inviato tramite questo trasporto.

Il comando `push-sealed` ricalcola/verifica **tutti i tag AES-GCM senza pubblicare plaintext**, controlla il MAC dell'index e poi invia tutti i file cifrati tramite PUT con `If-None-Match: *` sotto `sealed-v1/<backup-id>/`. Per ogni file verifica SHA-256 e dimensione rileggendo il byte stream dal server remoto. Invia `transfer.json` (nomi canonici, SHA-256 e lunghezze) autenticato da `transfer.hmac` con sottochiave derivata dalla chiave AES, e **soltanto alla fine** pubblica `complete`. Un prefisso in cui `complete` non esiste, o il cui inventario/oggetto non verifica MAC e hash, **non è un backup scaricabile**.

`pull-sealed` verifica il marker, il MAC, la lista esatta degli oggetti remoti, tutti gli hash SHA-256 e i tag AES-GCM. Scrive inizialmente in una nuova directory privata 0700 con file 0600, e rinomina nella destinazione soltanto quando tutti i controlli sono superati. Un remote download non produce **mai** dump o documenti plaintext; quelli diventano disponibili soltanto tramite il successivo `unseal` in `tmpfs` protetto. Il token di backup ID è un UUID senza trattini, con **32 caratteri esadecimali minuscoli**, ed è univoco per ogni tentativo.

### Esempio di replica e download

Richiede endpoint `https://`, credenziali S3 ottenute da secret manager, bucket di backup dedicato preprovisionato, ACL minime, identità diversa da quella applicativa e accesso alle due chiavi indipendenti fuori dal bundle (file `0600`). I seguenti valori sono soltanto esempi e **non** credenziali utilizzabili:

```bash
export PERITIAL_BUNDLE_AES_KEY_FILE=/secure/keys/pg-s3-bundle.key
export PERITIAL_ARCHIVE_HMAC_KEY_FILE=/secure/keys/s3-manifest.key
export PERITIAL_OFFSITE_S3_ENDPOINT=https://remote-backup.example.invalid
export PERITIAL_OFFSITE_S3_BUCKET=peritial-encrypted-recovery
export PERITIAL_OFFSITE_S3_ACCESS_KEY='(dal secret manager)'
export PERITIAL_OFFSITE_S3_SECRET_KEY='(segreto dell uploader)'
# Secondo principal S3: reader per verifica SHA-256 dopo ogni PUT.
export PERITIAL_OFFSITE_VERIFY_S3_ACCESS_KEY='(access key del reader)'
export PERITIAL_OFFSITE_VERIFY_S3_SECRET_KEY='(segreto del reader)'
export PERITIAL_OFFSITE_BACKUP_ID=0123456789abcdef0123456789abcdef
export PERITIAL_BUNDLE_DIR=/secure/backup-sets/recovery-001.sealed

export PERITIAL_BUNDLE_CONFIRM=REMOTE_ENCRYPTED_UPLOAD
dotnet run --project scripts/recovery/Peritial.RecoveryBundle -- push-sealed
```

Per il download **su un host separato**, mantenere endpoint, bucket e backup ID, ma assegnare `PERITIAL_OFFSITE_S3_ACCESS_KEY`/`PERITIAL_OFFSITE_S3_SECRET_KEY` al **reader** (non all uploader). Scegliere una directory nuova e richiedere il consenso specifico:

```bash
export PERITIAL_BUNDLE_CONFIRM=ISOLATED_ENCRYPTED_DOWNLOAD
export PERITIAL_BUNDLE_DOWNLOAD_DIR=/secure/restore-staging/recovery-001.encrypted
dotnet run --project scripts/recovery/Peritial.RecoveryBundle -- pull-sealed
```

Proseguire con `unseal` usando `PERITIAL_BUNDLE_DIR=/secure/restore-staging/recovery-001.encrypted`, e `PERITIAL_BUNDLE_UNSEAL_DIR` sotto il `tmpfs` privato swap-free dell'host di recovery; poi eseguire i restore PostgreSQL/S3 nelle destinazioni vuote. Il download del solo bundle cifrato **può** stare su disco persistente; l'output decifrato **no**.

La sola eccezione a HTTPS è il server usa-e-getta **`seaweedfs-backup`** nella CI Compose con conferma `PERITIAL_OFFSITE_CI_HTTP=DISPOSABLE_TEST_ONLY`. Non c'è un bypass HTTP per altri server. Per CI vengono usate tre istanze SeaweedFS: S3 applicativo, destinazione backup cifrato, e S3 del restore; la CI cancella la prima copia locale cifrata prima di eseguire `unseal` sul bundle scaricato.

**Confini della sola tranche ADR 0024:** il trasferimento cifrato impediva overwrite e backup incompleti, ma non imponeva ancora Object Lock o retention. Il controllo fail-closed è stato aggiunto in ADR 0025, con prova positiva nel job WORM di ADR 0026. Non garantisce distribuzione geografica, IAM o KMS. Un amministratore remoto con DELETE/PUT può ancora cancellare/corrompere il backup; la verifica ne rileva la corruzione ma non ne garantisce la disponibilità. Un tentativo fallito prima di `complete` lascia oggetti remoti parziali: non riprovare sullo stesso backup ID, non cancellare automaticamente in modo pericoloso; creare un nuovo ID ed effettuare riconciliazione supervisionata del prefisso incompleto. Per grandi backup i singoli PUT S3 possono incontrare limiti provider; non sono ancora disponibili multipart upload, resume o una policy di capacity e retention. Questo è **un drill di replica remota**, non un backup offsite produttivo certificato.

## Gate per depositi S3 reali: Object Lock COMPLIANCE (ADR 0025)

**Non basta che l'archive sia cifrato o che esista `complete`:** in modalità produttiva `push-sealed` impone, **prima di qualsiasi PUT**, che il servizio risponda correttamente alle API `GetBucketVersioning` e `GetObjectLockConfiguration`. Richiede versioning esattamente `Enabled`, Object Lock `Enabled`, retention predefinita **`COMPLIANCE` di almeno 30 giorni** (non `GOVERNANCE` e non retention mancante). I permessi insufficienti, un server non compatibile o una configurazione inadeguata provocano un rifiuto senza scrivere nuovi oggetti.

Ogni file ciphertext, `transfer.json`, `transfer.hmac` e il marker `complete` vengono caricati con PUT condizionale; **prima di proseguire**, la procedura richiede il `VersionId` reale restituito dal server e chiama `GetObjectRetention` per **quella versione specifica**, controllando modalità `COMPLIANCE` e data di scadenza coerente con la finestra minima di 30 giorni (piccola tolleranza per gli orologi). Se un oggetto non supera la verifica non viene pubblicato il successivo; il prefisso può rimanere incompleto e deve essere riconciliato manualmente. Se proprio `complete` non supera il controllo, **non** assumere che il prefisso sia utilizzabile: la via produttiva `pull-sealed` ricontrolla il bucket e la retention **delle versioni correnti di ogni oggetto** prima di pubblicare la copia cifrata locale.

L'eccezione locale `PERITIAL_OFFSITE_CI_HTTP=DISPOSABLE_TEST_ONLY` è accettata dal CLI **solo** con l'endpoint `http://seaweedfs-backup` del laboratorio Compose; quella modalità non verifica le garanzie WORM e non deve essere interpretata come Object Lock. Le API dirette espongono un flag `disposableUnprotectedDemo` da usare esclusivamente nei test: il valore predefinito è fail-closed. Il test CI verifica che **il percorso predefinito non scriva nulla** sul SeaweedFS demo, mentre i test unitari verificano il rifiuto di versioning sospeso, `GOVERNANCE`, retention inferiore a 30 giorni, version-ID mancante e data di scadenza inadeguata. La CI Compose usa un SeaweedFS demo **senza** Object Lock e verifica il fail-closed. Un **sesto job indipendente** `worm-recovery` ora testa positivamente il protocollo su **PGSTY SILO** con Object Lock COMPLIANCE autentico; rimangono non testati AWS S3 gestito, TLS con CA del provider, policy IAM e backup geograficamente offsite.

**Operatività di produzione:** creare il bucket con Object Lock e versioning amministrati dal provider, configurare COMPLIANCE e minimo 30 giorni prima di concedere al processo i permessi di upload e lettura dei metadati/retention delle versioni. Usare credenziali dedicate senza `DeleteObject`, `PutBucketObjectLockConfiguration` né privilegi di bypass, e controllare la policy IAM separatamente (non verificata dal software). Le versioni S3 protette possono comunque essere *nascoste* da delete marker o sostituzioni correnti: il programma rifiuta queste situazioni, ma **non** recupera automaticamente un `VersionId` storico protetto; la futura prova di recupero da una versione non corrente rimane aperta. La retention non assicura geo-replica, disponibilità, corretta conservazione di entrambe le chiavi, snapshot DB↔S3, rotazione KMS, backup Keycloak/Azure o rispetto dei requisiti legali RPO/RTO. Per backup reali non usare la flag CI.

## Prova WORM positiva con versioni realmente protette (ADR 0026)

La CI aggiunge un job separato `worm-recovery` su runner Linux effimero, swap disabilitato e server S3 compatibile **PGSTY SILO**, distribuito con immagine fissata per digest pubblicato. Il fork SILO è **solo uno storage di riferimento per il test**; non rimpiazza SeaweedFS nell'applicazione e non prova AWS S3 gestito. La procedura di test `ObjectLockWormIntegrationTests` usa un account locale dimostrativo e crea un bucket unico con Object Lock **abilitato alla creazione**, versioning `Enabled` e retention predefinita `COMPLIANCE` di **32 giorni**.

La prova valida la configurazione mediante le API S3, crea un vero set cifrato PGDMP + manifest S3 autenticato, esegue `SealedBundleOffsite.UploadAsync` **senza bypass** e richiede un `VersionId` immutabile e `GetObjectRetention` su ogni versione caricata, compreso `complete`. Poi esercita la cancellazione con `DeleteObject(VersionId=...)` sul marcatore protetto: **il provider la rifiuta**. Come controllo positivo, una versione in un altro bucket con solo versioning, **non** protetta da Object Lock, viene invece eliminata con lo stesso tipo di API. Il test scarica il backup cifrato con la via produttiva e lo decifra esclusivamente in `tmpfs` privata, verificando il dump.

Infine un `DeleteObject` *senza VersionId* crea un **delete marker** che nasconde `complete` alla lettura per nome corrente; la versione protetta resta consultabile con `GetObject(VersionId=...)`, ma il comando standard `pull-sealed` **rifiuta correttamente** il prefisso non più completo secondo lo stato corrente. **Il ripristino operativo di un intero set da VersionId storici non è ancora implementato**: serve un comando offline esplicito e autenticato che selezioni e verifichi tutte le versioni coerenti, non un fallback arbitrario al backup precedente. Anche le prove contro AWS S3 effettivo, Object Lock su bucket amministrati con IAM minimo, georeplica, retention legale, KMS e rotation restano aperte.

La vecchia immagine upstream `quay.io/minio/minio` ha restituito 401 al pull; la suite usa quindi il fork mantenuto **SILO 2026-11-12**, **pin per digest**, unicamente in CI. Non considerare il test un certificato di compliance, continuità operativa o produzione: un container usa-e-getta non è un deposito offsite. Un `COMPLIANCE` di 32 giorni nel laboratorio vale **solo per i dati del container temporaneo**, che vengono distrutti insieme al server alla fine del job, non per backup veri.

## Recupero storico esplicito del set cifrato da versioni S3 nascoste (ADR 0027)

**Quando usarlo:** un `DELETE` di una key su bucket versionato ha creato un *delete marker*, o la versione corrente è stata sostituita. Il normale `pull-sealed` deve **rifiutare** lo stato corrente incompleto/alterato; non elimina automaticamente il marker e non deve cercare versioni precedenti alla cieca. **Solo un operatore in ambiente isolato** può scegliere un set preciso di versioni S3 COMPLIANCE ancora protette e richiederne il recupero.

Il nuovo `Peritial.RecoveryBundle -- pull-historic` richiede un file JSON esterno e locale `PERITIAL_OFFSITE_VERSION_SELECTION_FILE`. Contiene il `VersionId` **di ciascuna key**, relativo al prefisso immutabile `sealed-v1/<backup-id>/`: `complete`, `transfer.json`, `transfer.hmac`, `index.json`, `index.hmac` e tutti i `payload/00000000.enc` ecc. Il formato è versione 1, senza chiavi duplicate né campi imprevisti. Una selezione mancante, extra, con path non canonico o VersionId non valido fallisce chiusa. **Non** è una firma: l'autenticità deriva dall'HMAC legato al backup ID, dai SHA-256 dell'inventario, dai tag AES-256-GCM e dalla retention verificata per ogni `VersionId`.

Il software legge con `GetObject(VersionId=...)` solo gli oggetti selezionati, rifiuta ogni incongruenza fra nomi e inventario autenticato, richiede Object Lock COMPLIANCE configurato e retention `COMPLIANCE` **ancora non scaduta** per ogni versione, e copia soltanto ciphertext in una nuova directory privata con rename dopo la verifica completa. Non esegue alcuna PUT, DELETE, rimozione di marker o mutazione del deposito. L'output diventa plaintext **solo** dopo il normale `unseal` in `tmpfs` swap-free.

### Preparazione operatore

1. In una finestra di ripristino isolata, identificare il backup ID esatto e inventariare le versioni con un client S3 amministrativo in **sola lettura**, per esempio `aws s3api list-object-versions --bucket <bucket> --prefix sealed-v1/<backup-id>/`. Annotare `Key`, `VersionId`, `IsLatest` e i delete marker. Esaminare il punto di ripristino approvato: **non** selezionare automaticamente la versione più recente di ogni key o combinare snapshot diversi.
2. Creare un file privato con tutte le key relative. Esempio minimo per un bundle senza documenti (quattro file di payload tecnici; in caso di documenti aggiungere tutti i `payload/*.enc`):
```json
{
  "Version": 1,
  "Versions": {
    "complete": "version-id-complete",
    "transfer.json": "version-id-transfer-json",
    "transfer.hmac": "version-id-transfer-hmac",
    "index.json": "version-id-index-json",
    "index.hmac": "version-id-index-hmac",
    "payload/00000000.enc": "version-id-payload-0",
    "payload/00000001.enc": "version-id-payload-1",
    "payload/00000002.enc": "version-id-payload-2",
    "payload/00000003.enc": "version-id-payload-3"
  }
}
```
3. Proteggere il file fuori dal repository (`umask 077`; idealmente `chmod 600`) e usare lo stesso backup ID e la chiave AES del bundle originale. Non stampare il file né le credenziali nei log:
```bash
export PERITIAL_BUNDLE_CONFIRM=ISOLATED_HISTORICAL_DOWNLOAD
export PERITIAL_OFFSITE_VERSION_SELECTION_FILE=/secure/recovery/selected-versions.json
export PERITIAL_OFFSITE_BACKUP_ID=0123456789abcdef0123456789abcdef
export PERITIAL_BUNDLE_DOWNLOAD_DIR=/secure/recovery/restored-ciphertext
# Richiede inoltre PERITIAL_OFFSITE_S3_ENDPOINT HTTPS, bucket, credenziali,
# PERITIAL_BUNDLE_AES_KEY_FILE e PERITIAL_ARCHIVE_HMAC_KEY_FILE.
# PERITIAL_BUNDLE_DIR rimane richiesto dal CLI per compatibilità con gli altri comandi.
dotnet run --project scripts/recovery/Peritial.RecoveryBundle -- pull-historic
```
4. Eseguire `unseal` verso `tmpfs` privata con swap disabilitato e gli ordinari restore su **nuovo** PostgreSQL e **nuovo** bucket S3 vuoti. Verificare identità tenant, hash, RLS e audit prima di qualsiasi switch di traffico.

Il percorso storico è **sempre sottoposto a Object Lock**: il bypass `DISPOSABLE_TEST_ONLY` di SeaweedFS non può attivare `pull-historic`. Il job WORM del laboratorio esercita il recupero dopo delete marker su `complete` **e** su un file ciphertext, con selezione completa, chiavi errate, file mancanti, versione scorretta, JSON duplicato e divieto di overwrite, senza pubblicare partial. Lo storage WORM è una singola istanza usa-e-getta: restano da testare AWS S3 gestito, IAM con permessi di recupero minimi, KMS e restore *multi-tenant* interamente WORM su un provider offsite, oltre al disaster recovery in produzione.

## Identità separate per caricamento e recupero S3 WORM (ADR 0028)

**Nessuna credenziale root/admin S3 deve essere usata durante il normale `push-sealed`, `pull-sealed` o `pull-historic`.** Nel workflow di recupero remoto ci sono ora due profili IAM **distinti**, verificati con due utenti reali del server S3 compatibile nel job WORM:

- **Uploader**: legge soltanto configurazione di bucket, stato del prefisso `sealed-v1/` e retention S3; può fare `PutObject` nel prefisso, **non ha alcun permesso `GetObject` o `GetObjectVersion`**. Tutti i GET di verifica SHA-256 sono effettuati da un secondo client reader; nessuna cancellazione o amministrazione del bucket. La policy non concede `ListBucketVersions`, ma SILO può consentire ugualmente l'enumerazione sotto `ListBucket` (vedere sotto).
- **Reader/recovery**: può leggere la stessa configurazione di sicurezza, elencare gli oggetti correnti **e le versioni**, fare `GetObject`/`GetObjectVersion` e `GetObjectRetention` nel prefisso. **Non può caricare, sovrascrivere, cancellare o cambiare retention**. Il file di selezione storica rimane fornito separatamente dall'operatore; il reader non acquisisce mai autorizzazioni di modifica.

Per generare JSON IAM riproducibili per il bucket **esatto** di destinazione:

```bash
umask 077
python3 scripts/recovery/render_worm_iam.py \
  --bucket peritial-recovery-locked \
  --output /secure/policies/peritial-recovery-locked
# output: writer.json e reader.json (file 0600, directory 0700)
```

Il tool rifiuta nomi bucket non validi, ARN wildcard, output preesistente e sovrascrittura. I due JSON non contengono credenziali, ma restringono `Resource` a `arn:aws:s3:::<bucket>` per letture di configurazione e listing e a `arn:aws:s3:::<bucket>/sealed-v1/*` per gli oggetti. `s3:prefix` limita gli elenchi a `sealed-v1/*`. Non sono concessi `DeleteObject`, `DeleteObjectVersion`, `PutObjectRetention`, `PutBucketObjectLockConfiguration`, `PutBucketVersioning` o permessi `s3:*`. Conservare/gestire questi JSON come configurazione di infrastruttura e applicarli attraverso le procedure IAM del provider, **non** tramite il processo di backup.

**Amministrazione e prerequisiti fuori banda:** un amministratore indipendente deve creare il bucket con Object Lock attivato, versioning `Enabled`, retention `COMPLIANCE` minima di 30 giorni, poi creare/associare le due identità IAM separate. Non assegnare agli utenti di servizio altri ruoli che riaprono operazioni vietate. Per AWS S3 preferire profili a breve durata tramite IAM Role/STS e limitare le trust policy; **il CLI Peritial attuale usa ancora credenziali S3 esplicite via variabili d'ambiente**, perciò l'integrazione con assume-role/rotazione automatica è lavoro aperto, non un risultato della tranche.

La CI `worm-recovery` usa il client amministrativo `mcli` incluso nel container SILO **solo per provisioning dimostrativo** di due utenti e policy, poi esegue l'upload con l'identità uploader e il download corrente/storico con l'identità reader, senza usare credenziali root per queste operazioni. Il test prova fallimento delle operazioni non autorizzate (DELETE su una versione **non protetta** in un altro bucket per distinguere IAM da Object Lock, GET corrente/storico del writer, PUT del reader, configurazione bucket del writer e accesso al di fuori di `sealed-v1/`). Le credenziali del job sono usa-e-getta e non devono mai essere replicate in produzione.

**Rischi residui:** le policy sono modelli di permessi e la CI verifica **SILO**, non AWS IAM `SimulatePrincipalPolicy` o un account AWS gestito con trust policy, MFA, CloudTrail e scadenza STS. Il bucket può essere ancora compromesso da un amministratore con più permessi, un provider indisponibile oppure chiavi AES/HMAC perse. Il meccanismo non configura KMS, rotazione, archiviazione geografica o retention/scheduling di produzione; le issue #6, #18 e #21 restano aperte. Per le azioni IAM esatte vedere la [documentazione Amazon S3](https://docs.aws.amazon.com/AmazonS3/latest/userguide/using-with-s3-policy-actions.html).

**Correzione di sicurezza provata in CI:** PGSTY SILO mappa la lettura `GetObject(VersionId)` all'azione `GetObject` generica, e l'enumerazione versioni a `ListBucket`. Per evitare che la credenziale uploader legga byte storici, **il suo profilo non concede più alcun GET**. `SealedBundleOffsite.UploadAsync` in produzione esige una seconda istanza client `verificationSource` distinta, con il principal reader, per rileggere e verificare i ciphertext dopo ogni PUT. La CLI reale richiede le variabili `PERITIAL_OFFSITE_VERIFY_S3_ACCESS_KEY` e `PERITIAL_OFFSITE_VERIFY_S3_SECRET_KEY` del reader; la via demo SeaweedFS con bypass esplicito conserva il singolo client senza garanzie WORM/IAM. La CI SILO **non prova** che i VersionId siano invisibili all uploader: per la confidentialità dei metadati serve revisione del provider/AWS IAM. Inoltre, due client S3 distinti non dimostrano da soli due identità IAM differenti: sono indispensabili corretta associazione dei principal, policy del provider e audit credenziali.
