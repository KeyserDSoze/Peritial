# PostgreSQL backup e restore — test reale e confini (#6, #18)

Questa procedura riguarda **solo il database PostgreSQL**. L'archive S3 del runbook separato `docs/object-recovery-checkpoint.md` usa ora un HMAC simmetrico con chiave esterna per autenticare il manifest, **non** i dump PostgreSQL: l'autenticazione cross-servizio/offsite resta da progettare. Il file `pg_dump -Fc` con `.sha256` è anche il prerequisito verificato dal comando amministrativo `adopt` per un'installazione legacy *identica* a V001; si veda `docs/database-migrations.md`. Una prova di backup non autorizza da sola la modifica dello schema. I documenti sono salvati **fuori dal DB**, in SeaweedFS S3 o Azure Blob. La sola copia PostgreSQL **non costituisce un backup completo di Peritial**, né prova un punto di ripristino coerente tra database e blob. Per produzione servono un protocollo coordinato con sospensione scritture, snapshot/versionamento bucket, gestione dei segreti/Keycloak, cifratura, retention, monitoraggio e drill del ripristino integrale. Nessun backup è attualmente pianificato in automatico.

## Comandi disponibili

- `scripts/db/backup.sh`: esporta un'istantanea consistente con `pg_dump --format=custom`, comprensiva di schema, righe, audit, policy RLS, grants e journal `schema_migrations`. Produce `<absolute-path>.dump` e `<absolute-path>.dump.sha256`. Crea entrambi con `umask 077`, verifica il formato prima di pubblicare il risultato, evita sovrascritture e cancella i file parziali al fallimento.
- `scripts/db/restore.sh`: verifica SHA-256 e formato, richiede consenso esplicito e rifiuta database di sistema, target con oggetti preesistenti, ruoli mancanti/privilegiati o archivio modificato. Ristabilisce schema e dati con `pg_restore --single-transaction --exit-on-error --no-owner`, **preservando ACL e policy RLS**. Non usa `--clean`, non crea database, non sovrascrive quelli esistenti. Lo script deve girare solo contro un DB *nuovo, vuoto e non raggiungibile dai servizi*.
- Il checksum SHA-256 protegge da alterazioni accidentali, **non autentica** la provenienza contro un attaccante che può modificare archivio e checksum. Aggiungere firma crittografica e backup cifrati prima dell'uso produttivo.

Richiedono i client PostgreSQL **17** compatibili e un account privilegiato per dump/ripristino con capacità di leggere tutte le tabelle o creare tabelle, indici e policy. La connessione si configura con variabili standard `PGHOST`, `PGPORT`, `PGUSER`, `PGDATABASE`, `PGSSLMODE`, e un password file protetto/`PGPASSFILE` o una credenziale fornita dal secret manager. Non inserire password nei parametri di processo, file Git o output di log.

## Esempio operatore — PostgreSQL soltanto

1. Fermare il traffico mutativo REST/MCP e il worker di recovery, identificare le versioni applicative e migrazioni, e preparare una strategia **separata** per gli oggetti S3/Blob e Keycloak. Eseguire il backup con un account che vede anche audit/righe di *tutti* i tenant. Non usare `peritial_app`, soggetto a FORCE RLS.
2. Impostare le variabili standard PG e un nome file assoluto, non già presente:

   ```bash
   export PERITIAL_BACKUP_FILE=/secure/backups/peritial-YYYYMMDD-HHMMSS.dump
   sh scripts/db/backup.sh
   ```

   Trasferire dump e `.sha256` insieme in uno storage protetto, con cifratura, versionamento e accesso ristretto. Non presumere che un file ancora in `/tmp` costituisca backup durevole.

3. Su **un'altra istanza PostgreSQL 17** precreare un database vuoto, tramite template0 o equivalente, con nome diverso da `postgres` e dalle istanze operative. Predisporre separatamente `peritial_app` e `peritial_recovery` senza SUPERUSER/BYPASSRLS, mantenendo in secret manager i login veri. `pg_dump` non esporta ruoli globali, credenziali, storage o Keycloak.
4. Configurare l'accesso al nuovo DB e il file immutato con checksum, poi:

   ```bash
   export PERITIAL_RESTORE_FILE=/secure/backups/peritial-YYYYMMDD-HHMMSS.dump
   export PERITIAL_RESTORE_CONFIRM=EMPTY_DATABASE
   sh scripts/db/restore.sh
   ```

5. Con il ruolo di amministrazione verificare almeno `schema_migrations`, i conteggi per tenant di `perizie` e `documents`, i record `security_audit` e `upload_request_keys`, le policy `ENABLE/FORCE RLS` e i privilegi minimi. **Non riaprire l'API** fino a quando il contenuto referenziato è stato ripristinato e verificato nel bucket e il worker di recovery può riprendere in sicurezza. Chiudere il piano di rollback nel change record.

## Prova GitHub Actions

`bash tests/backup/run_postgres_restore.sh` avvia **due processi PostgreSQL 17 isolati in due container**, applica il migrator V001/V002 sul sorgente, inserisce fixture reali per 2 tenant (perizie, metadata documento, audit, idempotenza completed, upload pending), esegue i due script con copia del dump nel server di destinazione, e verifica:
- rifiuto senza conferma esplicita;
- checksum modificato e restore non eseguito;
- restore riuscito su server nuovo con righe e journal;
- cinque tabelle con FORCE RLS, grants minimi e assenza dati visibili all'app senza tenant context;
- rifiuto di rieseguire restore su database non vuoto.

Il job indipendente `database-recovery` di `.github/workflows/ci.yml` rende questa prova obbligatoria ad ogni push su `main`. In aggiunta, il job Compose esegue un drill **offline** di ripristino PostgreSQL e dei **byte effettivi SeaweedFS S3** verso server e bucket distinti (vedere `tests/backup/run_complete_pg_s3_restore.sh` e `docs/object-recovery-checkpoint.md`). Il test del job `database-recovery` riguarda solo PostgreSQL; quello Compose include anche i byte SeaweedFS S3 referenziati. Non sono coperti Azure Blob, orfani/versioni del bucket, snapshot durante upload reali, replica/failover, Keycloak, backup offsite cifrato/firma, credenziali reali né ripristino on-prem/Azure produttivo. **Non dichiarare soddisfatto il requisito di disaster recovery completo** in #6/#18 prima di tali prove.

## Checkpoint integrità S3 (nuova tranche #6, ADR 0019)

A complemento del backup PostgreSQL, è disponibile `scripts/recovery/Peritial.RecoveryCheckpoint`: genera un manifest protetto di SHA-256 calcolati dai **byte reali S3** e verifica successivamente che i riferimenti del DB e i blob ripristinati corrispondano esattamente. Richiede ambiente offline, ruolo DB di audit che non sia filtrato da RLS, nessun upload/idempotenza pending e accesso S3 read-only. Non esporta i blob: per la copia e l'eventuale riavvio dei servizi attenersi al piano di recupero esterno. Guida e requisiti: `docs/object-recovery-checkpoint.md`.

## Proteggere entrambi i backup in un unico bundle cifrato (ADR 0022)

`scripts/recovery/Peritial.RecoveryBundle` fornisce `seal`/`unseal` per il **dump PostgreSQL completo** (PGDMP + digest) e l'**archive S3 autenticato** con i byte dei documenti. Usa AES-256-GCM a blocchi, index MAC autenticato e due chiavi indipendenti da file owner-only, oltre alla chiave HMAC S3 esistente. La CI Compose prova il ripristino PostgreSQL/S3 **a partire dai file decifrati dal medesimo bundle**, sui due server distinti. Il dump PostgreSQL viene così autenticato insieme ai file S3; il bundle non è però una transazione atomica durante il traffico. In ogni fase di esportazione e decifratura, **i file intermedi restano in chiaro e devono risiedere su storage temporaneo cifrato/isolato**: nessun tool li cancella automaticamente in modo sicuro. Documentazione e limiti: `docs/sealed-multiservice-recovery.md`.

## Scratch volatile obbligatorio per il bundle cifrato (ADR 0023)

Nell'operazione multi-servizio `seal/unseal` non utilizzare un dump o un archive S3 in chiaro su filesystem persistente: `SecureRecoveryStaging` rifiuta sorgenti o destinazioni plaintext non ubicate su `tmpfs` Linux con **swap disabilitato**, directory private `0700` e percorsi non symlink. Il normale `scripts/db/backup.sh` continua a poter produrre un backup PostgreSQL in chiaro quando usato separatamente: **per il workflow sigillato predisporre una destinazione volatile prima del dump**. Solo il bundle AES-256-GCM può essere conservato su disco, per il resto vedere `docs/sealed-multiservice-recovery.md`. Linux-only fino a revisione formale di una soluzione scratch per altri OS; non è una prova contro la compromissione dell'host.

## Replica del bundle cifrato su S3 distinto (ADR 0024)

Il comando `Peritial.RecoveryBundle push-sealed|pull-sealed` replica **soltanto il bundle AES-GCM cifrato**, mai dump o documenti in chiaro, verso un bucket remoto S3 usando TLS obbligatorio (eccetto il server dimostrativo isolato CI). Richiede ID univoco e chiavi esterne, verifica GCM, SHA-256 e inventario HMAC, carica il marker `complete` per ultimo e rifiuta prefissi parziali o overwrite. La CI prova questa procedura con una terza istanza SeaweedFS e usa il file cifrato **scaricato da essa** per ripristinare PostgreSQL e i documenti nei due server di destinazione. Non implica Object Lock, retention automatica, multipart, georeplica né KMS: per la procedura e i limiti si veda `docs/sealed-multiservice-recovery.md`.

## Protezione Object Lock obbligatoria nella replica S3 reale (ADR 0025)

`Peritial.RecoveryBundle push-sealed` richiede per impostazione predefinita bucket S3 con versioning `Enabled` e retention `COMPLIANCE` minima di 30 giorni, verificata prima di ogni trasferimento; controlla il `VersionId` e la retention della specifica versione dopo ogni PUT. `pull-sealed` ripete i controlli sulle versioni correnti. L'istanza `seaweedfs-backup` in CI usa un'eccezione **esplicitamente dimostrativa** senza garanzie Object Lock: ciò verifica il fail-closed, **non** certifica un backup WORM di produzione. La policy IAM, Object Lock sul provider effettivo, prove di recupero di versioni storiche e KMS/offsite restano da realizzare; vedere `docs/sealed-multiservice-recovery.md`.

## Test positivo di immutabilità delle versioni su provider S3 compatibile (ADR 0026)

Oltre al test negativo SeaweedFS di ADR 0025, il nuovo job isolato `worm-recovery` costruisce un bucket con Object Lock COMPLIANCE 32 giorni su un server PGSTY SILO fissato per digest; testa **la vera API S3** end-to-end senza bypass, compresa l'impossibilità di cancellare la versione protetta, verificata contro il controllo di una versione non protetta che è cancellabile. Verifica anche che un delete marker non distrugga la versione sottostante e che il normale download rifiuti una chiave corrente nascosta. Questa è una prova di laboratorio WORM, non un servizio geografico offsite o un drill AWS/KMS produttivo. Runbook in `docs/sealed-multiservice-recovery.md`.

## Recupero di versioni protette non correnti (ADR 0027)

Se un delete marker nasconde un oggetto, il comando ordinario `pull-sealed` rifiuta giustamente il backup incompleto nello stato corrente. `pull-historic` consente invece un recupero **esplicito e non distruttivo** da tutte le versioni S3 COMPLIANCE scelte dall'operatore nel file `PERITIAL_OFFSITE_VERSION_SELECTION_FILE`: autenticazione dell'inventario, controllo della retention specifica, SHA-256 e AES-GCM, senza scritture remote né fallback implicito. Il ciphertext scaricato deve essere decifrato nel solito `tmpfs` privato swap-free, poi ripristinato nelle destinazioni vuote. Per identificazione delle versioni e JSON vedere il runbook `docs/sealed-multiservice-recovery.md`. Non è un recovery automatico/continuo o un processo offsite produttivo.

## Separazione IAM di upload e recovery (ADR 0028)

La procedura cifrata `push-sealed` deve usare un'identità S3 limitata ai soli upload e verifiche dei ciphertext nel prefisso `sealed-v1/`; `pull-sealed` e `pull-historic` devono usare una **diversa identità read-only** capace di leggere specifici `VersionId` e la loro retention COMPLIANCE. Usare `python3 scripts/recovery/render_worm_iam.py --bucket <nome-bucket> --output <dir-assoluta-nuova>` per generare due policy senza credenziali; associarle nel provider dopo il provisioning amministrativo del bucket. La CI sul server SILO verifica autorizzazioni consentite e negate, ma non convalida IAM/STS AWS reale, credenziali a breve durata o KMS: tali attività rimangono aperte, vedere `docs/sealed-multiservice-recovery.md`.
