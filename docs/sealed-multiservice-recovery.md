# Recovery bundle cifrato PostgreSQL + SeaweedFS S3 — laboratorio

**Ambito:** tranche #6 e ADR 0022. Questo procedimento è un **drill offline** su componenti locali: non fornisce backup schedulati, storage offsite, procedura di failover produttivo, snapshot DB+S3 sotto traffico né backup Keycloak/Azure Blob. Si lavora esclusivamente su `main`; `death` è storia.

## Formato e proprietà verificate

Il comando `seal` richiede un dump PostgreSQL **custom PGDMP** con file adiacente `.sha256`, e un archive S3 creato da `Peritial.RecoveryCheckpoint export` con `manifest.json`, `manifest.hmac` e `objects/*.blob`. Prima della cifratura controlla digest PostgreSQL, MAC del manifest S3 e SHA-256 di ogni blob rispetto al manifest autenticato.

L'output è una **nuova directory privata** con `index.json`, `index.hmac` e file `payload/00000000.enc` ecc. `index.json` rivela *solo* i percorsi tecnici e la dimensione dei file, non identificativi tenant o storage key. L'index è autenticato con HMAC-SHA256 da una sottochiave derivata dalla chiave AES esterna e da salt casuale (32 byte); non è cifrato. I byte dei file sono cifrati con **AES-256-GCM**, per blocchi da 1 MiB e tag da 16 byte. La chiave di ciascun file è derivata da HMAC-SHA256 con salt di bundle, contesto di protocollo e indice file; il nonce è il numero progressivo del blocco (unico per chiave derivata). L'AAD autentica il digest dell'index, l'indice file e il blocco. La verifica rifiuta file mancanti o extra, troncamenti, MAC non valido, lunghezze inattese, path non canonici e ciphertext modificato.

`unseal` **non pubblica** una destinazione anche parzialmente decifrata quando fallisce la verifica. Decifra in una nuova directory temporanea privata e la rinomina nella destinazione richiesta soltanto dopo controllo AES-GCM di tutti i blocchi, SHA-256 del PostgreSQL e HMAC del manifest S3. La directory decifrata **contiene dati personali e documenti in chiaro**. Non usare destinazioni condivise o permanenti.

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
export PERITIAL_BUNDLE_POSTGRES_DUMP=/secure/staging/peritial.dump
export PERITIAL_ARCHIVE_DIR=/secure/staging/peritial-objects
export PERITIAL_BUNDLE_DIR=/secure/backup-sets/recovery-001.sealed
export PERITIAL_BUNDLE_AES_KEY_FILE=/secure/keys/pg-s3-bundle.key
export PERITIAL_ARCHIVE_HMAC_KEY_FILE=/secure/keys/s3-manifest.key

dotnet run --project scripts/recovery/Peritial.RecoveryBundle -- seal
```

Il percorso `PERITIAL_BUNDLE_DIR` **non deve esistere**: non vengono mai sovrascritti backup preesistenti. L'operazione salva file 0600 su Unix e directory 0700. Spostare il set cifrato su un supporto offsite immutabile secondo il piano di backup; **questa funzionalità non esegue upload, retention né gestione offsite**.

**Attenzione ai file temporanei:** il dump PGDMP e l'archive S3 sorgenti **restano in chiaro** dopo `seal`. Custodirli su un volume scratch cifrato con accesso esclusivo, applicare la policy di eliminazione; la cancellazione ordinaria può non cancellare tutti i blocchi fisici di un SSD. Il bundle cifrato non rende sicuri i precedenti file in chiaro. Durante il seal e l'unseal esistono file temporanei nei rispettivi volumi, con permessi privati.

## Unseal e restore su destinazioni realmente vuote

Su un host isolato, con le due chiavi originali disponibili tramite secret manager e la directory cifrata non alterata:

```bash
export PERITIAL_BUNDLE_CONFIRM=ISOLATED_RESTORE
export PERITIAL_BUNDLE_DIR=/secure/backup-sets/recovery-001.sealed
export PERITIAL_BUNDLE_UNSEAL_DIR=/secure/restore-staging/recovery-001
export PERITIAL_BUNDLE_AES_KEY_FILE=/secure/keys/pg-s3-bundle.key
export PERITIAL_ARCHIVE_HMAC_KEY_FILE=/secure/keys/s3-manifest.key

dotnet run --project scripts/recovery/Peritial.RecoveryBundle -- unseal
```

I file risultanti hanno percorsi canonici e privati:

- `/secure/restore-staging/recovery-001/postgres/peritial.dump` e `.sha256`
- `/secure/restore-staging/recovery-001/s3/manifest.json`, `manifest.hmac` e `objects/*.blob`

Su **nuovo** PostgreSQL 17 con ruoli preprovisionati usare `scripts/db/restore.sh` e `PERITIAL_RESTORE_FILE` diretto al dump decifrato, conferma `EMPTY_DATABASE`. Sul **nuovo** bucket S3 vuoto usare `Peritial.RecoveryCheckpoint restore-objects` con `PERITIAL_ARCHIVE_DIR` diretto a `.../s3`, la chiave HMAC separata, `PERITIAL_CHECKPOINT_CONFIRM=WRITERS_STOPPED` e `PERITIAL_RESTORE_OBJECTS_CONFIRM=EMPTY_DESTINATION_BUCKET`. Solo dopo verificare i conteggi tenant, journal delle migrazioni, FORCE RLS, audit, hash S3, Keycloak/ACL e policy e scegliere l'eventuale switch di traffico.

Non ripetere un restore su database o bucket non vuoti. Un errore dopo una PUT può lasciare **storage parzialmente popolato**: scartare il bucket e ripetere su uno nuovo. Se `unseal` fallisce, la directory finale non viene pubblicata e la stage privata viene rimossa.

## Copertura test e ciò che resta aperto

`SealedRecoveryBundleTests` in backend usa dati multi-MiB per collaudare AES-GCM multiblocco, chiavi errate/identiche, SHA-256 PGDMP, sidecar HMAC e rifiuto di indice/ciphertext alterati senza pubblicare plaintext. La pipeline Compose usa invece **PGDMP reale e blob SeaweedFS reali**, generando chiavi effimere, eseguendo `seal`/`unseal`, verificando rifiuti di chiave assente o group-readable e importando il dump e gli oggetti **dal solo output del bundle decifrato** su due destinazioni separate. Il test è isolato e usa credenziali dimostrative.

Non sono coperte: unità di transazione PostgreSQL↔S3 durante upload live, S3 versioning e oggetti orfani/multipart, Azure Blob, Keycloak e secret backup, scheduling automatico, retention e cifratura end-to-end senza plaintext stage, verifica di ripristino a un punto RPO/RTO definito, audit KMS e rotazione chiavi, disaster recovery in produzione. **#6, #18 e #21 rimangono aperte.**
