# Sinistri — primo aggregate di dominio (#7)

Il porting dei sinistri inizia con `src/backend/Domains/Sinistro.cs`
e `tests/backend/Peritial.Domains.Tests/SinistroTests.cs`.
Questo è un **incremento interno di dominio**, non una CRUD disponibile.

## Caratterizzazione minima

Una ricognizione *sola lettura* dello storico `death` individua
`ExternalId` (obbligatorio, massimo 100 caratteri),
`AccidentDate` e `DenunciationDate` (opzionale), oltre a riferimenti
a assicurazione, agenzia, località e perizie. Non sono stati copiati
componenti legacy.

Il modello nuovo espone:
- `Id: Guid` e `TenantId` esplicito e validato.
- `ExternalReference: string` normalizzato con trim, lunghezza 1–100.
- `OccurredOn: DateOnly` e `ReportedOn: DateOnly?` come **sole date di
  calendario provvisorie**; non è presunta una cronologia di denuncia.
- `CreatedAtUtc: DateTime` di creazione, più `Restore` per future
  implementazioni di persistenza.
- `Update(actorTenant, ...)` con blocco cross-tenant e validazione prima
  di applicare tutte le modifiche.

**Limiti critici:** nello storico la data evento è `datetime`, dunque
ore, timezone e rappresentazione di dati importati dovranno essere
caratterizzate prima di importare eventi nel nuovo schema. Il solo
controllo `TenantId` nell'aggregate non costituisce autorizzazione
REST/MCP; l'isolamento DB è invece applicato dalla nuova V003.

## Lavoro ancora necessario per #7

- Contratti REST/MCP con scope e tenant derivato dal JWT firmato.
- Registrazione del repository nella Business layer, use case autorizzati e
  audit di esito/rifiuto; V003, FORCE RLS, CAS e audit dei successi sono ora
  implementati e testati nel repository non ancora esposto.
- CRUD e ricerca/autocomplete con filtri e paginazione.
- Collegamento a compagnie, polizze, anagrafiche e perizie (#8);
  import Classic/Accertatore (#10) senza assunzioni sul legacy.
- UI PWA it/en, test E2E desktop/mobile e documentazione API.

Riferimento decisione: [ADR 0030](../adr/0030-tenant-scoped-sinistro-domain.md).

## Persistenza PostgreSQL V003 — fondazione senza endpoint

La migrazione **forward-only** `V003__sinistri_rls.sql`, registrata dal
`SchemaMigrator` e incorporata nell'assembly, aggiunge una tabella
`sinistri` dedicata con PK composita `(tenant_id,id)`, riferimento esterno
varchar(100) non vuoto, data evento `date`, data denuncia `date` nullable,
data creazione `timestamptz` e `version bigint` per CAS.

La policy `tenant_isolation_sinistri` abilita **FORCE RLS**; il ruolo app
può fare solo SELECT/INSERT/UPDATE usando il tenant di transazione impostato
da `TenantDbScope`, non DELETE. Il recovery worker non ha accesso.
In assenza di contesto tenant le letture non vedono dati. Il riferimento
esterno è indicizzato **ma non UNIQUE**, in attesa di chiarire la
semantica dei duplicati storici e dei codici `YearlyId`.

`PgSinistroRepository` ha letture tenant-scoped `ListAsync` (limite 1–100),
`FindAsync` e **soltanto** scritture auditate `AddAuditedAsync` /
`UpdateAuditedAsync`: evento `sinistro.create` o `sinistro.update`
append-only nello **stesso commit PostgreSQL** dell'aggregate.
L'aggiornamento usa `version` nel predicato CAS: una lettura obsoleta
non sovrascrive modifiche successive, e un audit fallito annulla la
mutazione. La versione attesa del dominio aumenta solo dopo commit.

I test `SinistroPersistenceTests` usano PostgreSQL reale con schemi
isolati, il ruolo `peritial_app` non privilegiato, pool da una connessione,
rifiuto di scrittura cross-tenant, rollback dell'audit e conflitto CAS.
La suite delle migrazioni copre V001–V003 anche in caso di adozione
esplicita legacy e backup/restore. Il bootstrap demo `001_init.sql`
**non include V003**: chi vuole esercitare la tabella deve lanciare
esplicitamente il migrator DDL in un ambiente appropriato prima di
usare il repository, seguendo `docs/database-migrations.md`.
Nessun servizio espone ancora le operazioni sui sinistri.

**Restano fuori scope:** API REST/MCP, use case Business con JWT/scope,
audit dei rifiuti, relazioni anagrafiche e perizie, delete, import/merge,
ricerca/autocomplete, UI ed E2E. I campi temporali di data-only richiedono
un confronto con i datetime del legacy prima di importare dati.
Decisione: [ADR 0031](../adr/0031-sinistri-v003-rls-cas-audit.md).

