# Sinistri — dominio, PostgreSQL e primi use case REST/MCP (#7)

Il porting dei sinistri inizia con `src/backend/Domains/Sinistro.cs`
e `tests/backend/Peritial.Domains.Tests/SinistroTests.cs`.
Il dominio e la migrazione V003 sono seguiti dal primo slice REST/MCP
**list/search/read/create/update**. Mancano ancora DELETE, reparent/unlink della relazione, import e UI.

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
- Ricerca avanzata (full-text/fuzzy, filtri per assicurazione/polizza, paginazione cursor),
  indice di prefisso dimensionato e benchmark su volumi realistici; il primo
  autocomplete del riferimento esterno con date è ora disponibile via REST/MCP.
- Collegamento a compagnie, polizze e anagrafiche (#8), reparent/unlink
  Perizie–Sinistri con audit dedicato e UI;
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
La suite delle migrazioni copre V001–V004 anche in caso di adozione
esplicita legacy e backup/restore. Il bootstrap demo `001_init.sql`
**non include V003**: chi vuole esercitare la tabella deve lanciare
esplicitamente il migrator DDL in un ambiente appropriato prima di
usare il repository, seguendo `docs/database-migrations.md`.
REST e MCP riusano ora il servizio Business autorizzato che accede a questo repository.

**Restano fuori scope:** relazioni anagrafiche/polizze, reparent/unlink perizie, delete, import/merge,
ricerca full-text/fuzzy, cursor e UI; i test E2E cross-tenant con due identità
sono già presenti. I campi temporali di data-only richiedono
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
| `GET /api/sinistri/search?prefix=...&occurredFrom=...&occurredTo=...&limit=10` | `search_sinistri(prefix,...)` | read o write |
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
installazione V003. La CI effimera applica soltanto le fixture additive
`V003__sinistri_rls.sql` e `V004__perizie_sinistro_link.sql` prima dell'E2E,
senza alterare bootstrap o registrare un finto journal
V001–V004. Per ambienti reali migrare offline con
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
L'autorizzazione delle nuove relazioni Sinistri–Perizie è verificata
ora via REST/MCP su Keycloak locale; Entra ID produttivo resta da verificare.



## Ricerca e autocomplete per riferimento esterno (#7/#12)

`GET /api/sinistri/search?prefix=AB&occurredFrom=2026-01-01&occurredTo=2026-12-31&limit=10`
e il tool `search_sinistri(prefix,occurredFrom?,occurredTo?,limit=10)`
invocano **lo stesso metodo** `SinistroService.SearchAsync`
protetto da `peritial.read` o `peritial.write`.
Le due date ISO sono facoltative, inclusive, e si riferiscono a
`occurredOn` (non alla denuncia). Il prefisso è obbligatorio:
normalizzato con trim, **2–100 caratteri**, confronto
**case-insensitive letterale**; `%`, `_` e `!` non
sono wildcard interpretabili dal client.
Risultati ordinati per `occurredOn DESC,id`, default
10, cap 1–50; input invalido genera REST **400**
o errore del tool MCP.

Il repository applica `tenant_id=@tenant`, `ILIKE` con escape
e filtri data **in PostgreSQL prima del LIMIT**, in una transazione
`TenantDbScope` sotto FORCE RLS. Non filtra localmente
una lista limitata a 50/100 record; il DTO è sempre quello
senza tenant ID di `SinistroDto`. Nessuna lettura genera
audit di mutazione riuscita, né viene memorizzata nella
cache browser (`Cache-Control: no-store`).

**Test:** `SinistroServiceTests` verifica regole di input,
scope, date, limite e risultato; `SinistroPersistenceTests`
usa PostgreSQL reale con due tenant, record più recenti
non corrispondenti e prefissi contenenti `%`, `_`, `!`
per verificare escape e filtro prima del LIMIT.
`security.spec.ts` usa due utenti Keycloak reali,
chiamate REST/MCP, date, cap, 401/403/400 e dati senza
riferimenti di altro tenant, sia desktop che mobile.

**Debito esplicito:** non sono implementati ricerca
fuzzy/full-text o per dati collegati, cursor/paginazione
completa, autocomplete PWA e benchmark con grandi dataset.
L'indice esistente copre tenant e data; `ILIKE` su
riferimento può richiedere un nuovo indice funzionale o
trigramma in una futura migrazione **V005** dopo
`EXPLAIN ANALYZE` su dati rappresentativi.
Non modificare V003 storica. [ADR 0033](../adr/0033-sinistri-prefix-autocomplete.md).

## Collegamento Perizie–Sinistri con FK V004 (#7/#9/#12/#21)

La migrazione **V004** aggiunge `perizie.sinistro_id uuid NULL` e
la chiave esterna composta `(tenant_id, sinistro_id)`
verso `sinistri(tenant_id, id)`: il database **impedisce
fisicamente** di associare una perizia a un sinistro di un
altro tenant, anche con una query SQL elevata.
La colonna nullable preserva tutte le perizie già inserite;
un sinistro può avere più perizie, una perizia al massimo
un sinistro. Una FK con `ON DELETE/UPDATE RESTRICT`
impedisce la rimozione accidentale del genitore collegato.

`POST /api/perizie/{id}/sinistro`, body
`{"sinistroId":"UUID"}`, e il tool
`link_perizia_sinistro(periziaId,sinistroId)`
richiedono `peritial.write` e usano
`PeriziaSinistroLinkService` con il tenant firmato.
Il repository verifica entrambi gli ID nello stesso
tenant prima di aggiornare. La singola transazione PG
esegue `UPDATE ... WHERE sinistro_id IS NULL`
(CAS unidirezionale) e append dell'audit
`perizia.link-sinistro / succeeded`.
Un tentativo di nuovo link produce **409 REST**,
anche se si ripete l'ID già associato; foreign/missing
producono **404**. Qualsiasi errore audit causa
**rollback del link**. Nessuna riassegnazione o
scollegamento impliciti.

`GET /api/sinistri/{id}/perizie` e il tool
`list_perizie_for_sinistro(sinistroId)` accettano
`peritial.read` oppure `peritial.write` e
restituiscono un array di **UUID delle perizie**,
non una vista aggregata di dati personali.
Sinistro inesistente o di altro tenant produce
404 REST/errore MCP indistinguibile.
Tutti i DTO escludono `tenantId`, subject
e metadati audit.

I test xUnit con PostgreSQL reale coprono FK composta
cross-tenant, CAS/replay, tenant RLS, rollback audit
duplicato e salvataggio corretto. I test Business
coprono autorizzazioni, denial audit e DTO allowlist.
La suite Playwright con due JWT Keycloak distinti
verifica link A via REST e B via MCP, list
REST/MCP, 404, 409, 401 e 403.
Il restore PG su server indipendente verifica anche
il `sinistro_id` collegato dopo dump/ripristino.

**Non ancora implementato:** spostamento/scollegamento,
CRUD completo dei Sinistri nella PWA, validazioni su
polizze/anagrafiche, import Classic/Accertatore e
autorizzazioni Entra ID di produzione.
Il bootstrap demo V001 resta non journaled:
solo la CI effimera applica V003 **poi** V004;
i database reali usano un migrator DDL separato con
backup e journal V001–V004.
Decisione: [ADR 0034](../adr/0034-perizie-sinistri-v004-tenant-fk.md).


## Primo selettore Sinistri nella PWA (#7/#13)

La PWA `App.tsx` ora incorpora `PeriziaSinistroPicker`
**a richiesta**, sotto ciascuna perizia autenticata. Il
pannello parte chiuso e non effettua N+1 letture quando
viene caricata la lista delle perizie. Aprendolo, esegue
`GET /api/perizie/{id}/sinistro` con JWT corrente: il
nuovo DTO `{periziaId,sinistroId:UUID|null}` distingue
una perizia scollegata (200, `null`) da una non
visibile (404), senza dati del tenant. Se collegata,
legge `GET /api/sinistri/{id}` per mostrare il
riferimento effettivamente salvato; non propone
altre scritture.

Per perizie libere, l'operatore può cercare **su
richiesta** un riferimento esterno (almeno due
caratteri) tramite
`GET /api/sinistri/search?prefix=...&limit=20`
e scegliere un risultato con riferimento e data evento.
Il browser non filtra soltanto gli elementi già caricati
e non interpreta il prefisso come SQL pattern. Il
`POST /api/perizie/{id}/sinistro` viene inviato
**una sola volta dopo selezione**: solo il 201
confermato aggiorna la vista. Lo scope OAuth
`peritial.write` è usato per nascondere
i controlli di modifica ai lettori; **il server**
resta l'autorità finale su scope, tenant, audit
e CAS V004.

Se il POST incontra 409, la PWA elimina i candidati
obsoleti e mostra «Ricarica collegamento»: soltanto
un successivo GET firmato può rendere visibile la
relazione corrente. Con token non valido/insufficiente
o API non disponibile, la vista protetta viene
svuotata; nessuna mutazione entra in una coda
offline o viene ritentata automaticamente.
Le risposte in ritardo vengono ignorate dopo
chiusura pannello, cambio ricerca, cambio JWT
o unmount; il PWA service worker non conserva
JSON API/MCP. Il testo è localizzato **it/en**.

**Test**: `PeriziaSinistroLinkServiceTests` e
`SinistroPersistenceTests` controllano il read model
linked/unlinked, 404 cross-tenant e nessun audit
sulle letture. `authenticated.spec.ts` su browser
desktop/mobile e Keycloak reale verifica creazione
perizia, ricerca Sinistro, link 201, refresh,
conflitto 409 da un secondo client, reload
e comportamento offline senza dati residui.

**Residui:** questo pannello non crea/modifica sinistri
dalla PWA, non offre ricerca fuzzy, debounce,
paginazione oltre i 20 suggerimenti, riassegnazione,
scollegamento, import legacy, collegamenti polizze/
anagrafiche o certificazione WCAG/Entra ID.
V004 resta una migrazione amministrativa esplicita.
Vedere [ADR 0035](../adr/0035-pwa-sinistro-link-authoritative-read.md).


## Catalogo e creazione Sinistri nella PWA (#7/#13)

Il nuovo componente `SinistroCatalog` è disponibile nella
schermata autenticata delle Perizie, **chiuso per default**:
soltanto il pulsante «Apri catalogo sinistri» avvia
`GET /api/sinistri?limit=50`, già tenant-scoped
da PostgreSQL FORCE RLS. In questo modo il bootstrap
Compose demo V001 (senza migrazioni V003/V004) non
blocca la PWA principale con un fetch automatico
del catalogo non ancora migrato.

Il catalogo mostra riferimento, data evento e data
denuncia opzionale, ordinati per `occurredOn DESC,id`
dalla query SQL. L'utente con scope
`peritial.write` può compilare un form con
`externalReference` di 1–100 caratteri,
`occurredOn` obbligatoria e `reportedOn`
opzionale e inviare **una sola** richiesta
`POST /api/sinistri`. Il browser non trasmette
un tenant ID; il server verifica JWT/scope
e rende atomici creazione e audit. Soltanto
un **201 confermato** aggiorna i risultati.
Il record confermato mantiene l'ordinamento
SQL per data evento; se più vecchio dei
50 mostrati, rimane comunque visibile nella
ricevuta di creazione. Un 400 non produce
record locale né finge una scrittura riuscita.

Su rete/REST non disponibile o 401/403/503
la UI invalida i dati protetti e sospende
le operazioni. Offline il catalogo e i
controlli di creazione spariscono completamente;
non sono previste code di POST, replay
automatici, cache di risposte API o
associazioni tra token diversi. Dopo refresh
o riconnessione, la lista proviene da
nuova richiesta firmata al server.
Etichette, campi e messaggi sono disponibili
in italiano e inglese.

**Test:** `authenticated.spec.ts` verifica
login reale PKCE, apertura on-demand,
fallimento POST 400 senza riga locale,
creazione 201 con data e riferimento,
allowlist senza `tenantId`,
`Cache-Control: no-store`, persistenza
REST/refresh e fail-closed offline
su desktop e mobile. I test PG/Business
già verificano l'isolamento tenant e
l'audit atomico del POST.

**Fuori scope in questo slice di creazione:** modifica UI dei Sinistri
con CAS (aggiunta successivamente, vedi ADR 0037), eliminazione, gestione paginata oltre
i primi 50, filtri avanzati e relazione
con polizze/anagrafiche. V003/V004 non vengono
applicate automaticamente: usare il migrator
amministrativo esplicito prima del rilascio.
[ADR 0036](../adr/0036-pwa-sinistri-catalog-create.md).


## Modifica Sinistri nella PWA con CAS e reload firmato (#7/#13)

Il catalogo `SinistroCatalog` offre ora `SinistroEditor`
**per ogni riga** soltanto quando lo scope OIDC
include `peritial.write` (il vero controllo rimane
server-side). L'editor parte chiuso e non si affida
alla versione eventualmente superata del catalogo.
Quando viene aperto legge
`GET /api/sinistri/{id}` con il token corrente,
poi mostra riferimento, data evento, data denuncia
opzionale e **versione persistita**.

Il pulsante «Salva modifiche sinistro» invia
`PUT /api/sinistri/{id}` con
`{externalReference,occurredOn,reportedOn,expectedVersion}`
e con **expectedVersion uguale all'ultima
versione confermata dal GET/PUT**. Nessun tenant ID
o dato audit deriva dal browser. Soltanto una
**risposta 200 realmente riuscita** aggiorna
i valori e la versione nella PWA; l'ordinamento
delle 50 voci resta `occurredOn DESC,id`.
Questo usa l'UPDATE compare-and-swap PostgreSQL
e il log di audit atomico `sinistro.update`
già implementati nel Business e in V003.

Su **400** il form conserva i campi da correggere,
ma il record in elenco e la versione rimangono
immutati. Su **409 CAS**, l'editor elimina
immediatamente la snapshot vecchia e i
campi modificabili, maschera anche i valori
non più attendibili nella riga dell'elenco,
disabilita il salvataggio e mostra
«Ricarica sinistro». Solo dopo un nuovo
GET autenticato vengono mostrate la
versione vincente e le informazioni
correnti: non esiste retry automatico né
un override silenzioso dei dati del collega.

Un 404 su lettura/scrittura (perizia non più
visibile, sinistro cancellato o tenant
cambiato) invalida la vista protetta,
così come 401/403/5xx e errori di rete.
Offline non sono accodati POST o PUT,
nessuna risposta API viene memorizzata
in CacheStorage e le risposte asincrone
stale vengono invalidate alla chiusura
del form, cambio JWT o unmount.
Form e messaggi sono tradotti in italiano
e inglese.

**Playwright con PKCE reale** verifica:
GET v1, PUT 400 simulato senza modifica,
PUT v1→v2 con expectedVersion=1, GET
persistito, PUT concorrente v2→v3 da
altro client e risposta 409 al salvataggio
con snapshot v2, occultamento dati stale,
reload GET v3, PUT v3→v4 e refresh
con versione persistita. Gli unit test
Business e PostgreSQL già verificano
CAS e audit atomico sotto tenant RLS.

Restano aperti eliminazione Sinistri,
ricerca avanzata, paginazione/cursor,
integrazione polizze/anagrafiche,
import legacy e parità completa del
workflow della PWA. V003/V004 non vengono
applicate automaticamente su database reali.
[ADR 0037](../adr/0037-pwa-sinistri-cas-editor.md).

## Ricerca del catalogo Sinistri PWA (#7/#13, ADR 0038)

Oltre alla lista iniziale `GET /api/sinistri?limit=50`, il catalogo
include la **ricerca server-side** esplicita `GET /api/sinistri/search`
con `prefix` letterale 2–100 caratteri, `occurredFrom` e `occurredTo`
facoltativi (inclusivi), e `limit=50`. La PWA non filtra il batch
precedente dei recent-50: anche i casi storici sono ricercabili fuori
da quella finestra. Le date invertite impediscono il submit; i simboli
`%` e `_` sono codificati nell'URL e interpretati come testo dal
repository (non wildcard SQL). Il picker Perizie–Sinistri continua a
chiedere 20 suggerimenti.

Il pulsante di ricerca sostituisce immediatamente la lista precedente
con una GET firmata e mostra il filtro applicato. «Aggiorna sinistri»
ripete **la stessa ricerca**; «Cancella filtri sinistri» rilegge gli
ultimi 50 dal server. Nuove richieste invalidano le risposte in ritardo;
offline, cambio token, 401/403/503 e rete indisponibile non lasciano
pratiche visibili né code di scrittura. I dati tenant non sono salvati
in cache. Un POST 201 durante una ricerca attiva ricarica i match da
PostgreSQL e non inserisce il nuovo record a prescindere dal filtro.
Le letture/PUT confermate di righe già visualizzate aggiornano soltanto
quella finestra (la lista completa richiede «Aggiorna sinistri»).
L'editor CAS e la richiesta di reload su 409 non cambiano.

Il nuovo test Playwright PKCE desktop/mobile nel file
`tests/frontend/authenticated.spec.ts` crea tre sinistri con prefissi
contenenti `%_`, date differenti e decoy; verifica query REST
parametrica, date, cap 50, escaping, refresh, reset e offline.
I limiti restano **massimo 50 per ricerca**, nessun cursor/full-text/fuzzy,
ottimizzazione dell'indice a scala, filtri anagrafiche/polizze, DELETE,
Entra reale o audit WCAG completo. V003/V004 richiedono sempre
migrazioni esplicite. [ADR 0038](../adr/0038-pwa-sinistri-server-search.md).

## Paginazione keyset catalogo Sinistri (#7/#12/#13, ADR 0039)

Il catalogo completo dispone ora di `GET /api/sinistri/page?limit=50&cursor=...`
e del tool MCP read-only `page_sinistri(cursor?,limit=50)`, entrambi delegati
a `SinistroService.PageAsync` con scope `peritial.read|write`.
La risposta REST è `{items:[...], nextCursor:string|null}` e non contiene
`tenantId`, chiavi interne né informazioni di audit. Il cursore è base64url
canonico, solo posizione `yyyy-MM-dd|UUID`, massimo 128 caratteri, non un
token autorizzativo. Limite pagina 1–50: la query PostgreSQL sotto FORCE RLS
usa `limit+1`, ordinamento `occurred_on DESC,id ASC` e seek su data/ID,
sfruttando l'indice V003 esistente (nessuna nuova migrazione). Cursori malformati
e limiti illegali producono 400; sessioni anonime 401 e scope insufficienti 403.

La PWA continua a caricare il catalogo **solo su richiesta** e ora usa la
prima pagina keyset, con «Carica altri sinistri» quando `nextCursor` non
è nullo. La seconda GET aggiunge righe senza duplicare gli UUID già visti.
La ricerca per prefisso/data resta separata con un massimo di 50 risultati
(ADR 0038), **non** è ancora paginata. Reset/filtro/refresh/chiusura/offline/
token rotation eliminano il cursore obsoleto. Dopo un POST confermato in
modalità catalogo, il cursore precedente non viene riutilizzato; il nuovo
record è visibile con ricevuta e l'utente può aggiornare la lista per
riprendere la paginazione. Errori auth/rete seguono la policy fail-closed.

La paginazione non è uno snapshot globale sotto aggiornamenti concorrenti:
inserimenti recenti non spostano il boundary né duplicano righe, ma
modifiche alla **data evento** possono riordinare record. Restano aperti
cursor per la ricerca filtrata, delete e autorizzazioni conseguenti, filtri
polizze/anagrafiche, full-text e benchmark sui grandi cataloghi.
V003/V004 sono sempre migrazioni amministrative manuali.
[Test e decisione ADR 0039](../adr/0039-sinistri-keyset-pagination.md).
