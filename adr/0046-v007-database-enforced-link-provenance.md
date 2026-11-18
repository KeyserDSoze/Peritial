# ADR 0046 — V007: guardia PostgreSQL per revisioni e storia delle associazioni

- Data: 18 novembre 2026
- Stato: implementazione su `main`; CI definitiva da attestare
- Issue: #23, #21, #6, #7
- Branch: `main` soltanto; `death` rimane storico e intatto.

## Motivazione

La V006 registra storia source/target e audit atomico attraverso
il repository. Una query SQL diretta in possesso dei privilegi
di scrittura su `perizie` potrebbe però modificare
`sinistro_id` senza incrementare `sinistro_link_version`
o potrebbe aggiornare entrambe senza inserire la provenienza.
FORCE RLS isola tenant, non impone questi invarianti. Occorre
un blocco anche a livello database, conservando l'ordine
transazionale dell'applicazione senza esporre SELECT
dell'audit/history al ruolo `peritial_app`.

## Decisione

1. Nuova migrazione V007 **solo additiva**, con checksum
   e journal amministrativo: nessuna modifica ai contenuti
   delle V001–V006, nessun DDL all'avvio dell'API.
   `SchemaMigrator` applica sette versioni.
2. Trigger `BEFORE INSERT OR UPDATE` su `perizie`:
   nuova Perizia deve partire scollegata con revisione 1;
   un cambio di `sinistro_id` deve incrementare
   `sinistro_link_version` esattamente di 1; se
   `sinistro_id` non cambia, non è lecito modificare la
   revisione. Non riguarda le normali transizioni
   `status`/workflow che non cambiano il collegamento.
3. Constraint trigger `AFTER UPDATE DEFERRABLE
   INITIALLY DEFERRED`, eseguito al commit solo se
   cambia `sinistro_id`: esige una riga
   `perizia_sinistro_history` con stesso tenant,
   Perizia, revisione, source e target; l'`audit_id`
   deve puntare a un evento `security_audit` con
   `resource_id=periziaId`, `outcome=succeeded`
   e action coerente con NULL→target (link),
   source→NULL (unlink) o source→target (reparent).
   Un audit `denied` o `perizia.create` non prova
   una transizione. Un fallimento annulla **l'intera**
   transazione (UPDATE + audit + history), anche
   quando l'UPDATE era già riuscito.
4. La verifica dei dati append-only usa una funzione
   SECURITY DEFINER posseduta dal ruolo DDL, necessaria
   perché l'app ha solo INSERT sulla history/audit.
   `search_path=pg_catalog` e nomi di schema
   derivati da `TG_TABLE_SCHEMA` con quoting
   impediscono ricerche su oggetti di schema
   controllato dal chiamante. Le funzioni helper
   non concedono EXECUTE a PUBLIC.
5. V007 non genera retroattivamente gli eventi
   precedenti all'upgrade, non espone nuovi endpoint
   REST/MCP e non altera il gate hard-delete #22.
   I privilegi di chi possiede DDL/superuser non
   costituiscono un archivio WORM: la governance
   approvazioni/retention, la prova forense e
   il rollout reale restano aperti (#23/#21/#6).

## Prova e compatibilità

Test scritti prima dell'implementazione: operazione
diretta senza revisione rifiutata immediatamente,
operazione con revisione ma senza storia rifiutata
al commit, evento storia legato ad audit di azione
errata rifiutato, mutazione mediata dal repository
autorizzata. Test migrator e restore PG17 con
sette versioni e fixture UPDATE+audit+history
in **un'unica transazione**, oltre a tutti i
test OAuth/Playwright REST/MCP e PWA esistenti.

È una difesa dei *write path* normali e dell'app
least-privileged; non sostituisce controlli esterni
su ruoli DDL, backup, legal hold o immutabilità WORM.
Le issue non si chiudono senza quei criteri.

## Evoluzione V008

La V007 impedisce UPDATE diretti e privi di storia
ma non disciplina DELETE dell'intera Perizia. La
successiva [ADR 0047](0047-v008-revoke-perizia-delete.md)
revoca al ruolo `peritial_app` il privilegio
DELETE originariamente concesso nella V001:
non essendoci un workflow di cancellazione Perizia,
nessuna ragione applicativa ne richiede il mantenimento.
Le versioni e checksum precedenti non vengono riscritti.
