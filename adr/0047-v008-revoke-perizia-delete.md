# ADR 0047 — V008: revoca del privilegio DELETE sulle Perizie

- Data: 18 novembre 2026
- Stato: implementata su `main`; verifica CI definitiva da attestare
- Issue: #6, #4, #23, #21
- Branch: `main` soltanto; `death` intatto.

## Contesto

Lo schema storico V001 concede a `peritial_app`
`SELECT,INSERT,UPDATE,DELETE` su `perizie`. Non
esiste però un caso d'uso autorizzato di cancellazione
Perizia in Business/REST/MCP/PWA. Le migrazioni V006
e V007 proteggono gli aggiornamenti alle associazioni
con una revisione monotona e provenienza auditata,
ma il privilegio DELETE sarebbe un'altra via di
rimozione: in particolare una Perizia già collegata
alla baseline V006 senza history potrebbe essere
cancellata direttamente da un client SQL con
credenziali di applicazione. Una FK RESTRICT
su una history successiva non protegge i record
storici senza history.

## Decisione

1. Migrazione forward-only
   `V008__perizie_revoke_app_delete.sql`:
   `REVOKE DELETE ON perizie FROM peritial_app` e
   `REVOKE DELETE ON perizie FROM PUBLIC`.
   **Nessun DELETE dati**, nessuna modifica a
   V001–V007, nessuna riscrittura di checksum e
   nessun cambio della struttura di una Perizia.
2. `peritial_app` conserva SELECT/INSERT/UPDATE
   e le policy tenant FORCE RLS già esistenti.
   `peritial_recovery` non riceve nuovi accessi.
   Se in futuro servirà un'operazione di
   cancellazione, dovrà avere policy retention,
   legal hold, approvazione, scope firmato dedicato
   e commit transazionale con audit, più restore.
   Il generico `peritial.write` non è sufficiente
   per inferire questi requisiti.
3. Gli altri vincoli V004–V007 (FK same-tenant,
   revision anti-ABA, history source/target e audit
   differito al commit) restano applicati per
   ogni link/unlink/reparent; V008 non cambia
   REST, MCP o PWA.
4. Il migrator privileggiato applica V008 dopo
   V007 e verifica checksum/journal di tutte
   le **otto** migrazioni. Il Compose demo
   unjournaled V001 non viene aggiornato
   automaticamente; solo Compose CI disposable
   prepara V003→V008.

## Test, confini e rischi

Il test-first
`V008_perizia_delete_is_not_granted_to_app_role_and_cannot_erase_legacy_association`
controlla grant PostgreSQL, un DELETE diretto
negato al ruolo `peritial_app` con SQLSTATE
`42501`, integrità del record e mantenimento
delle capacità di lettura/aggiornamento. CI
migrazione/adozione/backup/restore verificano
ora otto versioni e regressioni.
Questa revoca è difesa applicativa di minimo
privilegio, non protezione contro DDL owner,
superuser o manutenzioni amministrative. Non
soddisfa autonomamente policy di conservazione
o conformità WORM; le issue #23/#21/#6
restano aperte per tali requisiti.
