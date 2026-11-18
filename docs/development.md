# TDD e processo

Ogni use case: test rosso (xUnit / Playwright), implementazione minima, test verde, refactoring, aggiornamento docs/ e adr/.

- Backend completo: docker compose -f docker/compose.yml --profile test run --rm tests
- Frontend: cd src/frontend && npm ci && npx playwright install chromium && npm run build && npm run test:e2e
- CI: xUnit unitari e con PostgreSQL reale, build frontend, Playwright, smoke Docker Compose e job isolato `database-recovery` (backup/restore su due server PG17 più adozione V001 legacy con backup reale, consenso e drift check).
- Lavorare solo su main. Non fondere death.
- Debiti bloccanti produzione: pin digest delle immagini e supply-chain, quarantena persistente e signature freshness dello scanner, limitazione chiamate, audit completo, idempotenza, backup/restore integrati con object storage verificati e upgrade di altre varianti legacy non riconosciute, provisioning e rotazione dei privilegi RLS in produzione, test OAuth/MCP su client reali, deploy automatizzato su Azure, secret vault.

La build iniziale CI ha guidato la correzione delle dipendenze e la scelta dell'immagine S3 open-source disponibile sul registry.

La CI Docker Compose esegue ora un test Playwright autenticato reale (Keycloak, creazione perizia, tool MCP) sia desktop sia mobile, e fallisce su errori OAuth o sulle chiamate non autorizzate. Dopo il ciclo Red→Green si eliminano stampe temporanee di pagine e credenziali dai test (Refactor).

## Verifica OAuth/MCP

`tests/frontend/security.spec.ts` effettua test negativi reali contro il realm Keycloak demo in Docker Compose: discovery RFC 9728, assenza `tenant_id`, audience errata, scope insufficiente, `401` vs `403`, tool MCP non autorizzati. I client Keycloak `peritial-test-*` con password grant servono **esclusivamente** per i test isolati locali, non per i client reali. La PWA continua con Authorization Code + PKCE. Vedere ADR 0007.

## Regressione Sinistri: due tenant OAuth realmente firmati (#7/#4/#12)

La suite `tests/frontend/security.spec.ts` usa ora **due identità
soggetto distinte** sul Keycloak demo: `localuser` con client
`peritial-test-write` (claim tenant firmato
`11111111-1111-1111-1111-111111111111`) e `otheruser` con
`peritial-test-write-tenant-b` (`22222222-2222-2222-2222-222222222222`).
Entrambi i client usano il solo **password grant per CI locale**;
nessun client PWA/PKCE o codice API accetta il tenant da una
header/body/parametro fornito dal chiamante.

Il test E2E su PostgreSQL reale e V003 verifica:
- Creazione sinistro A via REST con un `tenantId` ostile nel JSON e
  `X-Tenant-Id` falsificato; il record resta nel tenant firmato A.
- Creazione sinistro B via MCP, verifica che entrambi i soggetti JWT
  e i `tenant_id` firmati siano **diversi**, anche nelle risposte
  lettura/lista REST/MCP.
- `GET` cross-tenant A→B e B→A in REST produce **404 identico**
  al GUID inesistente, e PUT cross-tenant produce 404 senza modifiche.
- `get_sinistro`/`update_sinistro` su ID estraneo in MCP
  restituiscono `isError:true`, senza riferimento privato;
  `list_sinistri` per ciascun tenant non enumera quello altrui.
- Il token read-only dello stesso tenant legge ma non aggiorna
  (REST 403; MCP `isError:true`). Le versioni dei due record
  restano 1 e gli errori non producono aggiornamenti riusciti.

La CI Compose applica **solo in un database effimero** la fixture
SQL V003 prima dei Playwright, senza alterare il bootstrap V001
non journaled o l'upgrade esplicito della CLI migrator.
Queste due identità sono **fittizie e non sicure per produzione**:
i client pubblici password grant hanno tenant hardcoded per test;
chiunque disponga delle credenziali demo potrebbe scegliere il
client. In produzione il tenant deve essere assegnato e firmato
dal provider OIDC conforme al modello di trust e provisioning
dell'organizzazione, non dal client, da JSON o da header.
La prova non certifica Entra ID, IAM effettivo di produzione,
client MCP esterni, rotazione credenziali, né la parità del legacy.

## Test audit

SecurityAuditUseCaseTests verifica tenant, subject, azione ed esito senza riferimenti o payload. SecurityAuditRepositoryTests usa PostgreSQL reale e il ruolo INSERT-only per verificare RLS. La CI Docker Compose verifica audit realmente persistito dopo REST e MCP. Le fixture DDL/ruoli PostgreSQL usano un advisory lock per impedire corse del catalogo tra test paralleli. Vedere docs/audit.md.

## Transazioni atomiche audit/metadata (#21)

PeriziaAtomicAuditTests prova su PostgreSQL reale che INSERT/UPDATE perizie e audit si committano insieme, che un audit duplicato annulla CREATE o UPDATE, e che gli eventi cross-tenant non modificano dati. DocumentAtomicAuditTests applica gli stessi controlli ai metadata dei file. Tutte le mutazioni REST/MCP usano i metodi AddAuditedAsync/UpdateAuditedAsync delle porte Business; il ruolo DB resta soggetto a FORCE RLS. La lettura degli oggetti esterni e la loro compensazione in caso di errore restano fuori dalla transazione PostgreSQL: ADR 0008 e issue #5/#21.

## Cancellazione perizie REST/MCP (#9)

La transizione terminale `Cancelled` già presente in Domain è esposta da
`PeriziaService.CancelAsync`, dall'endpoint
`POST /api/perizie/{id}/cancel` e da `cancel_perizia(id)` nel catalogo MCP.
Autorizzazione `peritial.write` e tenant dal JWT firmato, senza parametro
tenant client. I test `PeriziaTests` verificano il divieto cross-tenant e
gli stati terminali `Billed`, `Duplicated`, `Cancelled`.
`SecurityAuditUseCaseTests` controlla un unico evento di successo, con
tenant e subject senza riferimento privato; il retry sullo stesso aggregato
viene rifiutato.

`PeriziaAtomicAuditTests` usa PostgreSQL reale con ruolo app FORCE RLS:
cancellazione, audit nello stesso commit, rollback del cambio stato se
l'audit fallisce e rifiuto di un secondo evento per una pratica già
annullata. `tests/frontend/security.spec.ts` esercita REST e MCP con
token Keycloak `peritial-test-write` e prova il `403` con token read-only;
il job Compose verifica gli eventi persistiti dopo Playwright.

**Concorrenza ottimistica** (ADR 0029, #9/#21): ogni lettura di
`Perizia` conserva una snapshot di `PersistedStatus` e
`PersistedAssignedTo`. Tutti gli UPDATE dell'aggregato, inclusi
`assign`, `advance`, `cancel`, confrontano in modo atomico lo stato e
l'assegnatario attesi nella clausola `WHERE` di PostgreSQL; una
mutazione obsoleta viene rifiutata **prima** di scrivere l'audit di
successo. REST distingue record assente/di altro tenant (404) da
versione confliggente (409); MCP restituisce un errore del tool.
`ConfirmPersisted` avanza lo snapshot solo dopo `COMMIT` riuscito.

`PeriziaAtomicAuditTests` verifica inoltre **quattro interleaving
deterministici** con due snapshot PostgreSQL reali: assign dopo cancel,
advance dopo cancel, cancel dopo assign e due assign concorrenti.
I quattro test hanno fallito prima della correzione in
[CI RED #43879181694](https://github.com/KeyserDSoze/Peritial/actions/runs/43879181694).
Dopo l'implementazione e la correzione dell'asserzione xUnit sul
tipo eccezione esatto, i 145 test Infrastructure sono passati in
[CI GREEN #43880138783](https://github.com/KeyserDSoze/Peritial/actions/runs/43880138783).

**Limiti:** lo snapshot status+assignee non è un contatore di versione
monotono e non protegge da futuri cicli ABA dello stato; non esiste
ancora ETag/If-Match pubblico, idempotenza completa degli altri tool,
prova di race multi-replica o di commit-ACK incerto, UI di storico,
notifiche o parità con le regole del legacy. #9 e #21 restano aperte.

## Lista documenti in PWA — solo metadati, niente download (#11/#13)

La PWA in `App.tsx` ora mostra nel riquadro upload
gli **ultimi 50 documenti del tenant** (`GET /api/files?limit=50`)
con nome del file, tipo MIME, dimensione in byte e data.
I contenuti sono renderizzati come testo React: non vengono
inseriti link a storage, presigned URL, pulsanti download o
anteprime; le azioni rimangono limitate all'ingestion già
autorizzata. Il microcopy esplicita che sono **solo
metadati** e che il download non è ancora supportato.
Le etichette sono disponibili in italiano e inglese.

Il form di upload permette inoltre di scegliere **facoltativamente** una perizia
tra quelle restituite dalla GET autenticata per il tenant corrente; senza scelta,
il documento resta generico (comportamento precedente). La selezione passa il
solo `periziaId` a `uploadDocument`/`POST /api/files`, dove il Business layer
esegue i controlli di autorizzazione/tenant prima di salvare i metadati.
La lista espone il riferimento della perizia se incluso nella proiezione
corrente, altrimenti una dicitura generica e mai una chiave storage.
La selezione viene azzerata al cambio JWT, offline, in caso di API
indisponibile o dopo upload confermato, ed è scartata se il riferimento
non è più presente nella successiva lista autenticata. Non esistono
upload in coda né nuovi permessi client-side.

La consultazione metadata ora ha anche un filtro opzionale per perizia:
`GET /api/files?limit=50&periziaId=<uuid>` eseguito **sul server**
con il JWT corrente, non un filtro locale sui soli 50 elementi
già ottenuti. L'opzione "tutte" usa `GET /api/files?limit=50`;
i documenti non associati a una perizia sono visibili nell'elenco
globale. Il filtro cambia i dati soltanto dopo una nuova GET autenticata:
all'interazione azzera immediatamente i metadati visualizzati,
invalida le risposte in volo con l'epoch e disabilita i form fino
al completamento delle due letture tenant-scoped. Se una GET fallisce,
la PWA nasconde entrambi gli elenchi e richiede un retry; non conserva
un elenco precedente o una cache offline. Il filtro viene cancellato
al cambio identità/offline/failure o quando la perizia non compare più
nell'elenco autorizzato. Nessuna scelta UI equivale ad autorizzazione:
restano obbligatori scope, tenant e FORCE RLS lato API/DB.
Il cap 50 resta **per query**; paginazione dell'intero storico non è
ancora implementata. Test Playwright Keycloak/ClamAV/SeaweedFS esercita
due perizie e due upload reali, commutando filtro e lingua it/en.

`App.tsx` recupera `listPerizie` e `listDocuments`
insieme tramite `Promise.all` usando il JWT corrente
**prima** di rendere nuovamente interattivi i form. Se
una delle due GET fallisce, `loading/ready/unavailable`
blocca entrambe le sezioni: non si riutilizzano
record di un vecchio tenant, e offline non resta
una copia dei nomi dei file in memoria UI.
Dopo un `POST /api/files` confermato, la PWA
svuota la selezione e ricarica soltanto con
nuove GET autenticate: **non** ripete il POST
se la conferma è incerta. Le risposte precedenti
a un cambio token/epoch non possono riattivare
le azioni.

`tests/frontend/authenticated.spec.ts` usa
Keycloak, ClamAV e SeaweedFS reali: carica un PDF
ammesso tramite il form, richiede la visualizzazione
del solo nome/dimensione e l'assenza di link o
pulsanti di download, quindi prova il refresh
della lista. Mancano ancora la paginazione di
tutto lo storico, la ricerca, il download autorizzato,
cartelle e versioning documentale (issue #11)
e le altre sezioni PWA (issue #13).

## Catalogo documenti: metadata tenant-scoped senza download (#11/#12)

La nuova read model `DocumentSummaryDto` contiene solo
`id`, `periziaId`, `fileName`, `contentType`,
`length` e `createdAtUtc`: niente `tenantId`,
`storageKey`, dati antivirus, URL interni, né byte
dei documenti. La projection PostgreSQL in
`PgDocumentCatalogRepository` seleziona esplicitamente
questi campi, all'interno di `TenantDbScope` e FORCE RLS.
L'interfaccia `IDocumentCatalogRepository` è separata dal
repository mutativo di ingestion: nessuna append di audit
succeeded su una semplice query.

`DocumentCatalogService` richiede `peritial.read`
oppure `peritial.write`, prima di interrogare il repository.
Offre `ListAsync(periziaId?, limit=50)`, con limite hard cap
1..100, e `GetAsync(id)`. In REST:
`GET /api/files?periziaId=<uuid>&limit=50` e
`GET /api/files/{id}`; in MCP:
`list_documents(periziaId?,limit?)` e `get_document(id)`.
ID non visibili di tenant diversi e sconosciuti
non sono distinguibili. Un filtro `periziaId`
sconosciuto restituisce elenco vuoto; nessuna query
permette di specificare il tenant. Nessun download
consentito tramite questi endpoint; per il listing
completo con storico maggiore di 100 record
manca ancora paginazione keyset/cursor.

**Test TDD:**
- `DocumentCatalogTests` (Business): token read-only e writer,
  validazione limit, not found e filtro, assenza di proprietà
  storage/tenant dal DTO;
- `DocumentCatalogRepositoryTests` (Infrastructure PostgreSQL):
  verifica stessi UUID perizia in tenant diversi,
  tenant-scoped `FindAsync/ListAsync`, pool con
  `MaxPoolSize=1`, sessione tenant non persistente nel pool,
  ordine e cap di lettura, assenza di storage key serializzata;
- `tests/frontend/security.spec.ts` (Keycloak reale):
  upload PDF con JWT write, listing/dettaglio REST read-only,
  filtro per perizia, cap, 401/403/404 e
  `tools/list`/`tools/call` MCP read-only con stesso
  metadata, senza chiavi storage.

RED verificato in
[CI #43907670151](https://github.com/KeyserDSoze/Peritial/actions/runs/43907670151):
nuovi tipi Business inesistenti, e in
[CI #43908081684](https://github.com/KeyserDSoze/Peritial/actions/runs/43908081684):
manca il repository PostgreSQL. GREEN backend
[CI #43908293363](https://github.com/KeyserDSoze/Peritial/actions/runs/43908293363):
**Domain 5/5, Business 46/46, Infrastructure 148/148**.
L'integrazione REST/MCP full-stack va confermata tramite
il job Compose del commit che include il test Playwright.

## Lettura puntuale di una perizia via REST/MCP (#12/#9)

La route `GET /api/perizie/{id}` e il tool
`get_perizia(id)` invocano entrambi `PeriziaService.GetAsync`
con `RequireRead`. La query `FindAsync` del repository
PostgreSQL usa il contesto tenant firmato, con FORCE RLS,
senza parametri tenant dal client. Token con scope
`peritial.read` oppure `peritial.write` possono accedere;
un token senza scope viene rifiutato, anche quando il GUID
rappresenta una pratica esistente.

`SecurityAuditUseCaseTests` copre letture read-only e
write-only, assenza di audit mutativo, not found per
ID sconosciuto, `Guid.Empty` o altra tenant e diniego
per assenza scope. `TenantIsolationTests` prova già
`PgPeriziaRepository.FindAsync` con tenant diversi e
pool di connessioni PostgreSQL reale. Playwright
`tests/frontend/security.spec.ts` verifica lettura di
una pratica con Keycloak read-only via REST e MCP,
discovery `tools/list`, 401/403, 404 e mancata
divulgazione di record inesistenti.

I read non emettono `succeeded` su `security_audit`:
l'audit append-only registrato dai casi d'uso resta
dedicato alle mutazioni e ai dinieghi. Mancano
catalogo completo MCP, read models paginati,
compatibilità con client esterni e versionamento
contrattuale (issue #12 ancora aperta).

## Assegnazione e avanzamento MCP: parità REST (#12/#9)

`McpTools.AssignPerizia` (`assign_perizia(id, assignee)`) e
`McpTools.AdvancePerizia` (`advance_perizia(id, target)`) usano
gli **stessi** metodi `PeriziaService.AssignAsync` e
`PeriziaService.AdvanceAsync` delle route REST, senza parametro
tenant o bypass del controllo di scope `peritial.write`.
Il target MCP è un nome stringa esplicito, case-sensitive,
limitato ai cinque successori `ToBeRevised`, `Revised`,
`ToSend`, `Sent`, `Billed`; il servizio/dominio verifica
comunque la transizione. Anche gli errori per scope mancante o
transizione non valida passano per il protocollo MCP con
`isError:true`.

La prova `tests/frontend/security.spec.ts` richiede i due
tool in `tools/list`, crea una pratica reale con Keycloak,
verifica il rifiuto con token read-only, assegna tramite MCP,
avanza tramite MCP, ne rifiuta il replay e confronta il
risultato tramite REST; il job CI Compose richiede le righe
`perizia.assign` e `perizia.advance` (`succeeded` e
`denied`) su PostgreSQL e verifica l'assenza di doppi successi
per le pratiche di questo test. L'audit PostgreSQL REST/MCP
continua a essere atomico con le mutazioni.

**Non sono ancora coperte** tutte le operazioni legacy e gli
altri tool, l'idempotenza estesa, ETag, compatibilità con client
MCP esterni, upload firmato o UI/storico completi. Vedere
`docs/mcp.md` e issue #9/#12/#21.

## API non raggiungibile nonostante browser online (#13)

Il valore `navigator.onLine` indica soltanto la disponibilità
apparente della connettività. Un gateway può restituire HTTP 503,
Keycloak può negare il token oppure la `fetch` REST può fallire
anche quando `navigator.onLine === true`. In questi casi
`App.tsx` **non** lascia visibile l'ultima lista tenant-scoped
né permette la creazione, l'upload o le mutazioni perizia.

La UI autenticata distingue tre fasi per l'elenco:
`loading` (lettura in corso, nessuna mutazione),
`ready` (GET REST autenticata riuscita, operazioni consentite)
e `unavailable` (la GET è fallita o una mutazione ha incontrato
un errore di rete/autenticazione/server). In `unavailable` la
lista e gli snapshot di mutazione vengono svuotati, il form e
gli altri controlli non sono renderizzati e compare un avviso
accessibile `role=alert` in italiano/inglese. Solo un click su
`Riprova` esegue nuovamente `GET /api/perizie` con l'access
token attuale e, se riesce, riabilita le operazioni. Quando il
servizio funziona, `Aggiorna elenco` permette una revalidazione
manuale e nasconde i controlli durante la richiesta.

Il modulo `api.ts` identifica come guasto critico le
`TypeError` di rete e risposte `401`/`403`/`408`/`429`
o `5xx` durante le mutazioni. Gli errori del dominio
`400`, `409`, `422` vengono invece gestiti normalmente
dai singoli casi d'uso, senza confonderli con una indisponibilità
globale. Le richieste avviate prima di un refresh non possono
rimettere la UI in `ready` dopo che il loro effetto React è
stato invalidato: un epoch in memoria invalida anche i GET
che erano partiti prima di un errore di mutazione. Quando
cambia il JWT autenticato, la UI svuota l'elenco, elimina le
revisioni locali e mostra i dati soltanto se la lista è stata
verificata con **esattamente il token corrente**, evitando
un transitorio con dati del tenant precedente.
Il client **non** memorizza né accoda POST
per riprovarle senza consenso, evitando duplicazioni dopo
esiti incerti; rimangono necessari i meccanismi di
idempotenza lato server.

`tests/frontend/authenticated.spec.ts` usa Keycloak e
PostgreSQL reali, mantiene `navigator.onLine=true`, inietta
HTTP 503 sulla GET dell'elenco e su una POST di creazione
**e forza un errore di rete `fetch` sulla GET**, verifica
assenza di pratiche e comandi e riabilitazione
solo dopo una nuova GET autenticata riuscita. Questo test
non simula un vero outage di rete in produzione, né sostituisce
fault injection multi-replica o controlli di scadenza JWT.
Un secondo test controlla una race deterministica:
fa partire una `GET /api/perizie` e la blocca, simula il
fallimento HTTP 503 di una `assign` concorrente, poi
completa la GET precedente. Il risultato obsoleto non deve
far ricomparire pratiche e controlli; soltanto un nuovo
`Riprova` autenticato può sbloccare la UI. Non è una prova
multi-replica, ma protegge il contratto dell'epoch React.

Test RED [CI #43902048937](https://github.com/KeyserDSoze/Peritial/actions/runs/43902048937)
sulla UI precedente: 2 test browser falliti,
38 test full-stack precedenti superati.
Test GREEN con HTTP 503 GET/POST su
[CI #43902848205](https://github.com/KeyserDSoze/Peritial/actions/runs/43902848205)
(step `Authenticated OAuth + MCP integration` SUCCESS);
ulteriori regressioni di rete abortita e interleaving GET/POST
sono in verifica nella CI main.

## Aggiornamento controllato del service worker PWA (#13)

La UI gestisce moduli e allegati con modifiche ancora **non inviate**.
Per evitare interruzioni improvvise, `src/frontend/vite.config.ts`
utilizza `registerType: "prompt"` e `injectRegister: false`
al posto di `autoUpdate`: Workbox non usa
`skipWaiting` e `clientsClaim` automatici. La registrazione
è gestita dal componente React
`src/frontend/src/components/PwaUpdatePrompt.tsx`
con il modulo `virtual:pwa-register` e `onNeedRefresh`.

Quando arriva una nuova versione già scaricata, la UI mostra
un **avviso accessibile** in italiano/inglese: chiede di
salvare prima il lavoro non inviato, poi offre
`Aggiorna ora` oppure `Più tardi`. Soltanto il
click esplicito su `Aggiorna ora` invoca
`updateServiceWorker()` e può attivare il worker in attesa
ricaricando la pagina. `Più tardi` riduce l'avviso a
un pulsante `Aggiornamento pronto` che lo riapre;
il pulsante di aggiornamento non è disponibile offline.
Non vengono salvati draft, JWT o identificativi tenant per
implementare il prompt.

La CI impone anche un gate indipendente dopo il build:
`src/frontend/scripts/pwa-update-policy.test.mjs` usa
Node `--test` per verificare il contratto `prompt`,
l'assenza di registrazione automatica nel file HTML generato,
la presenza del service worker buildato, e i comandi UI di
aggiornamento, rinvio e riapertura. È eseguito da
`npm run build`, quindi anche nella build Docker e nel
job frontend. L'automazione **non** sostituisce una prova
multi-tab reale di aggiornamento in produzione, né
copre rollback SW dopo un rilascio incompatibile:
questi restano follow-up di #13.

## Modalità offline dichiarata, senza cache di dati autenticati (#13)

La PWA utilizza già il plugin `vite-plugin-pwa` per il manifest e
per il precaricamento degli asset **statici** della shell. In
`src/frontend/vite.config.ts` la configurazione Workbox ora
limita esplicitamente il fallback di navigazione al solo path
`/` (`navigateFallbackAllowlist: [/^\/$/]`) e disabilita
qualsiasi `runtimeCaching`: richieste `/api`, `/mcp`,
`/.well-known`, OAuth e altri endpoint server **non** vengono
serviti dal fallback né archiviate in runtime cache dal service
worker. Il middleware ASP.NET Core risponde inoltre con
`Cache-Control: no-store` e `Pragma: no-cache` per
**ogni** richiesta `/api` e `/mcp`, inclusi 401/403 e
stream MCP: così anche la cache HTTP ordinaria del browser
o del proxy non dovrebbe conservare dati del tenant.
`tests/frontend/security.spec.ts` verifica gli header su
REST autenticata, MCP autenticato e REST anonima.
Questa impostazione non sostituisce la revisione degli
asset precache a ogni nuova release.

`src/frontend/src/useOnlineStatus.ts` ascolta gli eventi browser
`online` e `offline`. Quando il browser segnala assenza rete,
`App.tsx` mostra un pannello con `role=status` e spiegazione
it/en, anziché la schermata di autenticazione e le pratiche
del tenant. Form di creazione, documenti e mutazioni perizia
non sono renderizzati, né vengono **accodati per un futuro invio**;
l'elenco locale e le revisioni temporanee vengono svuotati.
Quando la connettività torna, la UI invia una **nuova GET REST
autenticata** prima di ripopolare le pratiche.

`navigator.onLine` è un *indicatore browser*, non una prova
della raggiungibilità di Keycloak, PostgreSQL o delle API;
l'applicazione deve comunque gestire le chiamate fallite.
Una shell precache può essere visibile offline se il service
worker è già installato, ma **lavorare sulle pratiche offline
non è supportato**, l'accesso/rinnovo OIDC richiede rete, non
c'è una copia consultabile dei documenti né una coda di scrittura.
Non promettere disponibilità offline di dati protetti.

Test di regressione:
- `tests/frontend/smoke.spec.ts`, Playwright Chromium desktop/mobile:
  transizione online → offline → online, messaggio localizzato,
  niente accesso o pulsanti di modifica offline.
- `tests/frontend/authenticated.spec.ts` (solo Compose/Keycloak):
  pratica realmente creata prima della disconnessione,
  controllo `CacheStorage` del build PWA installato dopo una
  mutazione autenticata (precache popolata ma nessuna entry con
  path `/api`, `/mcp`, `/realms` o `/.well-known`),
  richiesta REST offline che **deve fallire** e non ricevere una
  risposta dalla cache, assenza di riferimenti tenant e azioni
  offline, ritorno online con refetch REST prima di mostrare
  di nuovo la pratica.
- RED della nuova suite `smoke` su
  [CI #43894072347](https://github.com/KeyserDSoze/Peritial/actions/runs/43894072347);
  GREEN frontend 12/12 su
  [CI #43894655555](https://github.com/KeyserDSoze/Peritial/actions/runs/43894655555).

Il completamento della PWA installabile, l'audit WCAG,
l'aggiornamento controllato del service worker e la verifica
offline del build di produzione sono ancora aperti in #13.

## Preferenze PWA persistenti e accessibilità lingua/tema (#13)

Il client memorizza **soltanto** due preferenze di interfaccia in
`localStorage` dello stesso origin browser:
`peritial.ui.language = it|en` e `peritial.ui.theme = light|dark`.
Il modulo `src/frontend/src/uiPreferences.ts` ammette solo questi
valori e intercetta errori di lettura/scrittura (`SecurityError`,
storage bloccato, quota o navigazione privata). Valori sconosciuti
o manipolati non vengono eseguiti: il fallback è lingua italiana
e tema chiaro. La funzionalità continua a operare nella sessione
corrente anche se il salvataggio delle preferenze viene bloccato.

`src/frontend/src/i18n.ts` inizializza i18next dalla preferenza
validata; `App.tsx` inizializza il tema prima del primo render React,
aggiorna `document.documentElement.lang` quando cambia lingua,
applica/rimuove la classe `dark` e rende `aria-pressed` del
pulsante tema coerente con lo stato. Il toggle lingua salva la
selezione `it/en` e il toggle tema salva `light/dark`.
Le preferenze non contengono token OAuth, credenziali, documenti,
identificativi tenant né dati delle perizie. Sono locali al
browser/origin, non sincronizzate tra dispositivi.

`tests/frontend/smoke.spec.ts` verifica, su Chromium desktop e
mobile, lingua HTML `it→en`, toggle tema via tastiera e
`aria-pressed`, persistenza di entrambi dopo reload, valori
arbitrari ignorati, ripristino tramite toggle e leggibilità
della pagina. Un test inietta eccezioni sulle **sole** chiavi
`peritial.ui.*`: il browser continua a usare le scelte nella
sessione corrente ma ritorna ai default dopo reload quando
non può scriverle. Non simula la disabilitazione globale di
`localStorage`, che potrebbe interessare librerie OIDC esterne.

Il gate frontend della CI usa `npm run build` e
`npm run test:e2e` (Playwright Chromium desktop e mobile).
**TDD RED**: [CI #43891355737](https://github.com/KeyserDSoze/Peritial/actions/runs/43891355737)
aveva 4 regressioni fallite prima del fix. **GREEN**:
[CI #43892664860](https://github.com/KeyserDSoze/Peritial/actions/runs/43892664860)
con 10/10 test frontend superati, inclusa l'iniezione
mirata di errori storage.

Questi test **non** costituiscono un audit completo WCAG né una
validazione di offline cache PWA; #13 resta aperta.

## Workflow perizie nella PWA — assegnazione, avanzamento, annullamento (#13/#9)

La UI autenticata in `src/frontend/src/App.tsx` ora usa il componente
`PeriziaWorkflow` per ogni pratica visibile nel tenant firmato:

- `Registered`: campo assegnatario con label accessibile e bottone
  `Assegna` (REST `POST /api/perizie/{id}/assign`).
- `Assigned`, `ToBeRevised`, `Revised`, `ToSend`, `Sent`:
  un singolo passo `Avanza` verso lo stato successivo ammesso dal Domain
  (`POST /api/perizie/{id}/advance`). REST e MCP condividono
  `PeriziaAdvanceTarget.Parse`: il JSON REST usa una **stringa simbolica**
  come `{"target":"ToBeRevised"}`, non un enum numerico. Numeri,
  nomi case-insensitive, `Cancelled` e transizioni illegali sono rifiutati
  con HTTP 400 (su MCP: errore del tool). Il gestore globale ASP.NET
  preserva anche gli HTTP 400 emessi da `BadHttpRequestException`
  durante il parsing del JSON (body numerico o sintatticamente malformato),
  anziché trasformarli erroneamente in 500. `security.spec.ts`
  verifica nomi validi, numeri, casing, stato terminale,
  JSON troncato e replay. Il client non offre salti di stato.
- Stati non terminali: `Annulla` richiede **una seconda azione esplicita**
  `Conferma annullamento` oppure `Indietro`, prima di inviare
  `POST /api/perizie/{id}/cancel`. Gli stati terminali
  `Billed`/`Cancelled`/`Duplicated` non offrono mutazioni.
- Stato e assegnatario sono aggiornati solo dalla **risposta REST confermata**,
  senza avanzamento ottimistico. Errori HTTP 409 CAS non
  alterano lo stato visualizzato: la UI mostra un messaggio
  e un bottone `Ricarica stato` che interroga
  `GET /api/perizie/{id}` per ottenere il valore corrente.
  La fusione fra lista GET e mutazioni confermate è difesa da revisioni
  locali temporanee: un GET avviato prima del POST non può
  ripristinare una versione obsoleta, mentre un GET avviato dopo
  può aggiornare la UI normalmente. Logout svuota elenco e revisioni.
- Label, azioni, errori e nomi degli stati sono tradotti tramite
  `src/frontend/src/i18n.ts` in italiano e inglese.
  La difesa reale resta nel backend OAuth/Domain/PostgreSQL,
  **non** nella visibilità dei controlli del browser.

`tests/frontend/authenticated.spec.ts` include un E2E Keycloak reale
eseguito su Chromium desktop e mobile: creazione, stato tradotto,
assegnazione, avanzamento, cambio lingua in corso d'opera,
annullamento a due click e assenza di azioni dopo lo stato terminale.
Una risposta 409 simulata per il client verifica che non
venga mostrato un successo inesistente e che il comando di
ricarica ripristini lo stato REST prima di una nuova assegnazione.
L'errore simulato **non** sostituisce i test PostgreSQL reali
di concorrenza in `PeriziaAtomicAuditTests` (ADR 0029).

**Restano aperti**: UI dei duplicati e storico perizia/documenti,
permessi di azione progressivi nel browser, design system
shadcn/ui completo, test automatici WCAG sistematici, le altre
sezioni PWA e le regole esatte del legacy `death` (issue #9/#13).

## Fault injection documentale (#5/#21)

FileIngestionCompensationTests esercita lo storage fake con upload parziale, errore metadata dopo PUT, cleanup fallito, cancellazione HTTP a metà commit, retry dopo errore e successo senza cancellazione. L'adapter S3 e quello Azure implementano DeleteAsync, con cancellazione idempotente. Le prove reali contro entrambi i provider e il recupero persistente dopo crash non sono ancora completati: ADR 0009.

I test in Peritial.Infrastructures.Tests riutilizzano un database PostgreSQL condiviso: oltre al lock di bootstrap SQL, la suite xUnit è serializzata a livello di assembly. Il lock da solo non impedisce che DDL del test successivo interferisca con la transazione RLS di un test in esecuzione. La serializzazione riguarda solo l'assembly di integrazione, non i test Business o Domain.

## Scanner documentale (#5)

L'API richiede `Malware:Clamd:Host` e porta TCP privata (default 3310), con timeout configurabile 1–120 s. Nessun fallback permissivo: `IFileMalwareScanner` restituisce `Clean` solo per la risposta esatta `stream: OK`. In CI `MalwareAdmissionTests`, `ClamdScannerContractTests` e Playwright EICAR verificano che i file infetti/non verificati non raggiungano la persistenza. Per produzione, gestire update firme e quarantena persistente: ADR 0010.

## Upload recovery con intenzioni durevoli (#21)

`UploadIntentOrderingTests` verifica che l'intento sia persistito prima di PUT e che un errore metadata non avvii una cancellazione rischiosa. `UploadRecoveryTests` usa PostgreSQL reale e ruoli separati: crash simulato, cleanup ritentato dopo errore storage, preservazione blob referenziato quando il commit ACK è incerto, consumo atomico dell'intento con metadata+audit, rispetto del grace period e accessi recovery a colonne ristrette. In CI il job Compose controlla che, dopo Playwright REST/MCP, non rimangano intenti pendenti. Le prove reali di kill/restart multi-replica e l'idempotenza HTTP restano aperte (#21).

## E2E idempotenza upload (#21)

`UploadIdempotencyTests` usa PostgreSQL reale con richieste concorrenti, identità separate, payload mismatch, replay con audit unico, rilascio su errore e worker recovery. `UploadFingerprintTests` verifica il digest di contenuto e metadati. Nel test Compose Playwright usa la medesima chiave in REST upload, retry REST e MCP ingest_file: stesso UUID, nessuna seconda riga audit; un payload differente riceve 409. L'asserzione SQL CI verifica due documenti/azioni di audit per i due browser, non sei eventi da retry.

## Upload lease e prova riavvio reale (#21)

`UploadLeaseTests` prova FORCE RLS e lease valida/scaduta usando PostgreSQL; `UploadLeaseHeartbeatTests` prova il rinnovo periodico durante uno storage PUT bloccato e l'annullamento fail-closed se non è più possibile estendere il lease. Nel job Compose `UploadRestartContractTests` ha due fasi: `docker compose kill --signal SIGKILL api`, seed di un oggetto reale SeaweedFS e di un intento PostgreSQL scaduto mentre API è ferma, poi `docker compose start api`, polling finché l'intento non viene rimosso e verifica finale che il blob non esista e che la key idempotente sia stata liberata. Nessun reset dei volumi per il recovery. Non equiparare questa prova a un HTTP upload spezzato *durante* PUT. Vedi ADR 0013.

## Prova reale mid-PUT con proxy di caos (ADR 0014)

Il job Compose esegue `bash tests/chaos/run_mid_put.sh` dopo le normali prove OAuth/MCP, storage e scanner. Un file PDF di test viene caricato con token Keycloak firmato e idempotency key locale; l'API usa il normale adapter AWS SDK ma punta temporaneamente a un **proxy TCP S3 di test**. Il proxy inoltra l'intestazione e un prefisso del **corpo HTTP PUT** al SeaweedFS reale, segnala l'avvenuto trasferimento e blocca il flusso. La pipeline verifica sia il segnale sia che la chiamata upload non sia terminata, invia `SIGKILL` all'API, fa scadere l'intento del processo morto, riavvia l'API standard e controlla che il worker abbia eliminato intent e chiave idempotente. Un test .NET verifica inoltre con SDK S3 autenticato il 404 del blob, l'assenza del documento e di audit di successo. Il file `docker/compose.chaos.yml` non va usato in produzione; la prova NON rappresenta Azure Blob, multi-replica o timeout DB reali.

## Migrazioni PostgreSQL versionate (#6, ADR 0015)

`SchemaMigrationTests` prova installazione V001/V002, re-run, drift checksum, schema preesistente non tracciato e concorrenza in PostgreSQL reale. Il migrator esplicito `scripts/db/Peritial.DbMigrator` non parte insieme all'API e richiede credenziali DDL indipendenti. Per il runbook e i limiti della demo Compose consultare `docs/database-migrations.md`. Non equiparare i test di schema nuovo a una prova di upgrade/restore del database legacy.

## Adozione V001 legacy verificata (#6, ADR 0017)

`LegacySchemaAdoptionTests` genera schemi isolati tramite `SchemaBootstrap` e prova la baseline di dati reali, il journal V001/V002, il rifiuto di modifiche a colonne, indici, grants, policy FORCE RLS e oggetti aggiuntivi. Il job `database-recovery` esegue inoltre `tests/backup/run_legacy_adoption.sh` e `tests/backup/run_prelease_adoption.sh` con PostgreSQL 17 reale, `pg_dump -Fc`, convalida SHA-256 e `pg_restore --list`, conferma operativa e fail-closed. I comandi `adopt` e `adopt-prelease` restano **offline e manuali**, non passa dall'avvio API; vedere `docs/database-migrations.md`. Non è ancora un restore DB+S3 né un test di upgrade di un ambiente produttivo.

## Checkpoint offline integrità PostgreSQL ↔ SeaweedFS S3 (#6, #21; ADR 0019)

Il progetto `scripts/recovery/Peritial.RecoveryCheckpoint` fornisce due comandi manuali `capture` e `verify`, abilitati solo con `PERITIAL_CHECKPOINT_CONFIRM=WRITERS_STOPPED`. `ObjectRecoveryCheckpointContractTests` viene eseguito dalla CI Compose con PostgreSQL e SeaweedFS reali, in uno schema isolato; verifica su due tenant oggetto mancante, byte diversi con dimensione identica, modifica dei metadati, chiave S3 riutilizzata, intent/idempotenza pending, credenziali app RLS-filtrate e percorso positivo. Il controllo richiede identità DB di sola lettura con capacità BYPASSRLS per non ignorare tenant, e non pubblica chiavi o contenuti nei log. **Nessun oggetto è copiato e nessun ripristino è eseguito**: vedere `docs/object-recovery-checkpoint.md`. Restano aperti disaster recovery coordinato dei blob, Azure Blob, retention/firma e un vero restore end-to-end.

## Recovery reale dei byte S3 in CI — laboratorio (#6, ADR 0020)

`ObjectRecoveryArchive` esporta in una directory privata manifest più file binari ordinali, con SHA-256 byte-per-byte; `ImportAsync` verifica in anticipo gli oggetti, il DB indipendente, l'assenza di upload pendenti e il bucket di destinazione vuoto, poi usa PUT condizionali e GET di verifica. Il CLI `Peritial.RecoveryCheckpoint` aggiunge `export` e `restore-objects` (seconda conferma `EMPTY_DESTINATION_BUCKET`), ora con chiave HMAC-SHA256 esterna obbligatoria di 32 byte da `PERITIAL_ARCHIVE_HMAC_KEY_FILE` (`0600`, fuori archive), test negativi su chiave assente/errata e MAC modificato. I server di destinazione `postgres-restore` e `seaweedfs-restore` sono nel profilo Compose **`recovery-test`**, fuori dalla API. Il job Compose esegue `ObjectRecoveryArchiveContractTests` e `tests/backup/run_complete_pg_s3_restore.sh` (pg_dump + pg_restore, export e import veri). **Non** è un backup automatico o una transazione DB↔S3 sotto traffico. Vedi `docs/object-recovery-checkpoint.md`.

## Bundle cifrato offline dell'intero restore set (#6, ADR 0022)

`SealedRecoveryBundleTests` nella suite backend prova chiavi separate, PGDMP con SHA-256, manifest S3 HMAC, crittografia AES-256-GCM a blocchi (con blob di più MiB), rifiuto di ciphertext/index alterati e wrong key, e pubblicazione **soltanto** dopo verifica completa in directory privata. `scripts/recovery/Peritial.RecoveryBundle` è il nuovo CLI amministrativo `seal|unseal`, compilato via `Peritial.slnx`. Il job Compose `tests/backup/run_complete_pg_s3_restore.sh` impone export S3, sigillo del set PostgreSQL + byte S3, rifiuto di chiavi non configurate o group-readable, rifiuto AES key errata, unseal privato, `pg_restore` sul PostgreSQL indipendente e `restore-objects` su SeaweedFS indipendente con hash di verifica. **Mai eseguire il drill con dati produttivi/volumi reali**: non sono testati snapshot atomici, KMS, eliminazione sicura dei file temporanei, offsite, Keycloak/Azure e disaster recovery completo. Runbook `docs/sealed-multiservice-recovery.md`.

## Gate tmpfs/swap sul plaintext di recovery (#6, ADR 0023)

Le API di export/import dell'archive S3, sealing di PGDMP+S3 e unsealing rifiutano automaticamente filesystem persistenti, leaf directory con permessi troppo ampi, symlink e swap attivo quando trattano dump/documenti in chiaro. Il helper `SecureRecoveryStaging` verifica il filesystem del mount **più specifico** da `/proc/self/mountinfo`, controlla `/proc/swaps` e richiede una directory privata `0700` su Linux. La suite `SecureRecoveryStagingTests` prova l'accettazione tmpfs privato e il fail-closed su disco, directory `0755`, symlink e tutte le tre API. La CI backend e Compose disabilita lo swap soltanto nei runner temporanei, mentre il drill in `tests/backup/run_complete_pg_s3_restore.sh` usa `/dev/shm` host bind-mounted nel container; la copia del dump decifrato conserva i permessi privati tramite un accesso privilegiato limitato al test. Il bundle finale rimane AES-256-GCM su storage persistente. KMS, snapshot sotto traffico, RAM/capacity operativa e DR reale **non** sono coperti.

## Replica remota di bundle cifrato — gate reale di laboratorio (#6, ADR 0024)

`SealedRecoveryBundle.ValidateEncryptedAsync` valida l'HMAC dell'indice e **ogni tag AES-GCM** senza creare file plaintext. `SealedBundleOffsite.UploadAsync/DownloadAsync` trasferisce esclusivamente i ciphertext, manifest e HMAC verso un nuovo prefisso S3; GET di verifica SHA-256 dopo le PUT condizionali, `complete` come ultima scrittura, download atomico in directory privata, rifiuto di prefissi incompleti, chiavi errate, hash corrotti e destinazioni esistenti. Test `SealedBundleOffsiteContractTests` sul terzo server `seaweedfs-backup` della Compose CI; il drill `run_complete_pg_s3_restore.sh` ora sigilla, trasferisce e scarica il bundle **prima** di ripristinare PGDMP e documenti. Non è immutabilità Object Lock, offsite geografico, multipart o gestione KMS; issue #6/#18/#21 aperte.

## Retention S3 fail-closed (issue #6, ADR 0025)

`OffsiteObjectLockPolicy` impone versioning attivo e Object Lock `COMPLIANCE` di almeno 30 giorni tramite API S3. `SealedBundleOffsite.UploadAsync` chiama il preflight prima delle PUT, valida `VersionId` e `GetObjectRetention` dopo ogni PUT, incluso il marker finale; `DownloadAsync` verifica il bucket e la retention di ogni versione corrente prima di accettare un backup remoto. La suite `OffsiteObjectLockPolicyTests` prova stati/configurazioni/retention senza accesso cloud; `SealedBundleOffsiteContractTests` sul server SeaweedFS demo prova che i percorsi predefiniti rifiutano il deposito non compatibile senza creare oggetti o file locali. Solo i test del server interno `seaweedfs-backup` con `PERITIAL_OFFSITE_CI_HTTP=DISPOSABLE_TEST_ONLY` possono usare `disposableUnprotectedDemo` per i drill precedenti. Non c'è ancora un test positivo su AWS S3 Object Lock COMPLIANCE reale, né un controllo di KMS/retention effettiva su un provider remoto di produzione.

## Prova positiva S3 Object Lock WORM (ADR 0026)

Il job CI **`worm-recovery`**, separato dai cinque job esistenti, usa PGSTY SILO (fork S3 compatibile) fissato per digest, su Linux `tmpfs` swap-free. `ObjectLockWormIntegrationTests` crea un bucket con Object Lock **già all'atto della creazione**, versioning `Enabled`, retention `COMPLIANCE` 32 giorni; esercita le vere API SDK `push-sealed`/`pull-sealed` senza flag demo, verifica `VersionId` e `GetObjectRetention`, controlla che un `DeleteObject` con `VersionId` sia rifiutato e che un controllo non protetto sia eliminabile, legge una versione protetta tramite ID dopo un delete marker e verifica che il download per chiave corrente fallisca chiuso. Il set dimostrativo contiene un dump PGDMP sintetico e manifest S3 autenticato. La CI Compose continua ad esercitare PostgreSQL 17 e 4 documenti reali, ma sul SeaweedFS **senza** garanzia WORM (modalità test esplicita). Non sono testati Object Lock su AWS reale, recupero di set storici completi, KMS, geo-offsite e backup di produzione.

## Historical WORM restore con VersionId espliciti (ADR 0027, #6)

`SealedBundleOffsite.DownloadHistoricalAsync` usa un insieme di versioni richiesto dall'operatore per **tutti** i ciphertext, index e marker del backup remoto. Non ricorre mai implicitamente alla versione corrente o a una ricerca arbitraria nelle versioni: ogni `GetObject` specifica `VersionId`; l'insieme deve coincidere esattamente con le key dell'inventario **HMAC autenticato e vincolato al backup ID**. Verifica `GetObjectRetention` per ogni versione COMPLIANCE, hash e tag GCM prima di rinominare la directory privata. La CLI `pull-historic` usa `PERITIAL_BUNDLE_CONFIRM=ISOLATED_HISTORICAL_DOWNLOAD` e `PERITIAL_OFFSITE_VERSION_SELECTION_FILE` (parser JSON v1 stretto, duplicati/path traversal/missing keys rifiutati), **senza bypass CI**. Il job `worm-recovery` estende `ObjectLockWormIntegrationTests` con un blob cifrato oltre 1 MiB e due delete marker, e verifica il recupero completo e le condizioni negative. Per il formato JSON e il runbook vedere `docs/sealed-multiservice-recovery.md`. Restano esclusi AWS S3 di produzione, key lifecycle KMS, IAM minimo e recovery automatizzato delle versioni storiche.

## Due ruoli S3 scoped nella CI WORM (ADR 0028, #6)

La suite `worm-recovery` genera due IAM JSON con `scripts/recovery/render_worm_iam.py`, li valida con `tests/backup/test_worm_iam_policies.py` (nomi bucket e scope, rifiuto di wildcard/DELETE/admin, permessi 0600 e nessun overwrite), e usa `mcli admin` sul solo server SILO effimero per associare policy a utenti **separati**. `ObjectLockWormIntegrationTests` continua a creare e configurare il bucket con identità admin, ma esegue `SealedBundleOffsite.UploadAsync` soltanto con il writer scoped, `DownloadAsync/DownloadHistoricalAsync` soltanto con il reader scoped. I test rifiutano DELETE su un bucket di controllo senza Object Lock, scritture del reader, modifiche del bucket da parte del writer, elenco dello storico da parte del writer e accesso alle key fuori da `sealed-v1/`. Credenziali CI dimostrative; AWS IAM role/STS e KMS produttivi non ancora integrati. Vedere il runbook di recovery e ADR 0028.

**Revisione IAM:** il server SILO tratta `GetObjectVersion` come `GetObject` e può permettere `ListBucketVersions` con il normale `ListBucket`. L'uploader non ha perciò più `GetObject` né `GetObjectVersion`. `SealedBundleOffsite.UploadAsync` richiede in modalità reale un secondo client read-only `verificationSource`, distinto dall'uploader, per i GET di confronto SHA-256 dopo ogni PUT. Il comando `push-sealed` reale richiede credenziali separate `PERITIAL_OFFSITE_VERIFY_S3_ACCESS_KEY` e `PERITIAL_OFFSITE_VERIFY_S3_SECRET_KEY`. La CI WORM prova che l'uploader non può leggere ciphertext correnti o storici e che il reader non può modificarli; il mancato elenco di VersionId **non** è verificato su SILO. La simulazione AWS IAM/STS e la rotazione restano aperte.

## Regressione JWT scaduti con issuer reale (#4)

La CI `compose` crea **soltanto nel Keycloak dimostrativo effimero** il client
`peritial-test-expired` a partire da
`docker/keycloak/peritial-expiry-client.json`, usando le variabili bootstrap già
presenti *dentro* il container Keycloak. Il client è separato da `peritial-web`
(PKCE) e dagli altri client E2E: il suo access token firmato dura **5 secondi**
ed è limitato ai permessi di lettura, con tenant e audience del test.

`tests/frontend/security.spec.ts` prima controlla l'accesso a REST
(`GET /api/perizie`) e MCP (`tools/list`) con il token valido, poi attende
`exp + 65 s` (i 60 secondi di `ClockSkew` di ASP.NET Core più un margine
deterministico) e richiede **401 su entrambe le superfici** con lo stesso
JWT ancora correttamente firmato. Non stampa né persiste il bearer. Il test
richiede `E2E_FULL_STACK=1`, perciò è saltato nella suite frontend isolata e
obbligatorio solo nella Compose CI.

Questa è una prova su Keycloak locale, **non** una validazione Entra ID reale,
revoca/rotazione chiavi o client MCP esterni. Non importare il client di test
in realm o ambienti produttivi; mantenere aperta la issue #4 per i gate residui.


## TDD ricerca/autocomplete Sinistri tenant-scoped (#7/#12, ADR 0033)

La ricerca REST `GET /api/sinistri/search` e il tool MCP
`search_sinistri` accettano `prefix` (2–100, trim,
case-insensitive **letterale**), `occurredFrom` e `occurredTo`
(DateOnly ISO, filtri inclusivi sulla data evento) e `limit`
(1–50, default 10). Il servizio controlla `peritial.read` o
`peritial.write` e deriva il tenant soltanto dal JWT.
Il repository esegue il predicato tenant + `ILIKE` escaped +
date **nel database prima di ORDER BY/LIMIT**;
nessuna ricerca locale sui primi 50/100 elementi.
I metacaratteri SQL `%`, `_`, `!` nell'input sono
trattati letteralmente, quindi non estendono il filtro.

Il ciclo RED → GREEN è coperto da:
- `SinistroServiceTests`: normalizzazione, date inverse/default,
  limiti, scope, projection senza tenant, ordinamento e filtro
  prima del cap.
- `SinistroPersistenceTests`: test PG17 in schemi isolati e
  ruolo `peritial_app` su FORCE RLS, A/B e caratteri wildcard,
  con record più recenti da escludere prima del limite.
- `tests/frontend/security.spec.ts`: JWT firmati reali
  da due tenant, REST e tool MCP, prefisso `SR_%...`,
  range, cap=1, default no-cache, 400/401/403, nessuna riga
  dell'altro tenant. Il test gira su desktop e mobile nella
  Compose CI effimera con fixture V003.

**Residui:** nessun indice specializzato garantito per `ILIKE`
case-insensitive su grandi cataloghi: misurare
`EXPLAIN ANALYZE` con cardinalità realistiche e, se necessario,
aggiungere l'indice in una migrazione nuova senza mutare V003.
Mancano cursor, full-text/fuzzy, collegamento Sinistri–Perizie,
autocomplete PWA e deployment/tenancy Entra reali. Non chiudere
la #7 né la #12. Dettagli in
[ADR 0033](../adr/0033-sinistri-prefix-autocomplete.md).


## Perizia ↔ Sinistro: V004, RLS e audit atomico (#7/#9/#12/#21)

L'ADR 0034 registra la nuova migrazione forward-only
`scripts/db/migrations/V004__perizie_sinistro_link.sql`.
La tabella `perizie` ottiene un `sinistro_id uuid NULL` con
FK composta `(tenant_id,sinistro_id)` verso
`sinistri(tenant_id,id)`. La PK composta e le policy
FORCE RLS già attive non cambiano; il vecchio schema
V001 e la migrazione V003 rimangono immutati.
Nessuna perizia viene collegata automaticamente e
non esiste un modello di reparent/unlink silenzioso.

**Operazioni**: `POST /api/perizie/{id}/sinistro` con body
`{"sinistroId":"UUID"}` / tool MCP
`link_perizia_sinistro(periziaId,sinistroId)`, scope
`peritial.write`. Il POST ritorna 201 con il solo
`{periziaId,sinistroId}`; sinistro o perizia
assente/estero → 404, link già presente → 409.
`GET /api/sinistri/{id}/perizie` / tool
`list_perizie_for_sinistro(sinistroId)` ritorna
soltanto gli ID della perizia, scope
`peritial.read` oppure `peritial.write`.
Il tenant e il subject sono ricavati dal JWT
firmato, mai da header/body.

`PgPeriziaSinistroLinkRepository` usa un
`TenantDbScope` unico: verifica le due risorse con
RLS, esegue `UPDATE ... WHERE sinistro_id IS NULL`,
poi append `perizia.link-sinistro/succeeded`
nella **medesima transazione PG**. Race concorrenti:
solo un link può confermarsi; nessun audit di
successo per conflitto. Audit duplicato/fallito:
rollback dell'associazione. La FK composta
impedisce cross-tenant anche con SQL elevato.

**Test**: `PeriziaSinistroLinkServiceTests` valida
scope, audit denied, DTO allowlist e lettura tenant;
`SinistroPersistenceTests` usa PostgreSQL
reale, due tenant, replay 409, test FK diretto
cross-tenant e audit duplicato con rollback.
`tests/frontend/security.spec.ts` crea casi e
perizie con due JWT Keycloak, collega A via REST
e B via MCP, verifica 404 simmetrico
missing/foreign, 409 replay, 401/403,
cataloghi id isolati e assenza di aggiornamenti
dopo tentativi respinti. La CI `database-recovery`
fa il dump/restore del link e della versione
V001–V004 del journal.

**Importante per sviluppo**: Compose demo
continua a creare lo schema V001 **senza journal**.
Soltanto il job Compose CI su stack effimera
applica V003 e poi V004 come fixture SQL prima
dei test Keycloak. Non copiare questa tecnica
su database con dati: usare sempre il migrator
privilegiato e il runbook `docs/database-migrations.md`,
con backup e lock. V004 è disponibile soltanto
dopo applicazione esplicita della migrazione.
Non è completato il modello legacy di polizze,
anagrafiche, ricerca composita, UI, reparent o
import Accertatore/Classic.


## PWA: selezione Sinistro per singola Perizia (#7/#12/#13, ADR 0035)

La PWA ora include `PeriziaSinistroPicker` sotto
ogni `PeriziaWorkflow`. Il pannello è **chiuso per
default** e consulta il server soltanto quando
l'operatore lo apre: evita letture N+1 nella lista
perizie. Al primo accesso usa
`GET /api/perizie/{id}/sinistro` con il JWT
firmato corrente. Il DTO `{periziaId,sinistroId:
UUID|null}` distingue una perizia visibile
e ancora scollegata (200/null) da una perizia
mancante/di altro tenant (404). Il nuovo tool
MCP `get_perizia_sinistro(periziaId)` condivide
esattamente il servizio Business. Una perizia
già collegata appare con il riferimento del
sinistro verificato tramite
`GET /api/sinistri/{id}`, senza pulsanti
per ripetere il link.

Per una perizia non collegata, l'utente ricerca
esplicitamente per prefisso (2–100 caratteri)
tramite `GET /api/sinistri/search?prefix=...&limit=20`.
Il risultato viene filtrato **sul server**
da tenant/FORCE RLS, wildcard letterali e
limite applicato dopo i predicati; la PWA
offre un select etichettato con riferimento
e data evento, non accetta tenant dal form.
`POST /api/perizie/{id}/sinistro` è
inviato **una sola volta dopo selezione**
e aggiorna il pannello soltanto dopo
conferma 201. Lo scope `peritial.write`
della sessione OIDC è un indizio di UI
per nascondere controlli ai lettori;
la decisione definitiva resta nel server.

Un **409** (link concorrente o già presente)
non trasforma il candidato in relazione:
la UI invalida i risultati e chiede
«Ricarica collegamento», rifacendo
GET con il token corrente prima di
mostrare il nuovo stato. Un 401/403,
network error, timeout, 429 o 5xx
cancella la lista protetta e blocca
ogni scrittura, come le altre sezioni.
Un 404 sul dettaglio della perizia,
ormai non visibile, produce anch'esso
fail-closed. Cambi di query, chiusura,
token e unmount invalidano le risposte
asincrone precedenti. Offline, le perizie
sono nascoste e non esiste coda/replay
di mutazioni. Messaggi e form in it/en.

**TDD e prove**:
- `PeriziaSinistroLinkServiceTests`: read model
  linked/unlinked, scope reader/write, ID
  foreign/missing e nessun audit in lettura.
- `SinistroPersistenceTests`: PostgreSQL V004
  reale con tenant A/B e FORCE RLS, lettura
  perizia visibile non collegata, link
  confermato e 404 cross-tenant.
- `tests/frontend/authenticated.spec.ts`:
  login PWA PKCE vero, sinistro creato via
  REST, selezione UI, POST 201, ricarica
  pagina con associazione persistita;
  altro client collega una seconda perizia
  prima del POST PWA e causa 409, dopo
  cui il reload rivela il link corrente;
  test offline senza dati né coda.
- `tests/frontend/security.spec.ts`:
  due JWT Keycloak firmati, perizia
  linked/null per REST+MCP, ID foreign
  indistinguibile da missing (404),
  readonly, anonimo 401 e DTO senza
  tenant ID.

**Non comprende** creazione o edit
Sinistri da PWA, ricerca senza click/
debounce, filtri avanzati o cursor,
unlink/reparent, import Classic,
anagrafiche/polizze, IAM Entra reale
o audit completo WCAG. La migrazione
V004 resta manuale su database
versionati: nel Compose test si applica
solo come fixture effimera dopo V003,
mai all'avvio API. [ADR 0035](../adr/0035-pwa-sinistro-link-authoritative-read.md).


## PWA: catalogo e creazione Sinistri con JWT (#7/#13, ADR 0036)

Il componente `SinistroCatalog` è visibile nella PWA
protetta solo con un token verificato e connessione online.
È **collassato per default**; al click «Apri catalogo
sinistri» carica soltanto i 50 Sinistri più recenti per
data evento dal backend usando
`GET /api/sinistri?limit=50` e il JWT corrente.
La ricerca per prefisso/dati evento nel catalogo usa
ora la GET server-side dedicata (ADR 0038), **non** un filtro
locale delle prime 50 righe. Anche il selettore
Perizia–Sinistro usa la ricerca REST già esistente.
Il catalogo non viene caricato automaticamente a
ogni lista perizie perché la Compose demo inizia
dal V001 storico senza le fixture V003/V004.

Il form di creazione è mostrato solo ai titolari
di scope OIDC `peritial.write` (controllo UX,
la sicurezza viene imposta dal backend).
Accetta riferimento esterno, data sinistro
obbligatoria e data denuncia opzionale.
`POST /api/sinistri` viene inviato solo
su conferma esplicita, con data ISO e senza
`tenantId`. **Solo HTTP 201** autorizza
l'inserimento nella lista, mantenendo
`occurredOn DESC,id` e il limite dei 50;
la ricevuta confermata è visibile anche
quando la pratica è più vecchia del limite.
Un 400 visualizza un errore e non crea
nuovi record client-side. 401/403,
5xx o rete indisponibile svuotano
completamente la vista protetta, senza
ripetizione/coda dei POST incerti.
Offline tutti i dati tenant del catalogo
sono rimossi; il successivo accesso
richiede una nuova lettura autenticata.

**E2E Playwright desktop/mobile** nel file
`tests/frontend/authenticated.spec.ts`:
login Keycloak Authorization Code + PKCE,
pannello inizialmente chiuso, form con
date valide, richiesta 400 simulata
senza creazione locale, POST reale 201
e JSON privo di tenant ID, GET reale
di persistenza, refresh e recupero della
pratica, offline senza dati né pulsanti
e nuova lettura alla riconnessione.
La suite Security/Business/PG verifica
già policy OAuth, audit e RLS della
creazione Sinistri.

**Limiti:** non è il CRUD Sinistri
completo in PWA. La modifica
con `expectedVersion` è arrivata nel successivo
[ADR 0037](../adr/0037-pwa-sinistri-cas-editor.md); mancano delete,
filtri/cursor e ricerca avanzata,
relazioni anagrafiche/polizze e
import legacy. V003/V004 restano
migrazioni amministrative manuali.
[ADR 0036](../adr/0036-pwa-sinistri-catalog-create.md).


## PWA: modifica Sinistri con optimistic concurrency CAS (#7/#13, ADR 0037)

`SinistroEditor` è integrato in
`SinistroCatalog` per ogni riga,
solo se il token OIDC corrente contiene
lo scope `peritial.write`. L'autorizzazione
è comunque sempre imposta dal backend.
L'apertura del form esegue
`GET /api/sinistri/{id}` con il JWT
corrente, per non trattare il
`version` dell'elenco come snapshot
aggiornata. Le tre proprietà editabili
sono `externalReference`,
`occurredOn` e `reportedOn`
(facoltativa); la versione del record
verificato viene mostrata nel form.

Al submit esplicito viene inviato
`PUT /api/sinistri/{id}` includendo
**`expectedVersion` uguale alla
versione del GET/PUT confermato**,
mai ottenuta da una nuova lettura
invisibile per aggirare la concorrenza.
Solo risposta **200** aggiorna
editor/catalogo, con l'ordinamento
`occurredOn DESC,id` e il cap 50.
Il repository PostgreSQL usa già
CAS con audit `sinistro.update`
atomico nella stessa transazione,
sotto FORCE RLS. Il browser
non trasmette `tenantId`.

In presenza di **400** la riga
rimane invariata, il modulo e
`expectedVersion` sono conservati
per correggere la richiesta.
Su **409** il browser considera
obsoleti sia l'editor sia la
riga della lista: cancella
la snapshot e i campi, oscura
riferimento/date non verificabili,
disabilita ogni altro PUT e
richiede il click «Ricarica
sinistro» per un nuovo GET
firmato. Dopo un GET riuscito,
l'operatore deve esplicitamente
riesaminare e salvare la versione
vincente. **Nessun auto-merge
o retry di scritture concorrenti.**
Un 404 sul GET/PUT, 401/403,
5xx e errori di rete causano
fail-closed della vista PWA.
Su offline non vengono conservati
dati tenant in cache API, né
accodati PUT, e i risultati
asincroni tardivi sono ignorati
dopo unmount, chiusura o cambio
del JWT. Messaggi it/en e input
con label e `role=alert`.

**E2E `authenticated.spec.ts`**:
- login Keycloak PWA con PKCE,
  creazione caso di test su PG V003;
- caricamento form con versione 1;
- simulazione **400** senza
  modificare valori/verifiche server;
- PUT v1→v2 con verifica del
  payload `expectedVersion=1`
  e GET server di conferma;
- altro client aggiorna v2→v3,
  mentre l'editor ha v2;
- PUT stale produce **409**,
  campo precedente non più
  visibile, salvataggio disabilitato,
  niente replay;
- click «Ricarica sinistro»
  recupera v3; nuovo PUT usa
  `expectedVersion=3`,
  aggiorna v4 e sopravvive al
  refresh del browser.

Durante il TDD è emerso anche
un test E2E **fragile** del vecchio
`list_sinistri(limit=10)`:
con nuovi record futuri in parallelo
non è corretto pretendere che
un caso con `occurredOn=2026`
sia nei primi 10. Il test ora
controlla il cap della lista e
verifica la reperibilità del caso
con la ricerca server-side per
prefisso sia via REST sia MCP,
senza alterare la semantica del
repository per favorire i test.

**Limiti:** non sono ancora
disponibili DELETE, paginazione
cursor, filtri polizze/anagrafiche,
import Classic/Accertatore o
parità del gestionale legacy.
L'upgrade V003/V004 continua
a richiedere il migrator esplicito
e backup per volumi persistenti.
[ADR 0037](../adr/0037-pwa-sinistri-cas-editor.md).

## PWA: ricerca Sinistri per prefisso/date dal catalogo (#7/#13, ADR 0038)

Nella vista autenticata il form «Cerca nel catalogo» invia
`GET /api/sinistri/search?prefix=...&occurredFrom=...&occurredTo=...&limit=50`
con il JWT corrente, solo su click e con almeno 2 caratteri. Le
date incidente sono opzionali e inclusive; i limiti 2–100, la relazione
from<=to e gli scope sono validati dal servizio Business, non
delegati all'interfaccia. Ricerca letterale `%_` supportata, query
PostgreSQL FORCE RLS prima del limit; il browser non passa tenant ID.
Il picker associazione resta con il cap 20 precedente.

La ricerca non dipende dai recent-50 della vista iniziale. Il catalogo
mostra il filtro applicato e un avviso di limite, ripete la ricerca
con «Aggiorna sinistri» e torna a `GET /api/sinistri?limit=50` quando
si cancellano i filtri. Nessuna GET vecchia ripristina la lista dopo
cambio ricerca, chiusura, token o offline. 401/403/503 e rete
indisponibile seguono il fail-closed della pagina. Un POST confermato
durante una ricerca attiva rilegge i match, senza inserire una riga
incoerente nella finestra filtrata. La riconciliazione dell'editor
riguarda solo righe già mostrate; nessun nuovo risultato viene dedotto
dal browser. E2E Keycloak desktop/mobile con escaping letterale
`%_`, date, cap, refresh, reset e offline in
`tests/frontend/authenticated.spec.ts`.

Ancora aperti cursor/oltre 50, full-text/fuzzy, indice e benchmark
su grandi cataloghi, filtri polizze/anagrafiche, DELETE e WCAG.
V003/V004 sempre con migrazione amministrativa esplicita.
[ADR 0038](../adr/0038-pwa-sinistri-server-search.md).
