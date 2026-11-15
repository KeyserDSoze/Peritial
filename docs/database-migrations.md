# Migrazioni PostgreSQL versionate — fase iniziale (#6)

**Stato:** `apply` gestisce i database nuovi e già versionati; il nuovo comando **esplicito** `adopt` permette la baseline **solo per database preesistenti identici alla V001 `001_init.sql`**, con un archivio PostgreSQL integro, conferma di manutenzione e confronto esaustivo del catalogo. La variante storica senza colonna/indice lease è ora supportata da un **comando distinto** `adopt-prelease`. Nessun altro drift, upgrade automatico o rollback/DR di produzione è supportato.

## Confini e identità

- Il comando amministrativo è `scripts/db/Peritial.DbMigrator`; non viene eseguito all'avvio REST/MCP. Usa `PERITIAL_MIGRATOR_POSTGRES`, una stringa di connessione separata con privilegi DDL, ricevuta tramite il gestore segreti / ambiente protetto. Non usare `peritial_app` o `peritial_recovery` né credenziali locali nei deploy.
- PostgreSQL 17+; mantenere separato l'account DDL dalle credenziali dell'applicazione. La migrazione iniziale `scripts/db/001_init.sql` crea i ruoli senza LOGIN; il provisioning di login/password è esterno. `002_dev_app_role.sql` contiene password dimostrative e **non è una migrazione di produzione**.
- Il journal `schema_migrations(version, filename, sha256, applied_at_utc)` è scrivibile solo dal migrator. Le versioni vengono applicate in ordine; eventuali buchi, script modificati, versioni sconosciute o database esistenti privi di journal producono errore prima di qualsiasi commit. Tutti gli script in coda sono eseguiti nella **stessa transazione PostgreSQL** insieme al journal, con `pg_advisory_xact_lock(4700039001)` per coordinare deploy concorrenti e fixture di test.
- Gli script sono incorporati nell'assembly: **V001** = `001_init.sql` (baseline per un DB nuovo), **V002** = `scripts/db/migrations/V002__security_audit_action_index.sql` (indice di lettura dell'audit). Non modificare gli script storici dopo il rilascio: creare sempre una nuova Vnnn e aggiornare l'elenco e la risorsa embedded.

## Esecuzione esplicita su database nuovo

1. Approvare lo schema previsto, creare un database Peritial dedicato e predisporre un ruolo DDL con privilegi per creare ruoli, tabelle, policy e indici. Prima dell'operazione acquisire backup e un piano di restore verificato.
2. Impostare `PERITIAL_MIGRATOR_POSTGRES` nell'ambiente di esecuzione sicuro (non in Git né nei log; evitare di inserire password nella command line).
3. Con .NET 10 SDK e repository su `main` eseguire:

   ```bash
   dotnet run --project scripts/db/Peritial.DbMigrator -- apply
   ```

4. Solo dopo l'uscita con codice 0 verificare il journal con il ruolo amministrativo:

   ```sql
   SELECT version, filename, sha256, applied_at_utc
   FROM schema_migrations ORDER BY version;
   SELECT relname, relrowsecurity, relforcerowsecurity
   FROM pg_class WHERE relname IN ('perizie','documents','security_audit','upload_intents','upload_request_keys');
   ```

5. Configurare **separatamente** i login applicazione e recovery con privilegi minimi e segreti reali. Eseguire smoke OAuth/MCP, verifica RLS, e quindi aprire il traffico. Una seconda esecuzione identica deve indicare 0 migrazioni nuove.

## Upgrade di un database esistente: blocco intenzionale

Se il database contiene tabelle Peritial senza journal, **`apply` le rifiuta**. Per un'installazione con l'esatto schema V001 verificato, esiste un percorso amministrativo, **mai automatico**: `adopt`. Se lo schema è privo della sola colonna `upload_intents.lease_expires_at_utc` e del relativo indice/grant, usare **soltanto** `adopt-prelease` (vedere sotto). Per qualunque altra divergenza, fermarsi e pianificare una migrazione specifica; non applicare `001_init.sql` di nuovo, non cancellare volumi e non creare un falso journal.

### Procedura V001 esatta: `adopt`

1. Prenotare una finestra di manutenzione, **fermare API REST/MCP, worker di recovery e ogni writer PostgreSQL**, conservare configurazione e backup separato di SeaweedFS/Azure Blob. Gli oggetti sono fuori dal database e non sono inclusi nel dump.
2. Con l'account PostgreSQL privilegiato, eseguire e conservare `sh scripts/db/backup.sh` come descritto in [database-backup-restore.md](database-backup-restore.md). Tenere l'archivio `pg_dump -Fc` e il suo file `.sha256` in directory protetta. Non riattivare i servizi durante la verifica.
3. Sul medesimo DB, con un'identità DDL separata dai ruoli applicativi, impostare le tre variabili seguenti mediante il secret manager o shell amministrativa protetta:

   ```bash
   export PERITIAL_MIGRATOR_POSTGRES='(connessione DDL PostgreSQL da secret manager)'
   export PERITIAL_LEGACY_BACKUP_FILE=/secure/backups/peritial-V001-before-adopt.dump
   export PERITIAL_LEGACY_CONFIRM=OFFLINE_BACKUP_VERIFIED
   dotnet run --project scripts/db/Peritial.DbMigrator -- adopt
   ```

   `adopt` rifiuta path non assoluti, file mancanti/symlink, SHA-256 errato, archivio non leggibile da `pg_restore --list`, consenso assente, journal già creato e ruoli DB mancanti/privilegiati. **Il digest verifica l'integrità del file, non la provenienza, la data o la corrispondenza con il database**: il change manager deve verificare questi aspetti separatamente.
4. Il migrator richiede lock esclusivi **NOWAIT** sulle cinque tabelle, crea uno schema temporaneo di riferimento in un savepoint, applica lì V001 e confronta strutturalmente tabelle/colonne, default, constraint, indici, validità, policy RLS, ACL, trigger e funzioni. Annulla il savepoint: la reference non sopravvive. Solo per match esatto registra il checksum V001 e applica V002 **nella stessa transazione**, senza rieseguire DDL distruttiva di V001 e senza scrivere righe tenant.
5. Dopo uscita con codice 0, verificare il journal V001/V002, tutte le policy FORCE RLS, grants, conteggi/ID delle righe tenant e audit; `dotnet run --project scripts/db/Peritial.DbMigrator -- apply` deve applicare 0 ulteriori versioni. Collegare i smoke test e controllare la presenza dei blob prima di riaprire traffico. Un secondo `adopt` è rifiutato.
6. Se un controllo fallisce: non ripetere forzando il journal, **non riaprire i writer**, confrontare schema e backup e predisporre manualmente una migrazione versionata su un clone. La transazione annulla schema/DDL in caso di errore; per danni esterni serve restore coordinato DB+object storage.

La CLI è testata in `tests/backup/run_legacy_adoption.sh` contro **un PostgreSQL 17 isolato con righe di due tenant**. Questa è una prova locale/CI, non un upgrade provato su un volume Azure/on-prem reale. Tra le lacune: firme del backup, snapshot PostgreSQL/S3 coordinati, rollback integrale, prerequisiti segreti e test di failover.

### Variante storica pre-lease: `adopt-prelease`

Per database PostgreSQL precedenti all'introduzione del lease degli upload, con tutte le altre tabelle, policy FORCE RLS e ACL equivalenti alla V001, è disponibile un comando **manuale distinto**. Non è un riparatore generico di schemi.

1. Fermare completamente API REST/MCP, worker di recovery e writer DB, predisporre il backup PostgreSQL e la copia separata dei blob, verificare il punto di ripristino. Come per `adopt`, impostare tramite ambiente amministrativo protetto `PERITIAL_MIGRATOR_POSTGRES`, `PERITIAL_LEGACY_BACKUP_FILE` (file assoluto PGDMP con `.sha256`) e `PERITIAL_LEGACY_CONFIRM=OFFLINE_BACKUP_VERIFIED`.
2. Invocare: `dotnet run --project scripts/db/Peritial.DbMigrator -- adopt-prelease`.
3. Il runner confronta tutto il catalogo esistente con una V001 temporanea, a cui è stata rimossa **solo** la colonna `lease_expires_at_utc` di `upload_intents` (insieme a indice e grant dipendenti). Se differisce altro, rifiuta l'adozione prima di creare il journal.
4. Dopo match esatto, aggiunge nella stessa transazione la colonna `timestamptz NOT NULL DEFAULT (now() + interval '2 minutes')`, `ix_upload_intents_lease` e `GRANT UPDATE (lease_expires_at_utc)` al ruolo app; verifica ancora il catalogo ricostruito; registra V001 e applica V002. In caso di errore torna allo schema precedente, senza perdere intenti/documenti. `apply` deve poi applicare 0 versioni.
5. Prima di riavviare il recovery worker, controllare gli intenti già pendenti e gli oggetti nello storage: il lease assegnato ai record storici **non prova che i blob esistano** o che il restore sia coerente con S3/Azure.

`adopt` rifiuta questa variante; `adopt-prelease` rifiuta una V001 già completa, la sola assenza dell'indice con colonna presente, qualunque ACL/RLS alterata e la presenza di un journal. I test xUnit e `tests/backup/run_prelease_adoption.sh` esercitano i casi con PostgreSQL 17 reale e backup PGDMP verificato. Restano mancanti firme dei backup, restore DB+blob coordinato, upgrade reali Azure/on-prem e supporto di altre varianti legacy (ADR 0018).

## Rollback, ripristino, test

- Script forward-only; non esiste un "down" automatico né rollback inter-app/oggetti. In caso di errore SQL prima del COMMIT, PostgreSQL ripristina tutte le modifiche e il journal; in caso di upgrade già committato, fermare i servizi e **ripristinare il backup** approvato mantenendo coerenza con l'object storage.
- La suite `SchemaMigrationTests` prova su PostgreSQL reale: prima installazione, re-run idempotente, rilevazione checksum modificato, schema legacy non tracciato senza journal, migrazioni concorrenti serializzate.
- CI: `dotnet test Peritial.slnx` con `PERITIAL_TEST_POSTGRES`; le prove avvengono in schemi isolati e non modificano dati dei tenant del test condiviso. Il job `database-recovery` ora prova backup/restore **solo PostgreSQL** su due server reali con ruoli RLS, audit e journal verificati (`docs/database-backup-restore.md`, ADR 0016). Il nuovo job CI prova anche la **baseline esplicita V001 di una demo legacy** con archivio PostgreSQL reale, rifiuto del consenso/manifest alterato, drift strutturale e conservazione dei dati. Restano privi di prova il restore coordinato dei blob, gli upgrade di **altre** varianti legacy divergenti, upgrade multi-release, Azure effettivo e rollback complessivo.

## Ambienti Compose demo

Il Compose corrente usa gli script `docker-entrypoint-initdb.d` per una **demo** e può montare volumi esistenti. I percorsi **manuali** V001 esatta e pre-lease riconosciuto sono documentati sopra; non eseguirlo a stack attivo né su database con drift non risolto. I due meccanismi non sono intercambiabili. La sostituzione del bootstrap Compose con un servizio migrator e il percorso di upgrade completo di volumi persistenti sono attività residue della #6/#18.
