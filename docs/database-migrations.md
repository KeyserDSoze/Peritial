# Migrazioni PostgreSQL versionate — fase iniziale (#6)

**Stato:** runner transazionale disponibile per un **database Peritial vuoto**. Le installazioni preesistenti con schema creato da `001_init.sql` ma senza journal **non sono ancora aggiornabili automaticamente**. Non confondere l'esistenza del runner con una procedura di upgrade/rollback production-ready.

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

Se il database contiene `perizie`, `documents` o altre tabelle legacy senza journal, il runner **rifiuta** l'adozione e non registra una versione fittizia. Non creare `schema_migrations` a mano, non cancellare volumi e non ricreare tabelle. La procedura di baseline verificata dopo backup/restore, con controlli delle policy FORCE RLS, ruoli, colonne e dati, è ancora da implementare e testare su PostgreSQL persistente. Tracciare lo stato nella #6 e #18 prima di autorizzare rollout Azure/on-prem.

## Rollback, ripristino, test

- Script forward-only; non esiste un "down" automatico né rollback inter-app/oggetti. In caso di errore SQL prima del COMMIT, PostgreSQL ripristina tutte le modifiche e il journal; in caso di upgrade già committato, fermare i servizi e **ripristinare il backup** approvato mantenendo coerenza con l'object storage.
- La suite `SchemaMigrationTests` prova su PostgreSQL reale: prima installazione, re-run idempotente, rilevazione checksum modificato, schema legacy non tracciato senza journal, migrazioni concorrenti serializzate.
- CI: `dotnet test Peritial.slnx` con `PERITIAL_TEST_POSTGRES`; le prove avvengono in schemi isolati e non modificano dati dei tenant del test condiviso. Mancano ancora prove di restore/backups reali, migrazione volume legacy, upgrade multi-release, Azure effettivo e rollback coordinato.

## Ambienti Compose demo

Il Compose corrente usa gli script `docker-entrypoint-initdb.d` per una **demo** e può montare volumi esistenti. Non eseguire il migrator contro questo schema senza una procedura di baseline: i due meccanismi non sono intercambiabili. La sostituzione del bootstrap Compose con un servizio migrator e il percorso di upgrade dati persistenti sono attività residue della #6/#18.
