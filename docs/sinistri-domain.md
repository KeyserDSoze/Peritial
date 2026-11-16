# Sinistri — dominio, PostgreSQL e primi use case REST/MCP (#7)

Il porting dei sinistri inizia con `src/backend/Domains/Sinistro.cs`
e `tests/backend/Peritial.Domains.Tests/SinistroTests.cs`.
Il dominio e la migrazione V003 sono seguiti dal primo slice REST/MCP
**read/create/update**. Mancano ancora DELETE, relazioni, import e UI.

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

- Eliminazione e politiche di retention con audit e autorizzazione;
  non è ancora un CRUD completo.
- Ricerca/autocomplete con filtri, cursor e paginazione oltre i primi 100.
- Test REST/MCP con identità di due tenant realmente distinti, oltre
  alle verifiche di isolamento PostgreSQL già presenti.
- Collegamento a compagnie, polizze, anagrafiche e perizie (#8);
  import Classic/Accertatore (#10) senza assunzioni sul legacy.
- UI PWA it/en, test E2E desktop/mobile e documentazione API.

Riferimento decisione: [ADR 0030](../adr/0030-tenant-scoped-sinistro-domain.md).

## Persistenza PostgreSQL V003 — repository protetto da RLS

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
REST e MCP riusano ora il servizio Business autorizzato che accede a questo repository.

**Restano fuori scope:** relazioni anagrafiche e perizie, delete, import/merge,
ricerca/autocomplete, UI e verifica E2E cross-tenant con due identità diverse. I campi temporali di data-only richiedono
un confronto con i datetime del legacy prima di importare dati.
Decisione: [ADR 0031](../adr/0031-sinistri-v003-rls-cas-audit.md).


## Business, REST e MCP — prima superficie firmata (#7/#12)

`SinistroService` riceve `ITenantContext` da una identità OAuth
validata: il client fornisce **solo** il riferimento esterno, la data
di calendario `occurredOn`, l'eventuale `reportedOn` e
`expectedVersion` sulle modifiche. I DTO escludono il tenant ID
e includono la versione persistita. Le letture sono autorizzate
per `peritial.read` o `peritial.write`; create/update
richiedono `peritial.write` e l'audit dei rifiuti registra
solo azione e subject firmato, senza contenuto della richiesta.

| REST | Tool MCP | Permesso |
|---|---|---|
| `GET /api/sinistri?limit=50` | `list_sinistri(limit=50)` | read o write |
| `GET /api/sinistri/{id}` | `get_sinistro(id)` | read o write |
| `POST /api/sinistri` | `create_sinistro(...)` | write |
| `PUT /api/sinistri/{id}` | `update_sinistro(...,expectedVersion)` | write |

Le due mutazioni passano solo da metodi repository auditate
nella stessa transazione del salvataggio. Un
`expectedVersion` non valido ritorna 400,
uno snapshot obsoleto produce **409 REST** oppure
un errore tool MCP; un identificativo di altro tenant
resta **404 REST**. Il servizio fa un primo controllo
versione, e PostgreSQL ne garantisce l'atomicità
sotto concorrenza reale. Non esiste ancora `DELETE`.

**Test:** `SinistroServiceTests` copre il RED/GREEN Business,
scope read-only/write, audit negato e riuscito, tenant
non enumerabile, ID mancante, cap 1–100 e CAS. Il test
Playwright `authenticated.spec.ts` usa OAuth Keycloak,
REST e MCP sulla stessa identità e verifica 201,
GET/list, update, stale 409, 401 anonimo e
`Cache-Control: no-store`, senza esporre tenant ID.

**Demo locale:** lo storico Compose V001 non è una
installazione V003. La CI effimera applica soltanto
`V003__sinistri_rls.sql` come fixture prima dell'E2E,
senza alterare bootstrap o registrare un finto journal
V001–V003. Per ambienti reali migrare offline con
`dotnet run --project scripts/db/Peritial.DbMigrator -- apply`
su database **già versionati** con ruolo DDL separato,
backup e procedure del runbook. Non usare il trucco
della fixture CI in produzione.

Riferimento: [ADR 0032](../adr/0032-sinistri-rest-mcp-oauth-cas.md).

## Prova di isolamento OAuth reale con due tenant (#7/#4/#12)

In aggiunta ai test xUnit PostgreSQL FORCE RLS, la suite
`tests/frontend/security.spec.ts` esegue REST e MCP con **due JWT
firmati da Keycloak** su identità separate: test tenant A
`11111111-1111-1111-1111-111111111111` e tenant B
`22222222-2222-2222-2222-222222222222`. La fixture
`docker/keycloak/peritial-realm.json` contiene esclusivamente
utenti/client di laboratorio che non vanno adottati in produzione.

La prova crea un sinistro A via REST e uno B via MCP. In entrambe
le direzioni: il catalogo REST/MCP non contiene risorse altrui,
`GET` su UUID esterno restituisce lo **stesso 404** di un UUID
inesistente, `PUT` verso il tenant altrui restituisce **404**
senza cambiare dati, e le tool call `get_sinistro` /
`update_sinistro` restituiscono `isError:true` senza
esporre il riferimento. Un `X-Tenant-Id` e un `tenantId`
falsificati sulla creazione REST vengono ignorati: la
firma del token determina il tenant. La versione CAS resta
invariata dopo le richieste respinte. Il token di sola lettura
può leggere il proprio sinistro, ma non aggiornarlo via REST/MCP.

**Limiti:** il test non garantisce che una configurazione OIDC
produttiva impedisca al chiamante di ottenere token per altri
tenant: su Keycloak demo i client password-grant pubblici
hanno claim hardcoded per semplificare il laboratorio.
Entra ID reale, ruoli e provisioning di utenti organizzativi,
test con MCP esterni e l'autorizzazione di nuove relazioni
Sinistri–Perizie restano da implementare e verificare.

