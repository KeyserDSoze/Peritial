# Recovery bundle cifrato PostgreSQL + SeaweedFS S3 — laboratorio

**Ambito:** tranche #6, ADR 0022 e ADR 0023 (staging volatile obbligatorio). Questo procedimento è un **drill offline** su componenti locali: non fornisce backup schedulati, storage offsite, procedura di failover produttivo, snapshot DB+S3 sotto traffico né backup Keycloak/Azure Blob. Si lavora esclusivamente su `main`; `death` è storia.

## Formato e proprietà verificate

Il comando `seal` richiede un dump PostgreSQL **custom PGDMP** con file adiacente `.sha256`, e un archive S3 creato da `Peritial.RecoveryCheckpoint export` con `manifest.json`, `manifest.hmac` e `objects/*.blob`. Prima della cifratura controlla digest PostgreSQL, MAC del manifest S3 e SHA-256 di ogni blob rispetto al manifest autenticato.

L'output è una **nuova directory privata** con `index.json`, `index.hmac` e file `payload/00000000.enc` ecc. `index.json` rivela *solo* i percorsi tecnici e la dimensione dei file, non identificativi tenant o storage key. L'index è autenticato con HMAC-SHA256 da una sottochiave derivata dalla chiave AES esterna e da salt casuale (32 byte); non è cifrato. I byte dei file sono cifrati con **AES-256-GCM**, per blocchi da 1 MiB e tag da 16 byte. La chiave di ciascun file è derivata da HMAC-SHA256 con salt di bundle, contesto di protocollo e indice file; il nonce è il numero progressivo del blocco (unico per chiave derivata). L'AAD autentica il digest dell'index, l'indice file e il blocco. La verifica rifiuta file mancanti o extra, troncamenti, MAC non valido, lunghezze inattese, path non canonici e ciphertext modificato.

`unseal` **non pubblica** una destinazione anche parzialmente decifrata quando fallisce la verifica. Decifra in una nuova directory temporanea privata e la rinomina nella destinazione richiesta soltanto dopo controllo AES-GCM di tutti i blocchi, SHA-256 del PostgreSQL e HMAC del manifest S3. La directory decifrata **contiene dati personali e documenti in chiaro**. Non usare destinazioni condivise o permanenti.

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
