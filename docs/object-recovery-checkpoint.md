# Checkpoint offline DB ↔ SeaweedFS S3 — integrità, **non backup** (#6/#21)

**Stato:** verificatore read-only più export/import offline dei **byte reali SeaweedFS S3** verso un bucket vuoto su istanza separata, con prove PostgreSQL e S3 reali. Non sono coperti Azure Blob, snapshot atomici cross-sistema, restore Keycloak o produzione. Non eseguire su un ambiente con API, MCP o worker in scrittura. Manifest e credenziali contengono dati riservati; non condividerli né committarli.

## A cosa serve

Il comando `capture` costruisce un inventario ordinato dei documenti referenziati da `documents` in PostgreSQL, per **tutti i tenant**. Per ogni documento legge l'oggetto SeaweedFS S3, verifica che la dimensione reale sia uguale a `documents.length_bytes`, ne calcola SHA-256 e, **quando il campo privato `documents.content_sha256` è presente**, impone che i due digest coincidano **già durante la prima acquisizione**, non soltanto durante la verifica di un manifest esistente. Il manifest JSON versione 1 registra tenant UUID, documento UUID, chiave S3, dimensione e checksum. Se manca un blob, la dimensione differisce, una chiave globale è riutilizzata, l'utente DB può essere filtrato da FORCE RLS o esiste un upload/una chiave idempotente ancora in stato pending, l'operazione si interrompe.

`verify` confronta un manifest precedente con **tutti** i riferimenti del database destinazione, incluso il digest privato del DB per i record V013/V014, e rilegge ogni blob S3 per confrontare dimensioni e SHA-256. La verifica dei metadati rifiuta un manifest ben formato il cui SHA-256 non corrisponda al digest DB prima di scrivere oggetti nel restore. Rifiuta differenze di cardinalità, tenant/ID/chiave/metadati, bytes alterati anche a dimensione invariata e manifest versionati diversamente. Il numero di documenti validati è l'unica informazione stampata: mai nomi, tenant, chiavi o dati.

**Record legacy:** un documento precedente alla V013 può avere `content_sha256=NULL`. Il checkpoint ne conserva la possibilità di backup senza inventare un digest di ammissione: l'hash del manifest in quel caso attesta soltanto i byte **presenti al momento del capture**, non la provenienza da una scansione precedente. Il download applicativo rimane fail-closed per tali documenti. La policy di bonifica/approvazione e il backfill sono ancora aperti nella [issue #27](https://github.com/KeyserDSoze/Peritial/issues/27).

**Budget bytes per oggetto (ADR 0112):** ogni GET S3 di `capture` e `verify` legge al massimo i byte dichiarati in `documents.length_bytes` (più un unico byte per identificare append malevoli) e rifiuta subito dimensioni eccessive o file troncati. Il copiatore `export` da S3 a `tmpfs` ha lo stesso limite, così un oggetto sovrascritto dopo la prima verifica non può riempire lo staging con byte non ammessi; `restore-objects` ricontrolla i file locali con il medesimo budget prima di effettuare PUT. I digest DB, manifest e MAC rimangono vincoli distinti. Il limite è per **singolo oggetto**, distinto dal nuovo budget *complessivo* cross-tenant della [ADR 0113](../adr/0113-offline-object-recovery-cumulative-byte-budget.md). Eseguire le operazioni soltanto con i writer realmente arrestati. Vedere anche [ADR 0112](../adr/0112-offline-s3-checkpoint-archive-bounded-byte-streams.md).

**Budget complessivo del recovery (ADR 0113):** `capture`, `verify`, `export` e `restore-objects` applicano una quota totale di default **536870912 byte (512 MiB)** sui documenti di **tutti i tenant**. Il valore configurabile `PERITIAL_RECOVERY_MAX_TOTAL_BYTES` deve essere un intero positivo decimale: valori invalidi/zero/negativi sono rifiutati. Il controllo avviene prima del primo S3 GET del checkpoint, prima della creazione della directory `tmpfs` per export e, dopo HMAC, prima della lettura file/PUT S3 nel restore. Un archivio con molti file piccoli non può superare la somma ammessa. **La quota non riserva lo spazio**: scegliere un valore sensibilmente inferiore alla capienza `tmpfs`/bucket effettivamente disponibile, considerando overhead di directory, manifest, dump DB, SDK e altri servizi. Mai aumentarla automaticamente in caso di rifiuto senza un capacity plan verificato.

**Preflight spazio libero (ADR 0119):** dopo il checkpoint e prima della
seconda lettura S3, `export` confronta la dimensione per file arrotondata
a pagine con lo spazio libero del mount tmpfs reale. Riserva logica
almeno una pagina per manifest e HMAC; ricontrolla lo spazio effettivo
necessario ai due metadati prima della scrittura. Non è una prenotazione
fisica e non prova disponibilità di inode o RAM/cgroup.

**Budget cumulativo degli oggetti (ADR 0114):** i medesimi quattro comandi limitano separatamente il **numero di documenti di tutti i tenant**, con default **10.000** e tetto massimo configurabile **100.000**. `PERITIAL_RECOVERY_MAX_OBJECTS` accetta esclusivamente un intero decimale positivo compreso fra 1 e 100000. Il reader privilegiato PostgreSQL usa una query ordinata con `LIMIT maxObjects + 1` e si arresta al primo elemento in eccesso; non carica milioni di righe in RAM. Manifest autenticato e inventario del database restaurato sono verificati con lo stesso cap prima di attraversare gli oggetti S3. Un archive con molti file di 1 byte può dunque essere negato **anche se la somma dei byte è sotto i 512 MiB**. Il limite non sostituisce l'inventario completo del bucket, la quota fisica `tmpfs`, o una finestra con writer realmente fermi. [ADR 0114](../adr/0114-offline-recovery-cross-tenant-object-count-budget.md).

**Importante:** non c'è una transazione comune tra S3 e PostgreSQL. Il lock `SHARE NOWAIT` su `documents` e i registri di upload limita corse durante l'inventario DB, ma è rilasciato prima della lettura dei blob. La conferma `WRITERS_STOPPED` **non rileva da sola** processi ancora attivi: l'operatore deve fermarli realmente. Il semplice manifest di `capture` continua a **non contenere** i byte degli oggetti. Per salvare i byte, usare `export`: la cartella di archive contiene un manifest distinto e i file binari. Il semplice checkpoint `capture` NON è autenticato. L'archive `export`/ `restore-objects` autentica ora il proprio manifest con **HMAC-SHA256** usando un segreto esterno; **non** è cifrato, non ha firma asimmetrica né garantisce uno snapshot atomico con PostgreSQL.

## Prerequisiti operatore

- .NET 10 e accesso al repository `main` alla stessa release del DB; PostgreSQL 17+ e bucket S3 SeaweedFS compatibile. Per Azure Blob serve un implementazione distinta.
- Finestra di manutenzione: **fermare ogni writer** REST/MCP, worker di recovery, job schedulati e processi che scrivono al bucket; verificare che non ci siano operazioni in volo. Conservare separatamente il backup PostgreSQL `pg_dump -Fc` e **una copia durabile, cifrata e verificabile di tutti gli oggetti del bucket** a un punto concordato.
- Credenziale PostgreSQL con SELECT su `documents`, `upload_intents` e `upload_request_keys`, LOCK su quelle tabelle e lettura di tutti i tenant senza filtro FORCE RLS (ruolo audit dedicato `BYPASSRLS` e senza DML, creato dall'amministratore e disabilitato dopo la manutenzione; in CI si usa il superuser demo). **Non** usare il ruolo applicativo `peritial_app`. Credenziale S3 di **sola lettura** per il bucket/oggetti.
- Directory del manifest privata e già esistente; il comando crea il file temporaneo con permessi **0600 fin dall'apertura** su Unix, poi usa rename senza overwrite. Non inserirlo in Git, nei log o nel bucket pubblico. Sono presenti chiavi e identificativi riservati.

Impostare nel processo operatore (secret manager o ambiente protetto):

```bash
export PERITIAL_CHECKPOINT_CONFIRM=WRITERS_STOPPED
export PERITIAL_CHECKPOINT_POSTGRES='(connection string audit PostgreSQL)'
export PERITIAL_CHECKPOINT_S3_ENDPOINT='https://s3.example.invalid'
export PERITIAL_CHECKPOINT_S3_BUCKET='(bucket dei documenti)'
export PERITIAL_CHECKPOINT_S3_ACCESS_KEY='(secret manager)'
export PERITIAL_CHECKPOINT_S3_SECRET_KEY='(secret manager)'
export PERITIAL_CHECKPOINT_FILE=/secure/backups/peritial-documents-checkpoint.json
# Opzionale: quota cumulativa in byte, default 536870912 (512 MiB).
# Impostare SOLO dopo capacity planning dello staging e del bucket.
# export PERITIAL_RECOVERY_MAX_TOTAL_BYTES=268435456
# Opzionale: limite cross-tenant oggetti, default 10000, massimo 100000.
# export PERITIAL_RECOVERY_MAX_OBJECTS=5000
```

Durante la manutenzione, **dopo** aver fermato i writer e coordinato la copia dei blob e `pg_dump`:

```bash
dotnet run --project scripts/recovery/Peritial.RecoveryCheckpoint -- capture
```

Su un nuovo ambiente di prova, **dopo** avere ripristinato autonomamente il database e **tutti** gli oggetti nella destinazione S3, configurare le variabili per i servizi destinazione e conservare il manifest originale in posizione privata. Il file non deve essere modificabile da processi non autorizzati.

```bash
dotnet run --project scripts/recovery/Peritial.RecoveryCheckpoint -- verify
```

Solo dopo esito positivo controllare anche i record di audit, versioni schema, RLS/grants, storage ACL, ruoli Keycloak, code/documenti non referenziati e consentire ripartenza dei writer secondo il change plan. Oggetti orfani fuori da `documents` non vengono enumerati: richiedono inventario integrale del bucket e processo di riconciliazione separato. Il checksum del solo checkpoint `capture` non rileva una sostituzione malevola simultanea di oggetto e manifest. Per l'archive dei byte, HMAC verifica l'autenticità del manifest soltanto finché **la chiave separata non è compromessa**; rimangono necessarie chiavi in KMS, immutabilità, backup cifrato e protezione offsite.

## Evidenze di test e limiti

La suite `ObjectRecoveryCheckpointContractTests` usa PostgreSQL + SeaweedFS reali in Compose, con schema DB e chiavi S3 isolati, e verifica:
- manifest generato per **due tenant distinti** e validazione dei byte originali;
- oggetto mancante rifiutato;
- checksum diverso **a dimensione identica** rifiutato, anche quando la manomissione è avvenuta **prima** del primo `capture`;
- digest di manifest formalmente corretto ma diverso da quello persistito in PostgreSQL rifiutato da `VerifyMetadataAsync`;
- documento rimosso dal database rispetto al manifest rifiutato;
- upload intent e chiave di idempotenza pendenti rifiutati;
- riutilizzo della stessa chiave S3 tra tenant rifiutato;
- credenziali applicative filtrate da FORCE RLS rifiutate;
- versione manifest sconosciuta rifiutata.

La CI Compose esegue un test indipendente di export/import reale dei byte con destinazioni PostgreSQL e SeaweedFS separate, più un drill che combina `pg_dump`/`pg_restore` con la migrazione S3. Mancano: garanzia atomica di snapshot cross-sistema senza fermare tutti i writer, esportazione di oggetti non referenziati/versioni del bucket, Azure Blob, cifratura/firma asimmetrica dell'archivio, gestione e rotazione reale della chiave MAC in KMS, ripristino Keycloak, backup offsite, retention e recovery con failover. Per questi motivi #6/#18/#21 **restano aperte**.

## Archiviazione dei byte e restore su un nuovo SeaweedFS (ADR 0020)

**Operazione distinta dal checkpoint.** **Preparare prima** una directory dedicata `tmpfs` privata 0700, su Linux con swap disabilitato: l'export/import dei byte in chiaro **rifiuta** altro filesystem, directory accessibili a terzi e symlink (ADR 0023). Per i prerequisiti e i limiti vedere `docs/sealed-multiservice-recovery.md`. I nuovi comandi `export` e `restore-objects` salvano/ripristinano **veramente** i byte dei soli oggetti **attualmente referenziati** da `documents`, senza usare le chiavi S3 come nomi dei file locali. L'archive è una directory privata, con `manifest.json`, il suo autenticatore binario di **32 byte** `manifest.hmac` e `objects/00000000.blob`, `00000001.blob`, ecc. Il manifest v1 include i riferimenti DB, gli hash SHA-256 e i Content-Type S3. Ogni byte viene letto da S3 in streaming, salvato su disco e confrontato di nuovo con il checkpoint prima della pubblicazione della directory; il MAC autenticato copre esattamente i byte JSON di `manifest.json` (inclusi hash dei blob, storage key, tenant, metadati e Content-Type), con contesto di protocollo per prevenire usi incrociati della chiave. La verifica è constant-time e precede qualsiasi PUT; i file temporanei sono creati con permessi 0600 e le directory con 0700 su Unix. Non sovrascrive una directory già esistente; se un'esportazione fallisce elimina la directory parziale.

### Backup offline

1. **Fermare completamente** API REST/MCP, upload, recovery worker, job schedulati e tutte le altre scritture al database e al bucket. Il flag `WRITERS_STOPPED` è soltanto una attestazione manuale, non un lock S3. Nessun tool garantisce uno snapshot atomico DB↔bucket durante scritture.
2. Preparare e verificare il backup PostgreSQL con `scripts/db/backup.sh`, annotando istante/versione e mantenendolo con l'archivio S3 nello stesso set di recovery. Non usare credenziali API soggette a FORCE RLS: serve un'identità audit read-only con `BYPASSRLS` e S3 GET/LIST.
3. Generare una **chiave casuale di 32 byte**, salvata come file privato `0600` **fuori dalla directory dell'archive**, conservata tramite KMS/secret manager e disponibile anche durante il restore. Non usare una stringa/presenza di testo al posto di 32 byte binari. In laboratorio, ad esempio: `umask 077; openssl rand -out /secure/keys/peritial-archive-hmac.key 32`. Non pubblicare mai il file né i byte in log, Git, artifact CI o accanto all'archivio offsite. Impostare `PERITIAL_ARCHIVE_HMAC_KEY_FILE=/secure/keys/peritial-archive-hmac.key`.
4. Su Linux con swap disabilitato preparare `mkdir -m 0700 /dev/shm/peritial-recovery-001`. Impostare `PERITIAL_CHECKPOINT_*` e scegliere una directory **assoluta che non esiste** dentro la directory privata `tmpfs`:

   ```bash
   export PERITIAL_ARCHIVE_DIR=/dev/shm/peritial-recovery-001/objects
   dotnet run --project scripts/recovery/Peritial.RecoveryCheckpoint -- export
   ```

5. Sigillare **prima di qualsiasi copia persistente**, tramite `Peritial.RecoveryBundle seal` come documentato nel runbook dedicato. I file S3 temporanei contengono byte in chiaro: solo il bundle sigillato deve essere conservato fuori dallo scratch; proteggere **tutti** i file insieme: dump PostgreSQL, relativo checksum, archive completo, versioni e configurazione della release, più export Keycloak/segreti in canali separati. Non lasciare la copia primaria soltanto su disco locale. Per sicurezza produttiva sono obbligatori firma, cifratura e destinazione offsite immutabile.

### Ripristino offline

1. Creare una **nuova istanza PostgreSQL 17** e un **nuovo server/bucket S3 completamente vuoto e non esposto**. Provisionare ruoli PostgreSQL minimi; `scripts/db/restore.sh` applica il dump custom PostgreSQL sul database vuoto in un'unica transazione. Non riavviare API/worker.
2. Rendere disponibile allo strumento, tramite il secret manager, **la stessa chiave binaria di 32 byte** usata per l'export, montata in un file privato `0600` e indicata in `PERITIAL_ARCHIVE_HMAC_KEY_FILE`. La chiave non va inserita nell'archivio. In assenza di chiave o se la chiave è errata, l'import fallisce **prima di qualsiasi scrittura S3**.
3. Impostare `PERITIAL_CHECKPOINT_POSTGRES` per il database **restaurato**, `PERITIAL_CHECKPOINT_S3_ENDPOINT`, bucket e credenziali per lo storage **di destinazione**, `PERITIAL_ARCHIVE_DIR` per la copia locale invariata **decifrata su `tmpfs` privato con swap disabilitato**, `PERITIAL_CHECKPOINT_CONFIRM=WRITERS_STOPPED`. Questo comando richiede una seconda conferma esplicita:

   ```bash
   export PERITIAL_RESTORE_OBJECTS_CONFIRM=EMPTY_DESTINATION_BUCKET
   dotnet run --project scripts/recovery/Peritial.RecoveryCheckpoint -- restore-objects
   ```

4. Prima della prima PUT, il comando verifica il MAC del manifest con confronto constant-time e rifiuta file/chiavi mancanti, con permessi eccessivi, o manomessi; poi valida la struttura e tutti gli hash dei file, *tutti* gli hash e le dimensioni, la **corrispondenza esatta** dei metadati nel DB di destinazione (inclusi tenant, ID e chiavi), lo stato senza upload pendenti e l'**assenza di qualsiasi oggetto** nel bucket di destinazione. Le PUT usano `If-None-Match: *` per non sovrascrivere in caso di concorrenza. Dopo il trasferimento, legge nuovamente tutti i byte S3 e verifica SHA-256 e metadati; un secondo import viene rifiutato.
5. **Non esiste un commit distribuito tra PostgreSQL e S3:** se l'import fallisce durante una PUT, il bucket di destinazione può essere parzialmente scritto. **Scartare interamente il bucket di prova e ricominciare su un altro vuoto**, mai riavviare worker o API su un import incompleto. Accertare separatamente che non esistano oggetti S3 orfani, upload multipart incompleti o versioni di oggetti che il manifest non può rappresentare.
6. Verificare ruoli, credenziali, policy FORCE RLS, audit, Keycloak, ACL bucket, idempotenza e schema; la verifica dei soli blob referenziati non equivale al recovery completo.

Il test `ObjectRecoveryArchiveContractTests` parte su **due server PostgreSQL e due SeaweedFS distinti** in Compose: dati di due tenant, export in file reali, rifiuto di manomissione anche a lunghezza invariata, chiave S3 nel manifest contraffatta, HMAC assente/manomesso e chiave HMAC errata, import e verifica SHA-256, rifiuto di secondo import e corruzione del bucket destinazione. `tests/backup/run_complete_pg_s3_restore.sh` ferma l'API e porta avanti un `pg_dump` del DB usato da REST/MCP, un `pg_restore` su PostgreSQL nuovo e un export/import degli oggetti su SeaweedFS nuovo. Sono **drill di laboratorio**, non una dimostrazione di recupero di istanze produttive o di snapshot immutabile con traffico live.

### Confini ancora aperti

L'archive è **autenticato con HMAC simmetrico**, ma non è firmato con chiave asimmetrica, cifrato, versionato fuori sito o dotato di retention automatica. Il MAC non protegge da un attaccante che possiede la chiave: servono segregazione/rotazione/segreti KMS e prove di firma/offsite. Include **solo** i documenti che hanno metadati PostgreSQL; non enumera orfani, incomplete multipart, versioni storiche o metadati S3 non rappresentati, e non ripristina la configurazione Keycloak. L'identità DB con `BYPASSRLS` è di alto privilegio e deve essere limitata a manutenzioni supervisionate. Non usare questo flusso su Azure Blob senza un'implementazione e test separati. #6/#18/#21 rimangono aperte per questi requisiti.

## Protezione esterna del set PostgreSQL+S3 (ADR 0022)

L'archive S3 `manifest.hmac` è **un input intermedio in chiaro** al nuovo `SealedRecoveryBundle`. I comandi separati `seal` e `unseal` confezionano dump PostgreSQL, checksum e archive S3 nel medesimo set AES-256-GCM autenticato. Il bundle di destinazione non contiene il dump né i byte S3 in chiaro; l'index conserva solo nomi di file tecnici e dimensioni (metadata parzialmente osservabili). Entrambi i segreti — chiave AES e chiave HMAC S3 — devono essere protetti e consegnati fuori dal bundle. Il codice ora richiede `tmpfs` privato swap-free per gli originali e la destinazione decifrata (ADR 0023); questo **non** elimina la necessità di controllare memoria, host, swap e cleanup, né prova un DR produttivo. Vedere la [procedura dedicata](sealed-multiservice-recovery.md).
