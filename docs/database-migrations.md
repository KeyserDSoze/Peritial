# Migrazioni PostgreSQL versionate — fase iniziale (#6)

**Stato:** `apply` gestisce i database nuovi e già versionati; il nuovo comando **esplicito** `adopt` permette la baseline **solo per database preesistenti identici alla V001 `001_init.sql`**, con un archivio PostgreSQL integro, conferma di manutenzione e confronto esaustivo del catalogo. Non supporta schemi divergenti, aggiornamento automatico di volumi legacy o rollback/DR di produzione.

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

Se il database contiene tabelle Peritial senza journal, **`apply` le rifiuta**. Per un'installazione con l'esatto schema V001 verificato, esiste un percorso amministrativo, **mai automatico**: `adopt`. Se lo schema ha colonne/indici/policy differenti, fermarsi e pianificare una migrazione specifica; non applicare `001_init.sql` di nuovo, non cancellare volumi e non creare un falso journal.

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

## Rollback, ripristino, test

- Script forward-only; non esiste un "down" automatico né rollback inter-app/oggetti. In caso di errore SQL prima del COMMIT, PostgreSQL ripristina tutte le modifiche e il journal; in caso di upgrade già committato, fermare i servizi e **ripristinare il backup** approvato mantenendo coerenza con l'object storage.
- La suite `SchemaMigrationTests` prova su PostgreSQL reale: prima installazione, re-run idempotente, rilevazione checksum modificato, schema legacy non tracciato senza journal, migrazioni concorrenti serializzate.
- CI: `dotnet test Peritial.slnx` con `PERITIAL_TEST_POSTGRES`; le prove avvengono in schemi isolati e non modificano dati dei tenant del test condiviso. Il job `database-recovery` ora prova backup/restore **solo PostgreSQL** su due server reali con ruoli RLS, audit e journal verificati (`docs/database-backup-restore.md`, ADR 0016). Il nuovo job CI prova anche la **baseline esplicita V001 di una demo legacy** con archivio PostgreSQL reale, rifiuto del consenso/manifest alterato, drift strutturale e conservazione dei dati. Restano privi di prova il restore coordinato dei blob, gli upgrade di schemi legacy divergenti, upgrade multi-release, Azure effettivo e rollback complessivo.

## Ambienti Compose demo

Il Compose corrente usa gli script `docker-entrypoint-initdb.d` per una **demo** e può montare volumi esistenti. Il percorso **manuale** di adozione V001 esatta è ora documentato sopra; non eseguirlo a stack attivo né su database con drift non risolto. I due meccanismi non sono intercambiabili. La sostituzione del bootstrap Compose con un servizio migrator e il percorso di upgrade completo di volumi persistenti sono attività residue della #6/#18.
