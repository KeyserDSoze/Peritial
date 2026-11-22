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

## Ricerca filtrata keyset su più pagine (#7/#12/#13, ADR 0040)

`GET /api/sinistri/search/page?prefix=...&occurredFrom=...&occurredTo=...
&limit=50&cursor=...` e `search_sinistri_page` eseguono la ricerca
literal-prefix con intervallo evento inclusivo su più pagine, senza OFFSET.
La query di `PgSinistroRepository.SearchPageAsync` applica insieme
FORCE RLS, tenant SQL, `ILIKE` escaped, date inclusive e
`(occurred_on DESC,id ASC)` prima di `LIMIT 51`.
La risposta `{items,nextCursor}` non rivela tenant ID, audit o dati interni.
Il cursore filtrato versione `s1` include soltanto data/UUID e
fingerprint SHA256 troncata del prefisso/data normalizzati; mismatch
con il filtro o encoding malformato è rifiutato. Non è una credenziale.
L'endpoint precedente `/api/sinistri/search` e l'MCP
`search_sinistri` mantengono il contratto bounded, per il picker.

La PWA continua a caricare soltanto su richiesta e mostra
«Carica altri sinistri» anche durante una ricerca. Refresh/reset
rimuovono il cursor precedente; un POST confermato durante il filtro
esegue un nuovo GET della prima pagina e recupera il nuovo
`nextCursor`. Nessuna cache/offline queue, autorizzazione o paginazione
delegata al browser. I test sono aggiunti prima del codice Business
e coprono PostgreSQL FORCE RLS, filtri `%_`, date, tie-break UUID,
isolamento di due tenant, page boundary, mismatched cursor,
Playwright Keycloak e MCP. Resta fuori scope il full-text,
indice performante ad alto volume, garanzia di snapshot sotto
UPDATE concorrenti, DELETE e relazioni assicurative.
[ADR 0040](../adr/0040-sinistri-filtered-keyset-search.md).

## V005 — cancellazione auditata e versionata di Sinistri senza Perizie collegate (#7/#6/#12/#21, ADR 0041)

La migrazione forward-only `V005__sinistri_audited_delete.sql` concede
solo `DELETE ON sinistri` a `peritial_app`; **non cancella dati** e
non modifica la relazione V004. La FK composta same-tenant
`fk_perizie_sinistro_same_tenant` con `ON DELETE RESTRICT` impedisce
sempre di eliminare Sinistri collegati a qualsiasi Perizia,
anche se la relazione viene creata durante una cancellazione concorrente.
Il ruolo di recovery non ottiene accesso.

Il Business `SinistroService.DeleteAsync(id, expectedVersion)` richiede
lo scope firmato `peritial.write`. `expectedVersion` (intero positivo)
è obbligatorio: senza versione oppure con versione obsoleta il servizio
rifiuta la cancellazione. Il repository `PgSinistroRepository.DeleteAuditedAsync`
usa un solo `DELETE WHERE tenant_id=@tenant AND id=@id AND version=@version`,
`TenantDbScope` e FORCE RLS; l'evento append-only
`sinistro.delete / succeeded` è scritto **nella stessa transazione**.
Un errore audit comporta rollback del DELETE. Se la FK V004 blocca
l'operazione il client riceve 409; versione vecchia 409, ID
assente/foreign 404, input non valido 400, anonimo 401, scope read-only
403 (con audit del diniego). Non c'è cascata o cancellazione
silenziosa di Perizie, documenti o audit.

Contratti equivalenti:
- REST `DELETE /api/sinistri/{id}?expectedVersion=N` -> HTTP 204 solo
  dopo commit; nessuna risposta con record o tenant ID.
- MCP `delete_sinistro(id, expectedVersion)` -> `true` dopo commit.
- La PWA **non** offre ancora il tasto di eliminazione: prima vanno
  concordati retention, legal hold, autorizzazioni operative rafforzate
  e conferma accessibile dell'azione irreversibile. Non promuovere la
  cancellazione ad uso legale/produttivo senza questi gate.

Test-first: Business scope/tenant/CAS e audit; PG 17 reale con
utente `peritial_app`, perizia collegata e FK RESTRICT, record foreign,
stale version, rollback audit e cancellazione riuscita; Playwright
OAuth Keycloak REST/MCP desktop/mobile con 204/400/401/404/409.
CI effimera applica V003, V004 e V005 **solo** nel database
temporaneo, senza falsificare il journal dello storico Compose V001.
Il migrator amministrativo applica V005 con account DDL su
database versionati e backup verificato.
[Decisione e limiti ADR 0041](../adr/0041-sinistri-v005-audited-cas-delete.md).

## Sinistri: hard-delete disabilitata per default (#22, ADR 0042)

La mutazione V005 di ADR 0041 è protetta da due gate indipendenti
nel **Business** e vale per REST e MCP. Lo scope firmato
`peritial.write` non è più sufficiente: occorre anche il nuovo
`peritial.sinistri.delete`. Questo scope non compare tra i
`defaultClientScopes` della PWA né dei client `peritial-test-write`.
Il realm demo fornisce un *solo* client di test esplicito
`peritial-test-delete` con password grant per test CI effimeri;
**non è un modello di client produttivo**.

Il gate `SinistroDeletionGate` è **spento per default**, anche in
Development. Può essere acceso soltanto per test effimeri mediante
`Sinistri__EnableDevelopmentDelete=true` mentre
`ASPNETCORE_ENVIRONMENT=Development`. In altri ambienti,
`true` fa fallire l'avvio; non esiste una configurazione per
abilitare un hard-delete di produzione prima che policy retention,
legal hold, autorizzazioni più restrittive e approvazioni siano
codificate. Inoltre il Business non può cancellare senza il gate,
anche se uno strato REST/MCP viene modificato.

Un rifiuto di scope o gate viene auditato come
`sinistro.delete/denied` **senza resource ID**, evitando
qualunque lookup del record o rivelazione cross-tenant. Dopo
il gate positivo restano il CAS `expectedVersion`, il controllo
tenant FORCE RLS, la FK V004 RESTRICT e audit atomico V005.
Questa tranche **non soddisfa ancora** i requisiti di conservazione
di #22, non abilita il pulsante UI e non attesta cancellazioni legali.

Test RED→GREEN in `SinistroServiceTests`, E2E PKCE Keycloak
web normale negato, client esplicito consentito soltanto su CI
Development, HTTP 403/204 e MCP denial/success.
[ADR 0042](../adr/0042-sinistri-delete-development-gate.md).

## Scollegamento esplicito e controllato Perizia–Sinistro (#7/#9, ADR 0043)

Evoluzione additiva del link V004: `DELETE /api/perizie/{id}/sinistro?expectedSinistroId=UUID`
e MCP `unlink_perizia_sinistro(periziaId,expectedSinistroId)`
accettano soltanto il Sinistro letto dal client come expected value.
Non è disponibile nessun reparent automatico.
Con `peritial.write` e tenant JWT verificati, il repository applica
un **unico UPDATE CAS** su `perizie(tenant_id,id,sinistro_id)`
con FORCE RLS. Nel medesimo commit aggiunge audit
`perizia.unlink-sinistro/succeeded`, privo di body e tenant ID DTO.
Se l'audit fallisce, l'UPDATE viene rollbackato; se il link è
cambiato o già nullo, 409 senza falso audit di successo; se la
Perizia è foreign/missing, 404 equivalente. Il diniego di scope
appende audit `denied` senza resource UUID.

La PWA espone lo scollegamento **soltanto** dopo aver letto la relazione
firmata: doppia conferma con testo esplicativo, richiesta unica senza
retry automatico, GET fresh dopo HTTP 204, pulsante separato per
annullare la conferma. Un 409 invalida il link mostrato e richiede
«Ricarica collegamento» prima di ogni altro invio; rete offline o
token scaduto chiudono la vista tenant. I label sono it/en e usabili
via tastiera. Per ricollegare la Perizia si usa nuovamente la
ricerca server-side e `POST` link standard con CAS:
non c'è un'operazione atomica di sostituzione fra due Sinistri.
Nessuna migrazione V006, modifica dei documenti, cancellazione
della Perizia o del Sinistro. Il gate produttivo di hard-delete
ADR 0042 / #22 resta **bloccato** e indipendente.

Test RED→GREEN Business/PG17 least-privileged tenant, conflitti,
link stranieri e audit rollback; Playwright REST/MCP con JWT Keycloak,
PWA conferma/annulla/409/reload/204/GET e offline.
[ADR 0043](../adr/0043-perizia-sinistro-explicit-unlink.md).

## Riassegnazione atomica della relazione Perizia–Sinistro (#7/#9, ADR 0044)

La riassegnazione esplicita, distinta dal link/unlink V004, usa
`PUT /api/perizie/{id}/sinistro` con JSON
`{expectedSinistroId,newSinistroId}` e tool MCP
`reparent_perizia_sinistro(periziaId,expectedSinistroId,newSinistroId)`.
Richiede `peritial.write` firmato, due UUID Sinistro non vuoti e
distinti, target esistente nello stesso tenant JWT. La porta PG
`ReparentAuditedAsync` esegue **un solo UPDATE**
`SET sinistro_id=@target WHERE tenant_id=@tenant AND id=@perizia
AND sinistro_id=@expected AND status NOT IN (Billed,Duplicated,Cancelled)`
sotto FORCE RLS, con i parametri dei tre stati presi dall'enum
`PeriziaStatus`. Questa guardia SQL rende impossibile una
riassegnazione anche se lo stato diventa terminale durante una
richiesta concorrente. Il link non attraversa `NULL`, la Perizia
non viene duplicata e nessun Sinistro viene cancellato.

Mismatch di expected/source, Perizia già scollegata o stato terminale
→ 409, con nessun audit di successo; Perizia o Sinistro target
assenti/foreign → 404 indistinguibile; auth 401/403, validazione 400.
L'UPDATE e `perizia.reparent-sinistro/succeeded` sono committati
**nella stessa transazione** o rollbackati insieme.
REST ritorna 200 e `{periziaId,sinistroId:newSinistroId}`;
MCP ritorna lo stesso DTO, sempre senza tenant ID.

La PWA offre l'azione `Cambia sinistro collegato` separata da
`Scollega sinistro`: ricerca server-side del target, esclusione
del Sinistro corrente dalla selezione, anteprima e conferma
esplicita, richiesta unica e GET firmato dopo il commit.
Un 409 invalida l'associazione locale e impone reload; offline
o token non valido impediscono ogni replay. Sono supportati it/en.

L'expected UUID protegge dal cambio di relazione, **non** è un
token di versione univoco: un ciclo A→B→A non sarebbe distinto dal
primo A (possibile problema ABA). Inoltre l'audit attuale conserva
perizia UUID, azione, tenant/soggetto e timestamp, ma non lo storico
completo source/target. Per ricostruzione probatoria e approval
di riassegnazioni terminali occorre una decisione separata.
Restano aperti i requisiti di accessibilità WCAG e gli altri
collegamenti/filtri di #7/#9.
[ADR 0044](../adr/0044-perizia-sinistro-atomic-reparent.md).

## V006 — revisione monotona e audit storico delle associazioni (#23)

Perizia–Sinistro usa ora `sinistro_link_version` **bigint >=1**
per distinguere un link osservato da un altro che ha lo stesso UUID
dopo un ciclo `A→B→A`. Tutti i mutatori REST e MCP richiedono
`expectedLinkVersion` (positivo, letto da
`GET /api/perizie/{id}/sinistro`) oltre agli UUID.
La condizione SQL su `tenant_id,id,sinistro_id,version`
è nello stesso `UPDATE ... RETURNING sinistro_link_version` che
incrementa la versione. Per il primo link si richiede
`sinistro_id IS NULL AND sinistro_link_version=@expected`.
La modifica include un audit `succeeded` e un evento storico
`perizia_sinistro_history` con `revision`,
`previous_sinistro_id`, `new_sinistro_id`, `audit_id`
tutti nello stesso commit. Una collisione audit/DB fa rollback
di **tutte e tre** le scritture; 409 non registra un successo.
Il ruolo applicativo dispone solo di INSERT sulla storia
con FORCE RLS; niente lettura, update o delete nemmeno per
il ruolo recovery. Niente PII/JSON body/token, solo UUID correlati
all'audit già firmato.

DTO `{periziaId,sinistroId,linkVersion}`, senza tenantId.
POST `{sinistroId,expectedLinkVersion}` → 201 con revisione
aggiornata; PUT `{expectedSinistroId,newSinistroId,expectedLinkVersion}`
→ 200 con revisione aggiornata; DELETE query
`expectedSinistroId=...&expectedLinkVersion=...` → 204,
poi GET rivela la nuova revisione. I tool MCP accettano gli stessi
argomenti. Omettere la versione ⇒ errore 400/invalid arguments,
non operazione legacy. Il PWA picker conserva la revisione del
GET e la usa per l'unica mutazione esplicita; dopo 409 deve
ricaricare, mai usare una revisione recuperata automaticamente
per ripetere una scrittura.

La V006 usa baseline `1` anche per associazioni già collegate
sotto V004–V005: **non inventa eventi pregressi**.
Le approvazioni per cambiamenti sensibili e i requisiti
di retention/accesso per gli audit storici richiedono
ancora revisione di dominio/compliance, quindi #23 resta aperta.
Non modifica il gate di hard-delete produttivo #22.
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

## Contesa del mutex dell'import XML interno V012 (ADR 0082, #24)

La V012 fa acquisire a ogni `INSERT` e `UPDATE` su
`sinistri` il mutex PostgreSQL
`hashtextextended(tenant_id || newline || external_reference,0)`.
Lo store XML **interno e non registrato in DI**
acquisisce esplicitamente lo stesso lock prima di
prenotare `legacy_xml_import_keys`. Né il trigger
SQL né le UNIQUE legacy vengono modificati.

Per impedire che l'import rimanga sospeso senza
limiti dietro una transazione bloccata, lo store
pone `lock_timeout` a **massimo 1500 ms** soltanto
mentre acquisisce il mutex. Se il server ha un
timeout più restrittivo, lo mantiene. Dopo
acquisizione ripristina il precedente valore
transazionale prima di qualsiasi altra query;
il rollback della transazione pulisce in modo
automatico il setting anche se la lock acquisition
fallisce. SQLSTATE `55P03` durante questa sola
operazione diventa `LegacyXmlImportBusyException`,
un errore temporaneo **diverso** da
`LegacyXmlReferenceConflictException` per
riferimento realmente già presente. Nessun writer
non-import è soggetto al timeout aggiuntivo.

Il test PostgreSQL reale usa due pool distinti
app-role FORCE RLS: la prima transazione tiene il
mutex sulla coppia tenant/riferimento; l'import
nella seconda fallisce con `Busy` senza chiave,
Sinistro o audit di successo. Un import su altro
tenant con riferimento identico continua senza
bloccare; dopo il rilascio del mutex, la stessa
chiave originariamente rifiutata crea un solo
Sinistro e i replay successivi non duplicano.
Il test ha una propria deadline di 12 secondi
per rilevare anche regressioni che eliminassero
il timeout del server.

Il percorso di commit XML **rimane disabilitato
pubblicamente**, senza REST, MCP, UI o schedulazioni.
La gestione HTTP di un eventuale errore busy,
la politica di retry autorizzato, la prova di
staging, la caratterizzazione dell'identità
storica, la contention da bulk import e i campi
temporali non sono stati completati: issue
#24, #25 e #10 restano aperte.
