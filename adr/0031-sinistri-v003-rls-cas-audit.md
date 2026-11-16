# ADR 0031 — Migrazione V003 e repository Sinistro con RLS, CAS e audit atomico

**Stato:** Accepted (fondazione persistente, senza esposizione REST/MCP; #7 e #21 parziali).  
**Data:** 2026-11-16

## Contesto

Il primo aggregate `Sinistro` (#7, ADR 0030) non aveva persistenza. Il porting
richiede un repository nuovo senza riprendere codice legacy da `death`, con
lo stesso isolamento multi-tenant già adottato per `perizie` e `documents`.
Le migrazioni V001 e V002 sono immutabili e sono parte del journal checksum.
L'audit di successo non può essere registrato fuori dalla transazione della
mutazione; altrimenti una scrittura potrebbe essere confermata senza traccia.

## Decisione

1. Creare la migrazione **additiva, forward-only V003**,
   `scripts/db/migrations/V003__sinistri_rls.sql`. `SchemaMigrator`
   la applica dopo V002 nel medesimo lock/transaction e ne verifica hash
   come le altre versioni. Non cambiare gli script V001/V002. Applica V003
   anche quando un operatore esegue `adopt` o `adopt-prelease` dopo la
   baseline verificata: l'adozione resta **manuale**, solo con backup e
   conferma esplicita.
2. Tabella `sinistri`: PK `(tenant_id,id)` UUID, `external_reference`
   varchar(100) non vuoto, `occurred_on` DATE, `reported_on` DATE nullable,
   `created_at_utc` timestamptz, `version` bigint positiva; indici tenant+
   data e tenant+riferimento. **Non** introdurre prematuramente un vincolo
   UNIQUE sul riferimento: la semantica di duplicati, `YearlyId` e
   assicurazioni nello storico deve essere verificata con dati realistici.
3. `ENABLE ROW LEVEL SECURITY` **e** `FORCE ROW LEVEL SECURITY`,
   policy `tenant_isolation_sinistri` per `peritial_app` con
   `current_setting('peritial.tenant_id', true)`; in assenza di tenant la
   query non restituisce righe e le scritture vengono rifiutate.
   `peritial_app` ottiene solo `SELECT, INSERT, UPDATE`, non `DELETE`;
   `peritial_recovery` non riceve accesso alla tabella.
4. `PgSinistroRepository` usa `TenantDbScope` e si limita alle query
   `ListAsync` (cap 1..100), `FindAsync`, `AddAuditedAsync` e
   `UpdateAuditedAsync`. **Nessuna scrittura non auditata** è esposta dal
   repository. Un audit `sinistro.create` o `sinistro.update` validato
   su tenant/risorsa/esito è scritto nella **stessa transazione PG**. Un
   errore audit comporta rollback di dati e versione.
5. Gli update verificano `WHERE tenant_id=... AND id=... AND version=@expected`
   e incrementano `version` nel DB. Il dominio aggiorna il proprio
   `PersistedVersion` **solo dopo COMMIT**, distinguendo row not-found da
   snapshot obsoleto; non si emette audit di successo su CAS fallita.
6. Le date legacy di tipo `datetime` non sono importate: scegliere DATE
   nel nuovo slice preserva solo fatti di calendario e va sottoposto a
   caratterizzazione prima dell'eventuale import automatico.

## Verifiche e limiti

Test xUnit reali su PostgreSQL in schemi isolati: V003+journal idempotente,
RLS di ruolo app senza tenant, test cross-tenant di SELECT/INSERT,
reset della connessione pooled (`MaxPoolSize=1`), audit atomico in caso
di insert/update riusciti e rollback in caso di ID audit duplicato,
stale write/CAS e versioni. I test di migrazione e backup/adozione
aggiornano il journal a tre versioni e controllano la nuova FORCE RLS.

V003 **non viene eseguita dal bootstrap demo V001**: la CLI migrator
esplicita è necessaria per creare la tabella su database versionati o
nuovi. La repository non è ancora registrata come use case di Business e
non esiste endpoint REST/MCP o UI: restano da definire permessi, schema
anagrafiche, ricerca e audit di rifiuto a livello applicativo. Nessuna
installazione già in uso è modificata automaticamente. #7 e #21 aperte.
