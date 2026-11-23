# Migrazioni PostgreSQL versionate — fase iniziale (#6)

**Stato:** `apply` gestisce i database nuovi e già versionati; il nuovo comando **esplicito** `adopt` permette la baseline **solo per database preesistenti identici alla V001 `001_init.sql`**, con un archivio PostgreSQL integro, conferma di manutenzione e confronto esaustivo del catalogo. La variante storica senza colonna/indice lease è ora supportata da un **comando distinto** `adopt-prelease`. Nessun altro drift, upgrade automatico o rollback/DR di produzione è supportato.

## Confini e identità

- Il comando amministrativo è `scripts/db/Peritial.DbMigrator`; non viene eseguito all'avvio REST/MCP. Usa `PERITIAL_MIGRATOR_POSTGRES`, una stringa di connessione separata con privilegi DDL, ricevuta tramite il gestore segreti / ambiente protetto. Non usare `peritial_app` o `peritial_recovery` né credenziali locali nei deploy.
- PostgreSQL 17+; mantenere separato l'account DDL dalle credenziali dell'applicazione. La migrazione iniziale `scripts/db/001_init.sql` crea i ruoli senza LOGIN; il provisioning di login/password è esterno. `002_dev_app_role.sql` contiene password dimostrative e **non è una migrazione di produzione**.
- Il journal `schema_migrations(version, filename, sha256, applied_at_utc)` è scrivibile solo dal migrator. Le versioni vengono applicate in ordine; eventuali buchi, script modificati, versioni sconosciute o database esistenti privi di journal producono errore prima di qualsiasi commit. Tutti gli script in coda sono eseguiti nella **stessa transazione PostgreSQL** insieme al journal, con `pg_advisory_xact_lock(4700039001)` per coordinare deploy concorrenti e fixture di test.
- Gli script sono incorporati nell'assembly: **V001** = `001_init.sql` (baseline per un DB nuovo), **V002** = `scripts/db/migrations/V002__security_audit_action_index.sql` (indice di lettura dell'audit); **V003** = `scripts/db/migrations/V003__sinistri_rls.sql` (tabella sinistri con `tenant_id`, `version` CAS, indici, policy FORCE RLS e privilegi minimi); **V004** = `scripts/db/migrations/V004__perizie_sinistro_link.sql` (link opzionale su `perizie.sinistro_id`, FK composta same-tenant e indice parziale). Non modificare gli script storici dopo il rilascio: creare sempre una nuova Vnnn e aggiornare l'elenco e la risorsa embedded.

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
   FROM pg_class WHERE relname IN ('perizie','documents','security_audit','upload_intents','upload_request_keys','sinistri');
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
4. Il migrator richiede lock esclusivi **NOWAIT** sulle cinque tabelle, crea uno schema temporaneo di riferimento in un savepoint, applica lì V001 e confronta strutturalmente tabelle/colonne, default, constraint, indici, validità, policy RLS, ACL, trigger e funzioni. Annulla il savepoint: la reference non sopravvive. Solo per match esatto registra il checksum V001 e applica V002/V003/V004 **nella stessa transazione**, senza rieseguire DDL distruttiva di V001 e senza scrivere righe tenant.
5. Dopo uscita con codice 0, verificare il journal V001/V002/V003/V004, tutte le policy FORCE RLS, grants, conteggi/ID delle righe tenant e audit; `dotnet run --project scripts/db/Peritial.DbMigrator -- apply` deve applicare 0 ulteriori versioni. Collegare i smoke test e controllare la presenza dei blob prima di riaprire traffico. Un secondo `adopt` è rifiutato.
6. Se un controllo fallisce: non ripetere forzando il journal, **non riaprire i writer**, confrontare schema e backup e predisporre manualmente una migrazione versionata su un clone. La transazione annulla schema/DDL in caso di errore; per danni esterni serve restore coordinato DB+object storage.

La CLI è testata in `tests/backup/run_legacy_adoption.sh` contro **un PostgreSQL 17 isolato con righe di due tenant**. Questa è una prova locale/CI, non un upgrade provato su un volume Azure/on-prem reale. Tra le lacune: firme del backup, snapshot PostgreSQL/S3 coordinati, rollback integrale, prerequisiti segreti e test di failover.

### Variante storica pre-lease: `adopt-prelease`

Per database PostgreSQL precedenti all'introduzione del lease degli upload, con tutte le altre tabelle, policy FORCE RLS e ACL equivalenti alla V001, è disponibile un comando **manuale distinto**. Non è un riparatore generico di schemi.

1. Fermare completamente API REST/MCP, worker di recovery e writer DB, predisporre il backup PostgreSQL e la copia separata dei blob, verificare il punto di ripristino. Come per `adopt`, impostare tramite ambiente amministrativo protetto `PERITIAL_MIGRATOR_POSTGRES`, `PERITIAL_LEGACY_BACKUP_FILE` (file assoluto PGDMP con `.sha256`) e `PERITIAL_LEGACY_CONFIRM=OFFLINE_BACKUP_VERIFIED`.
2. Invocare: `dotnet run --project scripts/db/Peritial.DbMigrator -- adopt-prelease`.
3. Il runner confronta tutto il catalogo esistente con una V001 temporanea, a cui è stata rimossa **solo** la colonna `lease_expires_at_utc` di `upload_intents` (insieme a indice e grant dipendenti). Se differisce altro, rifiuta l'adozione prima di creare il journal.
4. Dopo match esatto, aggiunge nella stessa transazione la colonna `timestamptz NOT NULL DEFAULT (now() + interval '2 minutes')`, `ix_upload_intents_lease` e `GRANT UPDATE (lease_expires_at_utc)` al ruolo app; verifica ancora il catalogo ricostruito; registra V001 e applica V002/V003/V004. In caso di errore torna allo schema precedente, senza perdere intenti/documenti. `apply` deve poi applicare 0 versioni.
5. Prima di riavviare il recovery worker, controllare gli intenti già pendenti e gli oggetti nello storage: il lease assegnato ai record storici **non prova che i blob esistano** o che il restore sia coerente con S3/Azure.

`adopt` rifiuta questa variante; `adopt-prelease` rifiuta una V001 già completa, la sola assenza dell'indice con colonna presente, qualunque ACL/RLS alterata e la presenza di un journal. I test xUnit e `tests/backup/run_prelease_adoption.sh` esercitano i casi con PostgreSQL 17 reale e backup PGDMP verificato. Restano mancanti firme dei backup, restore DB+blob coordinato, upgrade reali Azure/on-prem e supporto di altre varianti legacy (ADR 0018).


### V003 — isolamento sinistri (incremento #7)

L'applicazione di V003 avviene esclusivamente tramite `apply` del
migrator con account DDL su un database già versionato oppure nuovo.
Le migrazioni **V001 e V002 sono immutabili**: gli hash vengono verificati
prima di applicare V003; su una installazione V002 il migrator aggiunge
solo V003, senza reimpostare le tabelle esistenti. L'upgrade conserva
tutti i dati perizie, documenti, audit e upload già presenti.

`sinistri` possiede `(tenant_id,id)` come chiave primaria e
`version bigint` per il compare-and-swap applicativo;
`peritial_app` ha solo SELECT/INSERT/UPDATE tramite policy tenant
**ENABLE+FORCE RLS**, `peritial_recovery` non ha SELECT e il
ruolo app non può DELETE. Il riferimento esterno resta non univoco.
Il bootstrap Compose demo esegue soltanto V001, pertanto **la
tabella sinistri non esiste nel Compose di default** fino
all'applicazione esplicita del migrator, e non è esposta via REST/MCP.
Non eseguire migrazioni contro il ruolo `peritial_app`, non alterare
il bootstrap storico per simulare un upgrade.

I test `SinistroPersistenceTests` costruiscono schemi isolati usando
`SchemaMigrator.ApplyAsync` e verificano catalogo, FORCE RLS, accessi
negati senza tenant, isolamento cross-tenant, audit atomico e
concorrenza CAS; la suite `LegacySchemaAdoptionTests` e gli
script di backup/restore sono aggiornati al journal a tre versioni.
La migrazione non cambia i datetime del legacy né importa sinistri.
Si veda [ADR 0031](../adr/0031-sinistri-v003-rls-cas-audit.md).

### V004 — collegamento opzionale Perizie–Sinistri (#7/#9)

La nuova migrazione `V004__perizie_sinistro_link.sql` **non**
sposta né modifica le righe tenant esistenti: aggiunge
`perizie.sinistro_id uuid NULL`, FK
`(tenant_id,sinistro_id) REFERENCES sinistri(tenant_id,id)`
con `ON UPDATE/DELETE RESTRICT`, indice parziale per
elencare le perizie associate e nessuna nuova tabella.
Un sinistro può riferire più perizie; una perizia può essere
collegata a un solo sinistro, e **la riassegnazione è
deliberatamente negata** finché non verrà sviluppato
uno specifico use case con audit. Le perizie preesistenti
restano non associate.

V004 richiede prima V003 e viene **applicata solo**
dal migrator forward-only su database versionati e
adottati esplicitamente (con lock, checksum e backup),
mai dal bootstrap demo o all'avvio API. Le condizioni
di upgrade esistenti rimangono valide: account DDL
distinto dall'app, backup verificato e blocco dei writers.
La CI Compose effimera applica V003 **poi V004 come
fixture**, senza fingere un journal. I test DB reali
verificano anche l'UPDATE amministrativo con FK
cross-tenant rifiutato, lo stesso confronto CAS e
rollback dell'audit. Il restore su server PostgreSQL
indipendente controlla che il link e il journal a
**quattro versioni** sopravvivano. V001–V003 restano
immutate. Vedere [ADR 0034](../adr/0034-perizie-sinistri-v004-tenant-fk.md).

## Rollback, ripristino, test

- Script forward-only; non esiste un "down" automatico né rollback inter-app/oggetti. In caso di errore SQL prima del COMMIT, PostgreSQL ripristina tutte le modifiche e il journal; in caso di upgrade già committato, fermare i servizi e **ripristinare il backup** approvato mantenendo coerenza con l'object storage.
- La suite `SchemaMigrationTests` prova su PostgreSQL reale: prima installazione, re-run idempotente, rilevazione checksum modificato, schema legacy non tracciato senza journal, migrazioni concorrenti serializzate.
- CI: `dotnet test Peritial.slnx` con `PERITIAL_TEST_POSTGRES`; le prove avvengono in schemi isolati e non modificano dati dei tenant del test condiviso. Il job `database-recovery` ora prova backup/restore **solo PostgreSQL** su due server reali con ruoli RLS, audit e journal verificati (`docs/database-backup-restore.md`, ADR 0016). Il nuovo job CI prova anche la **baseline esplicita V001 di una demo legacy** con archivio PostgreSQL reale, rifiuto del consenso/manifest alterato, drift strutturale e conservazione dei dati. Restano privi di prova il restore coordinato dei blob, gli upgrade di **altre** varianti legacy divergenti, upgrade multi-release, Azure effettivo e rollback complessivo.

## Ambienti Compose demo

Il Compose corrente usa gli script `docker-entrypoint-initdb.d` per una **demo** e può montare volumi esistenti. I percorsi **manuali** V001 esatta e pre-lease riconosciuto sono documentati sopra; non eseguirlo a stack attivo né su database con drift non risolto. I due meccanismi non sono intercambiabili. La sostituzione del bootstrap Compose con un servizio migrator e il percorso di upgrade completo di volumi persistenti sono attività residue della #6/#18.

## V005 — privilegio DELETE minimo per Sinistri (#6/#7/#21)

`scripts/db/migrations/V005__sinistri_audited_delete.sql` è una
migrazione **solo additiva nei privilegi**: concede `DELETE` su
`sinistri` unicamente al ruolo `peritial_app`, senza alterare
righe, PK, FORCED RLS o la FK V004 `ON DELETE RESTRICT`.
Non concede alcun privilegio a `peritial_recovery` o `PUBLIC`.
`SchemaMigrator` ne registra filename e SHA-256 in versione **5**
in transazione insieme alle versioni pendenti. Le migrazioni
V001–V004 **non sono state ritoccate**; versioni storiche e
checksum rimangono vincolanti. Sul DB V004 esistente con backup
verificato usare **solo il CLI privilegiato** `apply` fermando i
writer come da runbook: il database demo V001 Compose non si migra
automaticamente. Per adozione esatta V001 o prelease, il percorso
esplicito `adopt`/ `adopt-prelease` ora registra V001–V005,
preservando tutti i dati preesistenti.

La CI Compose applica le fixture additive V003, poi V004, poi V005
nel solo database effimero, senza scrivere un journal fittizio.
Test migrator, adozione con restore, PostgreSQL least privilege e
regressioni REST/MCP dimostrano il percorso. La semantica business di
cancellazione di record senza Perizie è nell'[ADR 0041](../adr/0041-sinistri-v005-audited-cas-delete.md);
retention e legal hold applicativi ancora aperti. Non promuovere questa
nuova mutazione senza le procedure operative richieste.

## V006 — revisione monotona associazioni e provenienza (#6/#23)

La migrazione forward-only
`scripts/db/migrations/V006__perizie_link_revision_history.sql`
aggiunge `perizie.sinistro_link_version bigint NOT NULL DEFAULT 1`
con check positivo. Non cambia valori di `sinistro_id` o lo
stato workflow, non modifica V001–V005 e non ricostruisce
la storia impossibile da conoscere delle associazioni
precedenti. La nuova tabella append-only
`perizia_sinistro_history` possiede PK
`(tenant_id,perizia_id,revision)`, FK same-tenant verso
Perizie e audit, e politica INSERT-only FORCE RLS.
`peritial_app` ha solo INSERT, `peritial_recovery`
non riceve accesso; non si concedono SELECT a client web/MCP.

**Upgrade operativo:** backup coerente già verificato,
fermare writer vecchi, applicare V006 con CLI DDL
`apply`, verificare journal V001–V006 e permessi,
quindi distribuire API e PWA che inviano sempre
`expectedLinkVersion`. Durante la transizione gli
operatori vecchi senza versione riceveranno 400 e
non potranno mutare la relazione; non aggiungere
fallback permissivi. Compose V001 demo non viene
migrato automaticamente; soltanto Compose **CI
disposable** applica V003, V004, V005 e V006
in ordine. I drill PG restore e adozioni legacy
verificano ora 6 versioni e la baseline di revisione.
Per testare l'upgrade reale V005→V006 in staging
con dati, permessi e approvazioni di compliance è
ancora necessaria la checklist di #23 e #6.
[ADR 0045](../adr/0045-perizia-sinistro-v006-aba-revision-provenance.md).

## V007 — guardia PostgreSQL per collegamenti non auditati (#23/#21)

La migrazione `V007__perizie_link_audit_guards.sql`, **forward-only**
e senza toccare V001–V006, aggiunge due controlli complementari al
CAS del servizio. Un trigger BEFORE INSERT/UPDATE impedisce la
creazione diretta di Perizie già collegate e qualsiasi cambio
`sinistro_id` che non incrementi `sinistro_link_version`
**esattamente di uno**; una revisione non può cambiare da sola.
Un constraint trigger AFTER UPDATE **DEFERRABLE INITIALLY DEFERRED**
verifica al commit che esistano la riga history con tenant, Perizia,
revisione e source/target esatti e l'evento `security_audit`
con resource ID, azione `perizia.link-sinistro`,
`perizia.unlink-sinistro` o `perizia.reparent-sinistro`
e outcome `succeeded`. Non basta un evento `denied` o
di un'altra azione. Il check differito permette la sequenza
legittima UPDATE → audit INSERT → history INSERT nella
**medesima transazione**; se manca uno degli elementi, PostgreSQL
annulla l'intero commit e non cambia né il collegamento né
la revisione.

Il verificatore usa un helper SECURITY DEFINER con
`search_path=pg_catalog`: consulta solamente le tabelle dello
schema effettivo del trigger, referenziandole con identificatori
quotati, perché `peritial_app` ha **solo INSERT** sugli eventi
storici e sull'audit. L'esecuzione diretta delle funzioni non
è concessa a PUBLIC. Non sono autorizzati nuovi endpoint REST/MCP
di lettura della storia e non vengono inseriti eventi inventati
per la baseline V006. Questo è un **controllo di integrità tecnica**,
non una garanzia WORM/compliance contro gli amministratori DDL
o un sostituto delle policy retention/approvazioni (#23).
[ADR 0046](../adr/0046-v007-database-enforced-link-provenance.md).

**Runbook V007:** applicare con il migrator DDL privilegiato,
dopo backup testato e blocco dei writer legacy, prima del deploy
API/PWA; il nuovo journal ha **sette versioni**. La CI Compose
effimera applica V003→V007 in ordine; il drill restore ora
mantiene una singola transazione esplicita per UPDATE,
audit e history, come richiesto dal trigger differito.
L'installazione reale e la policy di consultazione/compliance
devono essere validate dagli owner prima di segnare conclusa #6.

## V008 — revoca DELETE Perizie al ruolo applicativo (#6/#4/#23)

La V008 `V008__perizie_revoke_app_delete.sql` è una migrazione
**forward-only, senza DML**: revoca il privilegio `DELETE` su
`perizie` a `peritial_app` e `PUBLIC`. Le Perizie esistenti,
i Sinistri collegati, l'audit e la storia V006 restano invariati.
Il codice REST/MCP non espone una cancellazione Perizie:
il privilegio legacy V001 non è più necessario e avrebbe
permesso di eliminare una Perizia senza passare attraverso
la revisione V007, soprattutto per link creati prima
della baseline V006 privi di righe storiche.
`peritial_app` conserva i privilegi `SELECT,INSERT,UPDATE`
necessari al workflow; `peritial_recovery` continua
a non avere `DELETE`. Il test PostgreSQL con ruolo app
verifica un `DELETE` rifiutato con `42501` e la
persistenza della Perizia. Nessun automatismo di
cancellazione sostitutivo viene introdotto.

Il journal amministrativo ora arriva alla **V008 (8 versioni)**.
La CI applica fixture additive V003–V008 solamente al
database effimero; i database reali devono seguire il
migrator DDL con backup verificato e maintenance. In futuro
qualsiasi proposta di cancellazione/anonimizzazione Perizie
richiederà policy di retention, scope dedicato, audit,
approvazione e prove di ripristino specifiche; nessun
permesso di cancellazione va riattivato per aggirare
questi controlli.
[ADR 0047](../adr/0047-v008-revoke-perizia-delete.md).

## V009 — revoca UPDATE/DELETE metadati documentali (#5/#6/#11/#21)

La migrazione
`V009__documents_revoke_app_mutations.sql` riduce il
privilegio su `documents` a `SELECT`/`INSERT` per
`peritial_app`, revocando `UPDATE` e `DELETE`
anche a `PUBLIC`. Nessuna riga o byte nello storage
è modificato. L'account `peritial_recovery` mantiene
la sola lettura ristretta necessaria alla reconciliation.
La V009 non modifica V001–V008 e il journal contiene
**nove versioni**, con checksum e replay idempotente.

L'upgrade richiede backup e punto di recovery verificati,
fermo dei writer e `apply` eseguito dal ruolo DDL; in staging
verificare i privilegi mediante
`has_table_privilege('peritial_app','documents','UPDATE')`
e `DELETE`, entrambi falsi, e un test di upload/catalogo.
Solo la CI Compose effimera applica le fixture V003→V009
al database demo V001 non versionato. Questo cambiamento
blocca DML non autorizzato ma non implementa un protocollo
di rimozione/retention, legal hold o WORM. [ADR 0048](../adr/0048-v009-immutable-document-metadata-privileges.md).

## V010 — indici ordinati del catalogo documenti (#11/#6)

La migrazione forward-only
`V010__documents_catalog_keyset_indexes.sql` aggiunge
due indici PostgreSQL B-tree senza cambiare righe, FK,
tenant RLS, privilegi applicativi, audit o storage:

- `ix_documents_tenant_created_id`:
  `(tenant_id, created_at_utc DESC, id DESC)` per le pagine globali;
- `ix_documents_tenant_perizia_created_id`:
  `(tenant_id, perizia_id, created_at_utc DESC, id DESC)` per
  le pagine filtrate per Perizia.

La query `PgDocumentCatalogRepository.PageAsync` usa un
predicato di seek con confronto tuple
`(created_at_utc,id) < (@afterTime,@afterId)` quando
c'è un cursore, invece del precedente `OR` con parametri nulli.
Il predicato sfrutta l'ordine discendente dell'indice e
rimane sotto `TenantDbScope` e FORCE RLS; i servizi REST/MCP
e i vecchi endpoint di lista non cambiano contratto.

**Avvertenza di release:** il migrator DDL applica
`CREATE INDEX` normale nella transazione del journal V010.
Non è un build `CONCURRENTLY`; su tabelle grandi può attendere
lock e **bloccare le scritture**. Pianificare finestra offline,
fermo dei writer e backup PostgreSQL più bucket verificati,
test di restore e spazio disco adeguato prima di applicare
`apply` come owner dello schema. Non lanciare lo script
direttamente come ruolo `peritial_app`, non introdurre
`IF NOT EXISTS` che mascheri oggetti in drift e non
toccare V001–V009. La CI Compose usa V003→V010
**solo su database effimero**.

Il test PostgreSQL verifica entrambe le definizioni di indice,
stato `indisvalid/indisready` e un `EXPLAIN` controllato
con scan alternativi disabilitati. Questo prova la forma
del piano, **non** latenze o regressioni a scala reale; 
si devono ancora misurare cardinalità tenant reali,
dimensione indici, latenza p95/p99, budget I/O e costo
degli upload in staging. [ADR 0050](../adr/0050-v010-document-keyset-indexes.md).

## V011 — ledger idempotenza import XML interno (#10/#6/#21)

La migrazione forward-only
`V011__legacy_xml_import_idempotency.sql`
aggiunge una nuova tabella senza modificare
righe preesistenti, V001–V010, ruoli o grant
delle tabelle storiche. Il journal versionato
arriva a **11 versioni** con SHA-256 e replay
idempotente.

`legacy_xml_import_keys` ha PK su
`(tenant_id,subject_digest,key_digest)`,
SHA-256 per request e una FK differibile
a `(tenant_id,sinistro_id)`. Non contiene
XML, PII, chiavi originali né contenuti
binari. `peritial_app` ha solamente
SELECT/INSERT con FORCE RLS su tenant;
`PUBLIC` e il ruolo di recovery non hanno
accesso. Non è consentita una rimozione
silenziosa della prova d'idempotenza.
La FK impedisce la cancellazione di un
Sinistro importato, richiedendo una futura
policy approvata di retention/erasure.

Upgrade con migrator DDL e backup/recovery
verificati, mantenendo i writer fermi:
`CREATE TABLE` e la FK possono richiedere
lock temporanei su `sinistri`. La CI
Compose applica fixture **V003→V011**
solo sul database demo effimero V001
non versionato. Il REST/MCP di import
effettivo NON viene attivato nella V011;
gli unici endpoint XML restano preview
e piano read-only. [ADR 0053](../adr/0053-v011-xml-case-only-atomic-idempotency.md).

## V012 — protezione globale delle referenze importate

Nuova migrazione forward-only
`V012__imported_sinistri_global_reference_guard.sql`
nel journal con SHA-256 (12 versioni totali).
Non modifica dati o checksum V001–V011 e non
introduce un vincolo UNIQUE indiscriminato.
In un'unica transazione prende
`LOCK TABLE sinistri IN SHARE ROW EXCLUSIVE MODE`,
ricerca collisioni preesistenti per prove V011 e
**rifiuta l'upgrade** in caso di collisione.
Crea una funzione invoker e trigger row-level
INSERT/UPDATE; tutti i writer app-role sono
serializzati per riferimento importato, sotto
FORCE RLS, senza privilegi aggiuntivi sulle
tabelle legacy. Non rimuovere la serratura
o il controllo dati durante il deployment.

Per il migrator .NET non serve cambiare il processo:
le migrazioni vengono già eseguite come una sola
transazione DDL. Per la fixture Compose V001
non versionata, eseguire lo script V012 con
`psql --single-transaction -f /dev/stdin`
anziché STDIN autocommit; gli script V003–V011
restano invariati. Serve una finestra di
manutenzione con writer fermi e backup verificato.
Il rollback operativo richiede restore gestito,
non il `DROP TRIGGER` in un database popolato.
[ADR 0054](../adr/0054-v012-imported-reference-global-sql-guard.md).

## V013 — SHA-256 privato dei documenti e gate di download

La [ADR 0110](../adr/0110-document-sha256-admission-and-verified-download.md)
introduce `V013__documents_content_sha256_integrity.sql`, aggiunta
**forward-only e transazionale** al migrator .NET: 13 script nel
journal con checksum SHA-256 e replay idempotente. Gli script
V001–V012 restano immutati.

La nuova colonna `documents.content_sha256 bytea` è nullable **solo
per compatibilità con record precedenti** e vincolata a 32 byte
quando presente. La migrazione non legge, normalizza o riscrive
blob/metadati e **non inventa checksum** per documenti già
archiviati. Un trigger rifiuta ogni successivo UPDATE del digest;
`peritial_app` mantiene i divieti UPDATE/DELETE di V009 e
FORCE RLS. L'ingestione ordinaria obbliga SHA-256 dopo ClamAV,
prima del PUT, e lo persiste nella stessa transazione di metadati,
audit successo e completamento idempotency.

**Avvertenza operativa:** il servizio di download ora rifiuta i record
senza SHA-256. Prima di applicare V013 a un database reale, effettuare
backup e inventario dei documenti legacy senza digest; predisporre
una procedura approvata di ri-scansione e backfill, o accettare
formalmente il blocco del download fino alla bonifica. Mai generare
un digest da un blob non verificato assumendolo automaticamente
sicuro. La versione V013 non abilita da sola la governance della
quarantena, retention/legal hold e versioning, né sostituisce test
Azure/on-prem. Il vincolo DB consente ancora null su INSERT SQL
raw; il port di produzione `AddAuditedAsync` nega null e la issue
[#27](https://github.com/KeyserDSoze/Peritial/issues/27) traccia
l'enforcement completo e la policy legacy.

I test PostgreSQL `SchemaMigrationTests` verificano replay V013,
immutabilità digest e vincolo lunghezza; `DocumentCatalogRepositoryTests`
verifica il lookup privato FORCE RLS e assenza dal DTO pubblico;
`DocumentAtomicAuditTests` mantiene la transazione di commit.
Monitorare CI backend e Compose dell'ultimo HEAD senza
considerare un solo job verde come attestazione end-to-end.
