# MCP come superficie applicativa

## Obiettivo
Esporre le funzionalità tramite Model Context Protocol (SDK ufficiale .NET, HTTP stateless), raggiungibile via `/mcp`. Ogni tool invoca i **medesimi** use case dell'API REST e riceve il tenant esclusivamente dal JWT validato.

## OAuth
- Authorization Code + PKCE per i client interattivi; access token Bearer con audience dell'API e scope `peritial.read`/`peritial.write`.
- API/MCP sono OAuth **protected resource**: autorizzazione delegated al provider OIDC configurato, Entra in Azure o Keycloak on-prem.
- Discovery metadata RFC 9728: `/.well-known/oauth-protected-resource/mcp` espone il `resource` canonico di `/mcp`; la root `/.well-known/oauth-protected-resource` descrive la API root. Il challenge `401` MCP include `WWW-Authenticate: Bearer resource_metadata="<origin>/.well-known/oauth-protected-resource/mcp"`.
- `Auth:Resource` deve essere l'URL pubblico canonico **completo** (es. `https://api.example.org/mcp`); in ambiente Production è obbligatorio HTTPS. Mai comporre URL discovery dal valore del request Host, che può essere controllato da un client.
- La verifica JWT rifiuta token non validi, audience errata o privi di `tenant_id`/subject firmati con `401` prima dei servizi. Scope assente e insufficiente restituisce `403` tramite policy di endpoint REST e controllo all'ingresso MCP; i tool applicano anche scope nelle classi Business.
- Denial-by-default: nessuna invocazione tool senza token valido, tenant, scope pertinente. Non passare segreti in query string, payload tool o log.
- Non affidarsi alle sole descrizioni del tool: le autorizzazioni avvengono nel service layer/API. Per MCP ingest, fissare limiti byte e audit.
- Supporto client MCP esterni dipendente dalla compatibilità OAuth e dalla configurazione dell'authorization server: niente garanzia di compatibilità universale senza un test di integrazione.

## Catalogo atteso
| Capability | Tool | Fase |
|---|---|---|
| Perizie | list_perizie, get_perizia, create_perizia, assign_perizia, advance_perizia, cancel_perizia | lettura scoped e mutazioni REST/MCP con audit atomico e CAS (#9/#12) |
| Documenti | list_documents, get_document (lettura metadata), ingest_file (base64 limitato); REST multipart | catalogo read-only tenant-scoped + ingestion; download/report ancora aperti (#11/#12) |
| Sinistri e collegamenti perizie | list_sinistri, search_sinistri, get_sinistro, create_sinistro, update_sinistro, link_perizia_sinistro, list_perizie_for_sinistro, get_perizia_sinistro, unlink_perizia_sinistro | list/search/read/create/update Sinistri; link/unlink esplicito con FK tenant-scoped e audit atomico, senza reparent atomico/CRUD Sinistri UI completo (#7/#9/#12/#13) |
| Anagrafiche, import XML, report, messaggi, contabilità, KPI | tool dedicati per ciascun use case | iterazioni successive |

Per file grandi: non trasportare payload di gigabyte via JSON MCP; fornire un tool per ottenere URL di upload firmati/scoped e una procedura di completamento (da implementare). Una tool call MCP non equivale ad accesso indiscriminato al filesystem.

## Sinistri: primi contratti REST e MCP (#7/#12)

Le route `GET /api/sinistri?limit=50`, `GET /api/sinistri/search`, `GET /api/sinistri/{id}`,
`POST /api/sinistri`, `PUT /api/sinistri/{id}` e i tool
`list_sinistri(limit=50)`, `search_sinistri(prefix,occurredFrom?,occurredTo?,limit=10)`, `get_sinistro(id)`,
`create_sinistro(externalReference, occurredOn, reportedOn?)`,
`update_sinistro(id, externalReference, occurredOn, expectedVersion, reportedOn?)`
delegano allo **stesso `SinistroService`**. Le richieste devono
avere JWT validato con tenant e subject; non è accettato alcun
parametro `tenantId` dal client. Le query usano FORCE RLS (V003)
con `TenantDbScope` e non enumerano i sinistri di altri tenant.

| Operazione | Richiesta | Risultato |
|---|---|---|
| Lista | `GET /api/sinistri?limit=50` / `list_sinistri` | Array di ultimi sinistri per `occurredOn DESC,id`; `limit` da 1 a 100 |
| Ricerca/autocomplete | `GET /api/sinistri/search?prefix=...&occurredFrom=...&occurredTo=...&limit=10` / `search_sinistri` | Prefisso letterale case-insensitive (2–100), date evento incluse, cap 1–50; filtro SQL prima di LIMIT |
| Dettaglio | `GET /api/sinistri/{id}` / `get_sinistro` | Proiezione tenant-scoped; GUID estranei e mancanti non distinguibili (404 REST) |
| Creazione | `POST /api/sinistri` / `create_sinistro` | JSON `{"externalReference":"RIF","occurredOn":"2026-11-01","reportedOn":null}`, REST 201 |
| Modifica | `PUT /api/sinistri/{id}` / `update_sinistro` | Stesso JSON con `expectedVersion:1` obbligatoria; versione successiva restituita solo dopo COMMIT; stale REST 409 |

I campi dei DTO sono **solo** `id`, `externalReference`,
`occurredOn`, `reportedOn`, `createdAtUtc`, `version`.
Le date sono di calendario ISO, **non** datetimes storici, e
le risposte non contengono tenant ID, credenziali, chiavi storage,
audit soggetti o altri dati interni.

Le letture richiedono `peritial.read` o `peritial.write`; le
mutazioni `peritial.write`. I rifiuti senza scope via Business
sono auditati come `sinistro.create|update / denied` senza
riferimento né body. Il repository scrive successi
`sinistro.create|update / succeeded` **nella medesima
transazione PostgreSQL**. Per evitare lost update, il
`expectedVersion` del client è confrontato prima e durante
l'UPDATE SQL CAS. Le risposte non sono cacheabili
(`Cache-Control: no-store`).

**Autocomplete ricerca:** `prefix` è obbligatorio (trim, da
2 a 100 caratteri); i caratteri `%`, `_` e `!`
sono **letterali** e non wildcard del chiamante.
`occurredFrom` e `occurredTo` filtrano facoltativamente
la data evento con estremi **inclusivi** (`YYYY-MM-DD`);
la data denuncia non influisce. `limit` default 10,
minimo 1, massimo 50. Prefisso invalido, intervallo
inverso e cap superato generano **400 REST**
(errore del tool MCP). Il risultato segue
`occurredOn DESC,id`; il repository esegue
WHERE tenant, prefisso e date **prima** di LIMIT,
sempre in transazione FORCE RLS. Non è ricerca
full-text né una lista paginabile oltre il cap;
per cataloghi grandi è da validare un indice ILIKE
dedicato in una migrazione nuova, oltre ai benchmark.
[ADR 0033](../adr/0033-sinistri-prefix-autocomplete.md).

**Installazione:** V003 è una migrazione **esplicita**. Il
Compose demo con V001 non journaled non la applica
automaticamente. Solo la CI temporanea inietta V003 come
fixture senza journal prima del test REST/MCP Keycloak;
non adottare questa procedura come upgrade di produzione.
Per database versionati usare il migrator con identità DDL
e backup verificato come da [runbook](database-migrations.md).

**Limitazioni:** nessun DELETE, filtri full-text/fuzzy, indice specifico per ILIKE a scala produttiva,
cursor, relazioni polizze/anagrafiche, CRUD Sinistri PWA completo o import
Classic/Accertatore. Il primo selettore di collegamento PWA è già disponibile. Non è un CRUD completo. [ADR 0032](../adr/0032-sinistri-rest-mcp-oauth-cas.md).

## Collegamento Perizie–Sinistri, V004 (#7/#9/#12)

La migrazione amministrativa **V004** crea una relazione opzionale
`perizie.sinistro_id` con FK composta `(tenant_id,sinistro_id)`
verso `sinistri(tenant_id,id)`. La tabella perizie rimane
protetta da FORCE RLS. Un sinistro può avere più perizie; una perizia
può essere collegata a **un solo sinistro**, senza scollegamento
o riassegnazione automatica.

| Operazione | REST | MCP | Scope |
|---|---|---|---|
| Collegare una perizia già esistente | `POST /api/perizie/{id}/sinistro` con `{"sinistroId":"UUID"}` → 201 | `link_perizia_sinistro(periziaId,sinistroId)` | `peritial.write` |
| Elencare gli ID perizia di un sinistro | `GET /api/sinistri/{id}/perizie` → UUID[] | `list_perizie_for_sinistro(sinistroId)` | `peritial.read` o `peritial.write` |
| Consultare il link di una perizia | `GET /api/perizie/{id}/sinistro` → `{periziaId,sinistroId:UUID|null}` | `get_perizia_sinistro(periziaId)` | `peritial.read` o `peritial.write` |

Entrambe le superfici usano **lo stesso** `PeriziaSinistroLinkService`.
Il tenant e il subject arrivano solo dal JWT firmato.
Nel database le due risorse sono verificate nel tenant
prima del link e la FK composta impedisce associazioni
cross-tenant anche a chi bypassa accidentalmente il controllo
Business. ID esterno/inesistente: **404 REST**; link già presente:
**409 REST**. Il link si salva con una UPDATE CAS
`WHERE sinistro_id IS NULL` e il suo
`perizia.link-sinistro / succeeded` viene scritto
**nella stessa transazione PostgreSQL**.
Un errore audit provoca rollback completo. Il Business
audita `denied` per chi invoca senza scope; il blocco
REST avviene prima tramite policy globale.

**Nota serializzazione MCP:** il DTO in REST include sempre
`sinistroId:null` se la perizia è visibile ma non collegata.
Nel testo di risposta JSON-RPC del tool MCP, la serializzazione
del valore nullable omette il campo `sinistroId` quando è
`null`: il risultato senza quel campo e senza
`isError:true` indica **perizia visibile non collegata**;
ID assente/foreign continua a produrre un errore MCP.
I client non devono interpretare l'omissione come
una relazione sconosciuta, né inferire un tenant.

Il nuovo read model della singola perizia distingue
**perizia visibile ma non collegata** (`sinistroId:null`) da
**perizia assente o estranea** (404 REST, errore MCP).
Usa lo stesso servizio Business e un SELECT PostgreSQL
sotto FORCE RLS; non scrive audit di successo. La PWA
ne fa richiesta **solo quando l'utente apre il pannello**
e non assume che una perizia sia scollegata solo perché
la lista delle perizie non mostra un ID sinistro.
[ADR 0035](../adr/0035-pwa-sinistro-link-authoritative-read.md).

La lista espone **solo UUID**, non dati personali di
perizie/sinistri di altri tenant, token, tenant ID o audit.
Le risposte API sono `Cache-Control: no-store`.

**Installazione e limiti:** V004 viene applicata solo
dal migrator amministrativo dopo V003, con backup e
journal. La CI usa **esclusivamente sulla Compose
effimera** le fixture SQL V003 e poi V004, senza
alterare il bootstrap storico V001 né simulare il journal.
Non sono implementati reparent, unlink, eliminazione
sinistri/perizie in cascata, CRUD Sinistri completo in PWA
o il mapping degli ID legacy. Il selettore di associazione PWA
è invece disponibile secondo [ADR 0035](../adr/0035-pwa-sinistro-link-authoritative-read.md). Verifiche via due JWT
Keycloak di test, non Entra ID produttivo. [ADR 0034](../adr/0034-perizie-sinistri-v004-tenant-fk.md).

## Catalogo metadati documenti REST/MCP (#11/#12)

Le route `GET /api/files`, `GET /api/files/page`, `GET /api/files/{id}`
e i tool MCP `list_documents`, `page_documents`, `get_document`
usano lo stesso `DocumentCatalogService` di sola lettura.
Richiedono un JWT firmato con tenant/subject e scope
`peritial.read` **oppure** `peritial.write`.
L'identità del tenant **non** è accettata dal client: ogni
query passa per `TenantDbScope` con FORCE RLS PostgreSQL.

- `GET /api/files?limit=50&periziaId=<UUID>`: restituisce i documenti
  più recenti per il tenant, eventualmente filtrati per perizia;
  limite obbligatoriamente compreso fra **1 e 100** (default 50).
  Il risultato è ordinato per `createdAtUtc DESC, id DESC`.
  Questo contratto resta disponibile per i client precedenti.
- `GET /api/files/page?limit=50&periziaId=<UUID>&cursor=<opaque>`
  e MCP `page_documents(periziaId?,limit?,cursor?)` restituiscono
  `{items,nextCursor}` con **1..50** voci per pagina.
  PostgreSQL applica tenant, filtro e seek `created_at_utc DESC,id DESC`
  prima del cap, con una riga extra per determinare `nextCursor`.
  Un cursore `d1` canonico e limitato è legato al filtro Perizia
  da fingerprint SHA-256: input non valido o filtro diverso → 400 REST
  / errore MCP. Non è firmato né costituisce credenziale o
  snapshot multi-richiesta; JWT e FORCE RLS sono sempre obbligatori.
- `GET /api/files/{id}` e `get_document(id)`: restituiscono
  metadata di un documento; ID inesistenti e di altri tenant
  sono indistinguibili (404 REST, errore MCP).
- Il DTO contiene **solo**
  `id`, `periziaId`, `fileName`, `contentType`,
  `length`, `createdAtUtc`. Il repository proietta esplicitamente
  queste colonne, **senza** recuperare `storage_key`, URI interni,
  token, contenuti, artefatti scanner o chiavi di cifratura.
- Nessuna lettura genera un audit di *mutazione riuscita*.
  La lettura non concede il download dei byte: endpoint di download
  autorizzato, politiche di retention/delete, versioni, cartelle,
  indici/benchmark su archivi grandi e report rimangono aperti nella issue #11.

I test Business coprono read-only e write-only, filtro, cap,
not-found e scope negato; PostgreSQL reale verifica tenant diversi,
stesso UUID perizia in due tenant, pool di connessioni e RLS.
`tests/frontend/security.spec.ts` usa token Keycloak reali,
upload multipart, REST e MCP per testare scope, filtri e DTO
privi di chiavi storage. La risposta REST include
`Cache-Control: no-store` come tutte le API tenant-scoped.

## Lettura puntuale perizia senza escalation OAuth (#12/#9)

La route `GET /api/perizie/{id}` e il tool MCP
`get_perizia(id: UUID)` chiamano `PeriziaService.GetAsync`:
l'identità e il `tenant_id` provengono esclusivamente dal
JWT già validato, e `IPeriziaRepository.FindAsync` opera
nella stessa transazione tenant-scoped PostgreSQL FORCE RLS
delle altre letture. Non si accettano parametri tenant
provenienti dal client.

Sono ammessi token con `peritial.read` **oppure**
`peritial.write`; una richiesta anonima è 401, un
token senza scope autorizzato è 403. ID sconosciuti,
non validi come GUID oppure di tenant non visibile
non consentono di enumerare le pratiche: i GUID validi
non presenti producono **REST 404** e nel tool MCP
un risultato di errore, mai dati di altri tenant.

Le letture non modificano lo stato e non emettono un
audit mutativo `succeeded`. I test Business verificano
accesso read-only e write-only, riservatezza cross-tenant,
ID mancante e assenza di audit di successo;
`tests/frontend/security.spec.ts` esercita REST/MCP,
discovery del tool, Keycloak reale e permessi negativi.
La suite Infrastructure preesistente dimostra inoltre
l'isolamento di `FindAsync` tramite RLS su PostgreSQL reale.

## Assegnazione e avanzamento perizie via MCP (#12/#9)

Sono disponibili altri due tool MCP, con gli stessi use case delle route REST:

| Tool MCP | Argomenti JSON | Endpoint REST equivalente |
| --- | --- | --- |
| `assign_perizia` | `{"id":"UUID","assignee":"operatore"}` | `POST /api/perizie/{id}/assign` con `{"assignee":"operatore"}` |
| `advance_perizia` | `{"id":"UUID","target":"ToBeRevised"}` | `POST /api/perizie/{id}/advance` con `{"target":"ToBeRevised"}` |

`advance_perizia.target` accetta **solo** i nomi simbolici case-sensitive
`ToBeRevised`, `Revised`, `ToSend`, `Sent`, `Billed`; numeri,
nomi terminali come `Cancelled` e input ignoti sono rifiutati
**prima** di invocare il servizio. La state machine del dominio
controlla inoltre che il target sia **esattamente lo stato successivo**;
un nome corretto non autorizza salti di stato.

Entrambi richiedono token firmato con tenant+subject e scope
`peritial.write`; il token read-only può entrare in MCP ma non può
mutare, e la tool call ritorna `isError:true` con audit `denied`.
Per le invocazioni corrette, il repository PostgreSQL FORCE RLS
esegue update CAS e audit di successo nella stessa transazione
(ADR 0029). Conflitti o transizioni invalide non generano audit
`succeeded`. Il catalogo è provato via `tools/list`, mentre
`tests/frontend/security.spec.ts` verifica assegnazione, avanzamento,
read-only, target invalido, replay e stato REST con Keycloak demo;
la CI verifica anche le righe di audit in PostgreSQL.

**Limiti:** non sono implementate tutte le funzionalità del legacy,
né la discovery/versione del catalogo per client MCP esterni,
l'idempotenza generale o il supporto di upload firmati.
L'esito di errore MCP non è necessariamente un codice HTTP REST 409.

## Annullamento perizie REST/MCP (#9)

`POST /api/perizie/{id}/cancel` e `cancel_perizia(id: UUID)` invocano
lo **stesso** `PeriziaService.CancelAsync` e richiedono `peritial.write`
con tenant e subject validati dal JWT. Non accettano un tenant dal body.
Le perizie in stato `Billed`, `Duplicated` o `Cancelled` non possono
essere annullate; il replay su `Cancelled` viene rifiutato, non emette
un secondo evento di successo. Un ID assente o di altro tenant non è visibile
attraverso il repository protetto da PostgreSQL FORCE RLS.

Lo stato `Cancelled` e l'evento `perizia.cancel/succeeded` sono scritti
nella stessa transazione PostgreSQL. Per **tutte** le mutazioni di perizie
implementate (`assign`, `advance`, `cancel`) il repository confronta
in modo atomico lo stato e l'assegnatario persistiti al momento della lettura.
Se un'altra richiesta li ha cambiati, rifiuta la modifica prima dell'audit:
**REST 409 Conflict** oppure errore MCP, mai `succeeded` per il loser.
Un ID sconosciuto o di altro tenant resta 404 REST grazie a FORCE RLS.
Vedere [ADR 0029](../adr/0029-optimistic-perizia-state-cas.md) e
`docs/development.md` per i quattro test PostgreSQL RED/GREEN.

La coppia status+assignee non è un contatore monotono né un token
`If-Match` pubblico: transizioni inverse future potrebbero creare
rischi ABA e richiederanno un revision counter. Mancano ancora
idempotenza generale, UI di storico, prove multi-replica e equivalenza
con il branch storico `death`. #9/#21 restano aperte.

## E2E e compatibilità

I test `tests/frontend/authenticated.spec.ts` e `tests/frontend/security.spec.ts` verificano login PWA Authorization Code + PKCE, presenza claim `sub`, token Keycloak di sola lettura, senza tenant, senza scope e con audience errata, discovery e autorizzazioni REST/MCP contro Docker Compose, in modalità browser desktop/mobile. I client `peritial-test-*` nel realm Docker usano *solo per i test locali* grant password: non sono registrazioni da copiare in produzione.

Keycloak 26 richiede il client scope `basic` con mapper `oidc-sub-mapper` per il subject nel JWT; il realm demo lo definisce esplicitamente. In Azure Entra l'emissione dei claim firmati e l'interoperabilità con almeno due client MCP esterni devono ancora essere verificate nella issue #4. I log strutturati di audit delle mutazioni restano tra i gate di sicurezza aperti (#4/#19).

## Audit mutazioni

Tool MCP e API REST condividono eventi audit RLS append-only, per ogni mutazione e diniego di scope. Il database non espone queste righe tramite endpoint cliente. La CI full-stack verifica i record dopo Playwright. Consultare docs/audit.md per limiti di atomicità, retention e prerequisiti produttivi.

## Retry con chiave idempotente

Il tool `ingest_file` accetta `idempotencyKey` opzionale (16–128 ASCII consentiti). Ripetere lo stesso file, MIME, nome e periziaId con la stessa chiave e la stessa identità tenant/subject restituisce lo stesso UUID e non crea un audit duplicato; chiave pendente o dati diversi producono errore MCP. Per REST usare `Idempotency-Key`. Le richieste senza chiave rimangono non idempotenti e i tool mutativi non documentali non sono ancora coperti.

## Paginazione Sinistri MCP (#7/#12, ADR 0039)

`page_sinistri(cursor?:string, limit=50)` è un tool **sola lettura**
equivalente a `GET /api/sinistri/page`; richiede lo scope firmato
`peritial.read` o `peritial.write` e usa sempre il tenant JWT.
Restituisce `items` e `nextCursor` (null all'ultima pagina), max
50 elementi per chiamata. Il cursore è opaco/canonico e contiene soltanto
la chiave posizionale data evento+UUID: **non è un token di accesso**.
Cursori invalidi o limiti fuori 1–50 sono rifiutati. `list_sinistri`
e `search_sinistri` restano compatibili. La ricerca per prefisso viene
paginata da un tool distinto `search_sinistri_page` (ADR 0040).
Non esiste audit mutativo per queste letture.
[ADR 0039](../adr/0039-sinistri-keyset-pagination.md).

## Ricerca Sinistri paginata MCP (#7/#12, ADR 0040)

Il nuovo tool `search_sinistri_page(prefix, occurredFrom?, occurredTo?,
limit=50, cursor?)` replica `GET /api/sinistri/search/page`. In ogni richiesta
il backend valida l'identità OAuth e lo scope `peritial.read|write`,
normalizza il prefisso letterale (2–100 caratteri) e applica date evento
inclusive. La query PostgreSQL con FORCE RLS filtra tenant, prefisso
`ILIKE` con escaping di `%`, `_`, `!`, date e cursore **prima** del
limite (max 50 + lookahead). La risposta è `items,nextCursor`, senza
tenant ID. Il cursore di ricerca `s1` combina la posizione data/UUID
con un digest del prefisso/date; riutilizzarlo con filtri differenti
genera errore, ma non è una firma né un'autorizzazione.
Il tool `search_sinistri` resta invariato per l'autocomplete del picker
e continua a restituire un elenco limitato (default 10, max 50).
[ADR 0040](../adr/0040-sinistri-filtered-keyset-search.md).

## MCP delete_sinistro: V005 cancellazione autorizzata (#7/#12/#21)

`delete_sinistro(id:UUID,expectedVersion:long)` è un'operazione
mutativa che usa `SinistroService.DeleteAsync` in comune con
`DELETE /api/sinistri/{id}?expectedVersion=N`. Sono ora obbligatori
**entrambi** gli scope firmati `peritial.write` e
`peritial.sinistri.delete`, oltre al gate solo-Development
`Sinistri:EnableDevelopmentDelete=true`. In produzione il tool
è sempre disabilitato anche quando il flag è configurato: l'API
fallisce l'avvio per evitare abilitazioni accidentali. Il tool non
riceve tenant ID né permessi nel payload.
Ritorna `true` soltanto dopo che la cancellazione del caso **senza**
Perizie collegate e l'evento audit `sinistro.delete` sono stati
committati insieme. Il SQL CAS nega versioni obsolete e la FK composta
V004 `ON DELETE RESTRICT` nega casi con Perizie associate, senza
cascata. Record estranei sono indistinguibili da mancanti. Denied scope
viene auditato senza il corpo della richiesta. Il ruolo app riceve il
privilegio DELETE solo attraverso migrazione V005 amministrativa;
il ruolo recovery resta senza accesso. Restano da definire retention
e legal hold prima di abilitare una UI di cancellazione.
[ADR 0041](../adr/0041-sinistri-v005-audited-cas-delete.md).

## Evoluzione ADR 0042 — gate fall-closed per DELETE Sinistri (#22)

La modifica di #22 introduce un permesso OAuth **non assegnato**
ai client web e ai writer normali: `peritial.sinistri.delete` è
necessario **in aggiunta** a `peritial.write`. Il Business controlla
gli scope firmati e il gate immutabile prima della lettura del Sinistro,
indipendentemente dal trasporto REST o MCP. Mancanza di uno dei
permessi o gate disabilitato restituisce diniego, con audit
`sinistro.delete/denied` e `resource_id = NULL`.
`Sinistri:EnableDevelopmentDelete` è falso per default; può essere
abilitato soltanto in Development per la fixture E2E separata.
La configurazione true in ambienti non-Development arresta l'API
senza aprire l'endpoint di cancellazione. Non esiste ancora una
modalità abilitabile in produzione: mancano la policy retention/legal
hold, le approvazioni e i test legali dell'issue #22.
[ADR 0042](../adr/0042-sinistri-delete-development-gate.md).

## Unlink esplicito Perizia–Sinistro (#7/#9/#12/#21, ADR 0043)

Il nuovo tool `unlink_perizia_sinistro(periziaId,expectedSinistroId)`
condivide la Business API con `DELETE /api/perizie/{id}/sinistro?expectedSinistroId=UUID`.
Richiede lo scope firmato `peritial.write`, mai un tenantId dal client.
`expectedSinistroId` **non** è una password né il nuovo target:
è il link corrente che l'utente ha letto in precedenza; in PostgreSQL
si esegue `UPDATE perizie SET sinistro_id=NULL WHERE tenant_id=@tenant
AND id=@perizia AND sinistro_id=@expected`, prima dell'audit
`perizia.unlink-sinistro/succeeded` nello stesso commit.
Assente/foreign → 404 indistinguibile; link nullo o cambiato → 409,
nessun successo audit. Il tool ritorna
`{periziaId,sinistroId:null}` quando il commit è riuscito;
REST ritorna 204. Se è necessaria una nuova associazione,
occorre una distinta chiamata esplicita `link_perizia_sinistro`;
nessun reparent implicito o cambiamento di Sinistro/Perizia.
Non modifica il gate V005 hard-delete (#22).
[ADR 0043](../adr/0043-perizia-sinistro-explicit-unlink.md).

## MCP reparent_perizia_sinistro (#7/#9/#12, ADR 0044)

`reparent_perizia_sinistro(periziaId,expectedSinistroId,newSinistroId)`
è l'equivalente MCP del REST `PUT /api/perizie/{id}/sinistro`.
Una Perizia attualmente linked viene **riposizionata con un
singolo UPDATE CAS** (mai unlink+link) soltanto con scope
OAuth `peritial.write`. Il nuovo Sinistro deve esistere nello
stesso tenant firmato, differire dall'attuale e la Perizia
non deve essere `Billed`, `Duplicated` o `Cancelled`.
404 nasconde missing/foreign, 409 indica source cambiata,
link nullo o stato terminale, 400 input non validi.
La risposta di successo `PeriziaSinistroLinkDto` espone
solo `periziaId,sinistroId`; l'audit
`perizia.reparent-sinistro/succeeded` viene committato
insieme alla mutazione. Un normale `peritial.write` è
sufficiente per questo flusso **non distruttivo**;
non conferisce mai scope `peritial.sinistri.delete`,
che resta bloccato in produzione dalla #22.
[ADR 0044](../adr/0044-perizia-sinistro-atomic-reparent.md).

## Aggiornamento V006 del catalogo associazioni (#23)

I tool `link_perizia_sinistro`,
`unlink_perizia_sinistro` e
`reparent_perizia_sinistro` **non accettano più chiamate
prive di `expectedLinkVersion`**. Il tool di lettura
`get_perizia_sinistro` restituisce
`{periziaId,sinistroId,linkVersion}`; i successi di
link/reparent tornano con la nuova revisione e la
risposta unlink con `sinistroId:null,linkVersion`.
Usare esclusivamente la revisione effettivamente
osservata; una revisione vecchia dà conflitto anche se
il Sinistro corrente ha di nuovo lo stesso UUID.
Il client con argomenti obsoleti riceve errore MCP
invece di un replay silenzioso. Ogni successo è
tracciato da audit+history atomici con source/target,
ma non espone il contenuto della tabella di provenienza.
[ADR 0045](../adr/0045-perizia-sinistro-v006-aba-revision-provenance.md).

## Tool preview legacy XML (#10)

`preview_legacy_xml(xml: string)` usa
`LegacyXmlPreviewService`, lo stesso servizio di
`POST /api/imports/xml/preview`. Richiede JWT firmato
con `peritial.write`, indipendentemente dall'accesso
MCP generico. I byte UTF-8 non possono superare 1 MiB.
Riconosce Classic, Classic Base e Accertatore,
rifiutando XML malformato, namespace non previsti,
DTD/XXE, duplicati, riferimenti/date mancanti e
date non canoniche.

Il tool **non importa** e non tocca DB, S3 o audit.
La risposta è solo `format`, `externalReference`,
`occurredOn`, `reportedOn`, `attachmentCount`
e `hasUnmappedBusinessData` (booleano):
non sono restituiti allegati, nomi, altri dati personali,
chiavi di storage o file bytes. La preview **non esegue**
un controllo antivirus e non abilita l'importazione
effettiva. Per fare ingestion servono separati workflow
tenant-scoped, idempotenza, audit atomico e scanner.
[ADR 0051](../adr/0051-legacy-xml-safe-preview.md).

## Piano di import XML senza commit (#10)

MCP `plan_legacy_xml(xml)` condivide
`LegacyXmlImportPlanService` con
`POST /api/imports/xml/plan`. JWT firmato
con `peritial.write`, massimo 1 MiB di XML
validato via preview; ricerca PostgreSQL di soli
ID/date nel **tenant dell'attore** per riferimento
**esattamente uguale** e `LIMIT 2`.
Restituisce `{preview,status,existingSinistroId}`.
Gli stati possibili sono `new`, `existing`,
`review-required`, `conflict`, `ambiguous`.
`review-required` indica che esiste un solo
riferimento a date compatibili, ma l'XML contiene
dati non ancora mappabili alla identità storica
compagnia/polizza/anno; **nessun UUID è restituito**.
La presenza di altri campi viene comunicata solo
dal booleano `preview.hasUnmappedBusinessData`,
mai tramite nomi o dati assicurativi.
Solo `existing` per XML minimale
include l'UUID del Sinistro univoco compatibile;
nessun ID per gli altri stati. Non è un token,
una riserva, né un comando d'importazione:
non crea, aggiorna, collega o elimina record
e non tocca documenti, storage, audit di successo.
[ADR 0052](../adr/0052-xml-legacy-reconciliation-dryrun.md),
[ADR 0055](../adr/0055-legacy-xml-identity-review-gate.md).

## Precisione temporale e metadati XML nascosti (#25)

`preview_legacy_xml(xml)` continua a leggere
date con formato `yyyy-MM-dd HH:mm:ss` ma
espone **solo** `occurredOn/reportedOn`
come giorni e `hasUnmappedBusinessData=true`
per indicare l'informazione oraria non persistibile
nel modello attuale. Lo stesso flag è `true`
per commenti XML o processing instructions presenti
nel documento, il cui contenuto resta privato.
`plan_legacy_xml(xml)` restituisce
`review-required` senza UUID con un unico
riferimento e date compatibili, anziché
`existing`; non trasforma una data tronca
in prova d'identità. L'API REST espone lo
stesso comportamento. Gli import XML effettivi
restano non disponibili fino a schema,
convenzioni timezone e provenance verificati.
[ADR 0056](../adr/0056-legacy-xml-temporal-precision-fail-closed.md).

## Riferimenti normalizzati e campi XML vuoti (#25)

Nei tool `preview_legacy_xml` e `plan_legacy_xml`,
un `NumeroSinistro` con spazi interni ai delimitatori
XML (ma esterni al valore canonico), una data delimitata
da spazi o un `DataIncarico` presente ma vuoto producono
`preview.hasUnmappedBusinessData=true`. Il planner
non divulga l'ID di un candidato unico con quei
valori: risponde `review-required`, esattamente
come REST. L'indentazione **tra** tag non altera
lo stato di un documento altrimenti minimale.
La preview può rendere il numero/data normalizzati,
ma il commit interno dell'import è fail-closed.
[ADR 0057](../adr/0057-xml-source-scalar-normalization-review.md).

## CDATA e riferimenti XML decodificati (#25)

`preview_legacy_xml` e `plan_legacy_xml`
non considerano equivalenti, per una futura
scrittura, un riferimento scritto in testo
semplice e uno che utilizza `<![CDATA[...]]>`
o `&#...;`. La preview rende i **soli valori
decodificati già ammessi dal contratto** e
`hasUnmappedBusinessData=true`; per l'unico
candidato compatibile, il planner restituisce
`review-required` e nessun UUID, anche via REST.
Non sono restituiti il markup originale né dati
aggiuntivi. Il commit XML resta interno e bloccato
per questi input. [ADR 0058]
(../adr/0058-xml-character-references-cdata-fail-closed.md).

## Snapshot MCP v1, ABI e controlli di regressione (#12)

Il contratto pubblico iniziale è
[`mcp-catalog.v1.json`](mcp-catalog.v1.json),
`format=1` e `version=1.0.0`. Elenca esattamente
i 25 tool pubblicati in `McpTools.cs`, ordinati
per nome. Ogni voce dichiara `required`
(parametri MCP obbligatori), `access`
(`read`, `write`, `write+delete`) ed
`effect` (`read-only` oppure `mutating`).
I tool di preview/plan XML sono `read-only`
**ma richiedono `peritial.write`**; una chiamata
a `delete_sinistro` richiede `peritial.write`
e `peritial.sinistri.delete` e resta soggetta
al gate esplicito che ne impedisce l'utilizzo in
produzione.

Il job `supply-chain-actions` esegue
`tests/security/test_mcp_catalog_contract.py`:
estrae nomi e firme C# dei tool, confronta
l'elenco e gli argomenti obbligatori con
lo snapshot, blocca nomi duplicati, tool con
firme non riconosciute e categorie di rischio
alterate. Include test negativi per rename,
rimozione di `expectedVersion` e cambio
di classificazione di `delete_sinistro`.
Lo snapshot va aggiornato consapevolmente
con ogni modifica di catalogo; non equivale
a dimostrare a runtime i permessi.

Un'ulteriore prova Playwright usa i **veri
JWT Keycloak read-only e write** per interrogare
`POST /mcp`, metodo `tools/list` e confronta
nomi e `inputSchema.required` prodotti
dall'SDK, con verifica delle proprietà
obbligatorie. Non sono effettuate mutazioni
per catalogare i tool. Le prove già esistenti
restano autoritative per l'effettivo enforcement
di scope, tenant e audit.
[ADR 0059](../adr/0059-versioned-mcp-tool-contract-gate.md).

Non sono ancora implementati negotiation
multi-versione, client MCP esterni reali,
compatibilità semantica di ogni schema JSON
o idempotenza per tutti i tool mutativi.
L'issue #12 rimane aperta.

## Contratto catalogo v1.1: tutti i parametri (#12)

Il manifesto `mcp-catalog.v1.json` ora ha
`version=1.1.0`, stessa serie di 25 tool e
55 parametri pubblici. La sezione `parameters`
di ogni tool è una mappa ordinata per
firma C#, con per-parametro:

- `type`: `string`, `uuid`, `date` o `integer`.
  Gli UUID e le date hanno wire type JSON `string`,
  mentre `int` e `long` usano `integer`.
- `optional`: un valore default C# rende il
  parametro omettibile; non implica che sia
  nullable.
- `nullable`: deriva da `?` C#,
  distinto dall'omissibilità.

La lista `required` precedente rimane per
compatibilità con il controllo introdotto
nell'ADR 0059. I `CancellationToken` di
servizio non sono argomenti MCP, dunque non
appaiono nel manifesto.

Il gate `scripts/ci/mcp_catalog_contract.py`
verifica **nomi, ordine, presenza e dettagli
di tutti i parametri** contro il sorgente C#.
La suite ha 12 test, inclusi regressioni
avversariali su parametro opzionale rimosso
o rinominato, `DateOnly`/UUID/`long` mutati,
nullabilità e default che cambiano. Il test
Keycloak E2E verifica la presenza esatta delle
proprietà JSON (anche opzionali), i required,
e il tipo primario esposto da
`tools/list` per i client firmati read/write.
Non impone oggi un JSON Schema completo per
nullable/default/formati: le varianti dello
SDK sono verificate per il tipo primario
anziché per la forma del combinatore JSON.

**Non** è una modifica degli strumenti o
della loro semantica: gli scope applicati
nel Business, RLS, CAS e audit restano
invariati. Non è ancora una negotiation
multi-versione per client remoti.
[ADR 0060](../adr/0060-mcp-v1-1-full-parameter-contract.md).

### Default letterali del contratto v1.1

Ogni voce in `parameters` include anche
`defaultLiteral`: `null` (JSON) per gli
argomenti obbligatori che non hanno default,
oppure una stringa contenente l'espressione
C# esplicita (`"null"`, `"50"`, `"10"`).
Il gate statico impedisce di sostituire
`limit=50` con `limit=100` senza una
revisione coordinata del manifesto.
Non equivale a verificare la keyword
`default` nello schema MCP generato
dall'SDK, che non è garantita né richiesta
dalla convalida live.
[ADR 0060](../adr/0060-mcp-v1-1-full-parameter-contract.md).

## Catalogo MCP v1.2: firme dei risultati C# (#12)

Il manifesto `mcp-catalog.v1.json` è ora `version=1.2.0`, con
`format=1`, gli stessi **25 tool** e **55 parametri**. Il nuovo
`returnType` obbligatorio stabilizza il **tipo nominale C#** del
risultato, inclusi `Task<T>`, `IReadOnlyList<T>` e metodi sincroni.
Il gate `scripts/ci/mcp_catalog_contract.py` lo confronta direttamente
con `McpTools.cs`, anche quando la firma di input non cambia.
Quattro regressioni proteggono tipi scalari, liste annidate, risultati
sincroni e manifesti alterati.

Non cambia alcun endpoint, runtime MCP, DTO, permesso o database.
Il tipo C# **non** equivale al JSON `outputSchema` né blocca le
proprietà interne dei DTO o il serializer SDK. Il version negotiation
per client esterni e la compatibilità degli output JSON restano aperti
in #12. [ADR 0061](../adr/0061-mcp-v1-2-return-signature-gate.md).

## Manifesto DTO MCP di uscita v1.0 (#12)

Il controllo `docs/mcp-output-dtos.v1.json` elenca i **10 record DTO**
raggiungibili dai 25 ritorni dei tool MCP del catalogo 1.2.0 (incluse
le collezioni e i record annidati). La suite Python
`tests/security/test_mcp_catalog_contract.py` impedisce di
omettere un DTO raggiungibile, introdurne uno non revisionato o
disallineare la versione del catalogo. In aggiunta, il test xUnit
`McpOutputDtoContractTests` eseguito nel job backend usa reflection
sui tipi .NET reali: qualsiasi campo pubblico aggiunto o rimosso,
tipo cambiato o variazione di nullabilità C# interrompe il gate,
anche se il metodo MCP conserva lo stesso `returnType`.
Sono inclusi controlli avversariali su campi extra e nullabilità.

Questo è un **contratto del modello CLR**, non una promessa
di equivalenza con `outputSchema` JSON generato dall'SDK,
né una verifica del nome JSON effettivo delle proprietà.
Le conversioni JSON, i DTO con logica runtime e l'interoperabilità
fra versioni rimangono in #12; nessuna release produttiva è abilitata.
[ADR 0062](../adr/0062-mcp-public-output-dto-contract.md).

## Prova `tools/call` JSON reale e vincolo dei campi (#12)

La tranche [ADR 0063](../adr/0063-mcp-live-wire-json-allowlist.md)
introduce un test Playwright autonomo: crea dati sintetici
perizia/sinistro/documento con un utente writer Keycloak, poi
chiama con il token **read-only** i tool
`get_perizia`, `get_sinistro`, `get_perizia_sinistro`,
`search_sinistri_page`, `get_document` e
`page_documents`. Il test confronta il **JSON effettivamente
restituito via `tools/call`** con i campi del manifesto
`mcp-output-dtos.v1.json`: nomi lower-camel-case,
proprietà allowlisted con **campi non-nullable obbligatori**,
campi nullable omissibili (l'SDK omette le proprietà `null`),
tipi, UUID/DateOnly/DateTime e
liste annidate. I test negativi contro aggiunte di campi,
drift di tipi e date non ISO girano anche nella CI frontend
senza Compose.

È una protezione per il solo campione di **sei tool di lettura**,
non una certificazione completa di `outputSchema` SDK per tutti
i tool o del version negotiation con client esterni.
La #12 resta aperta.

## Copertura MCP wire read-only completa (#12)

La [ADR 0064](../adr/0064-mcp-all-read-tools-wire-output.md)
estende il test Playwright Keycloak autenticato `mcp-wire-output.spec.ts`
da sei a **tutti i 12 tool MCP con classificazione `access:read`**.
Il test confronta l'insieme dei tool `read` nel catalogo v1.2
con l'insieme effettivamente esercitato: un nuovo tool di lettura
non può essere aggiunto senza aggiornare anche la prova wire.

I tipi effettivi `returnType` sono derivati **dal catalogo**,
non passati arbitrariamente dal test, e le risposte sono validate
contro il manifesto dei 10 DTO, includendo liste di DTO,
risultati paginati e `IReadOnlyList<Guid>` non vuota.
Le prove si svolgono sotto JWT Keycloak `peritial.read`.
Solo i dati sintetici di fixture vengono creati/collegati
tramite REST con token writer, prima della lettura.
La CI frontend verifica anche le regressioni del validatore
in assenza di Compose.

Restano fuori dal perimetro wire i tool mutatori e i due
tool di preflight XML non mutanti ma **write-scoped**
(`preview_legacy_xml`, `plan_legacy_xml`).
Non è ancora un test JSON `outputSchema` di tutti i 25 tool
né una certificazione di compatibilità con client esterni.

## Preflight XML: output MCP write-scoped (#12)

L'[ADR 0065](../adr/0065-mcp-xml-preflight-wire-contract.md)
aggiunge al gate Playwright `tools/call` i due tool XML
`preview_legacy_xml` e `plan_legacy_xml` (entrambi
`effect:read-only` ma `access:write`). Con JWT Keycloak
`peritial.write` vengono testati XML sintetici Classic e
Accertatore: controllo JSON dei DTO pubblici, assenza di dati
privati/orari legacy e nessun sinistro creato prima o dopo il
preflight. Un JWT `peritial.read` deve essere rifiutato.
Un controllo statico impedisce di aggiungere preflight write-scoped
senza copertura. Il totale dei tool con prove wire sale a
**14/25**. Rimangono 11 mutatori, version negotiation e
`outputSchema` SDK formale; nessun import XML viene abilitato.

## MCP: output JSON dei primi mutatori su JWT writer (#12)

L'[ADR 0066](../adr/0066-mcp-perizie-sinistri-mutator-wire-contract.md)
estende il gate live `tools/call` a sei tool di mutazione:
`create_perizia`, `assign_perizia`, `advance_perizia`,
`cancel_perizia`, `create_sinistro` e `update_sinistro`.
I valori JSON reali vengono verificati contro il `returnType`
del catalogo e l'allowlist del manifesto DTO; lo stesso stato
viene letto successivamente tramite REST autenticato in modo
indipendente. In particolare, la modifica Sinistro richiede
`expectedVersion` e il test verifica l'incremento da 1 a 2.

Il test esegue inoltre tutti i sei mutatori con JWT
**`peritial.read`** e verifica `isError:true`, assenza di
nuovi riferimenti e assenza di modifiche di stato.
Un controllo statico obbliga a classificare esplicitamente
tutti gli **11 tool mutatori** tra sei già coperti e cinque
deferiti. Questi cinque (`delete_sinistro`, `ingest_file`,
`link_perizia_sinistro`, `reparent_perizia_sinistro`,
`unlink_perizia_sinistro`) restano fuori dalla prova JSON live.

Con questa tranche la copertura degli output osservati sale a
**20/25 tool** (12 read, 2 preflight, 6 mutatori), senza
modificare alcuna firma MCP, modello Business o policy.
Version negotiation, outputSchema SDK e gli altri cinque
mutatori restano aperti in #12.

## MCP: CAS Perizia–Sinistro, ingestion e hard-delete negata (#12)

L'[ADR 0067](../adr/0067-mcp-link-ingest-wire-and-delete-denial.md)
introduce prove live sui quattro restanti tool MCP con output
di mutazione abilitato: `link_perizia_sinistro`,
`reparent_perizia_sinistro`, `unlink_perizia_sinistro`
e `ingest_file`. Il test verifica il JSON effettivo
`tools/call` contro l'allowlist v1.0, la versione monotona
di associazione **1→2→3→4**, la persistenza REST e il
rifiuto di una versione obsoleta (ABA).

Il file PDF sintetico viene realmente scansionato da ClamAV,
archiviato in S3 e letto come metadati REST, senza
esporre storage key. L'`idempotencyKey` viene ripresentata
con payload identico e deve restituire il medesimo documento,
non creare un duplicato. Un JWT `peritial.read` deve essere
respinto da tutte e quattro le mutazioni, senza
cambiare l'associazione.

Per `delete_sinistro` il gate **resta chiuso**: con
ordinario token writer la risposta MCP deve essere errore
e il sinistro deve continuare a esistere. Non si invoca il
percorso successivo al gate, né si abilita la cancellazione
produttiva (#22). Tutti e 25 i tool sono ora **classificati
nei test**; 24 hanno una prova di output di successo,
uno (`delete_sinistro`) soltanto una prova di rifiuto.
Version negotiation, `outputSchema` SDK e client esterni
restano aperti nella #12.

## Metadati di rischio per client MCP (ADR 0068, #12)

Il manifesto revisionato `docs/mcp-tool-annotations.v1.json`
fissa gli hint MCP `readOnlyHint`, `destructiveHint`,
`openWorldHint` per **tutti e 25 i tool**; i valori sono
esposti realmente dall'SDK C# 2.0 tramite `tools/list`.
I **14 tool non mutanti**, inclusi i due preflight XML
`peritial.write`, riportano `readOnlyHint:true`.
Otto mutatori che cambiano o possono eliminare stato
riportano `destructiveHint:true`; creazione pratica,
creazione sinistro e ingestion documentale sono additive
(`destructiveHint:false`). Tutti operano entro i domini
tenant autenticati, quindi `openWorldHint:false`.
Non viene dichiarata idempotenza generalizzata.

La suite Python protegge il manifesto e gli attributi
C# contro drift, omissioni e riclassificazioni non
revisionate. La CI Playwright usa i veri token Keycloak
reader e writer e confronta tutti gli hint sul wire
`tools/list`. Il tool `delete_sinistro` resta
**distruttivo e non autorizzato** in mancanza del gate
produttivo (#22). Gli hint servono alle interfacce
client per presentare avvisi e consenso, **non
sostituiscono OAuth, FORCE RLS o controlli Business**.

[ADR 0068](../adr/0068-mcp-tool-risk-annotations.md).
`outputSchema` strutturati, negoziazione versioni e
client esterni restano in #12.

## Version negotiation HTTP stateless MCP 2.0 (#12, #4)

La [ADR 0069](../adr/0069-mcp-modern-legacy-wire-compatibility.md)
fissa esplicitamente `HttpServerTransportOptions.Stateless = true`
nell'API .NET 10 e introduce
`docs/mcp-protocol-eras.v1.json`: una policy revisionata
per **MCP 2026-07-28 moderno** e **2025-11-25 legacy**.
Non sono creati session ID server, e ogni POST autenticato
rivalida JWT, tenant e scope separatamente.

La suite Playwright `mcp-protocol-eras.spec.ts`
esercita il vero endpoint HTTP con JWT Keycloak:
`server/discover` + `tools/list` + `tools/call`
per la modalità moderna con header `MCP-Protocol-Version`
e `Mcp-Method`, e `initialize` + `tools/list`
per il client legacy. Entrambe devono pubblicare
i 25 tool identici, senza sessioni sticky e senza
tenantId nelle risposte.

I test negativi inviano `tools/call` mutativo con
una versione non supportata (codice `-32022`) o
header/metadata di protocollo incoerenti (`-32020`).
Devono essere respinti **prima del dispatch**, senza
creare una Perizia, e una chiamata moderna anonima
deve continuare a essere `401`.
Questo non equivale ancora a test con client
esterni con SDK diversi, né a un contratto completo
`outputSchema`, che restano aperti nella #12.

## MCP structured output per due tool di dettaglio (#12)

L'[ADR 0070](../adr/0070-mcp-structured-detail-outputs.md)
abilita in modo graduale `UseStructuredContent = true` per
`get_perizia` e `get_sinistro`. Su protocollo MCP
moderno 2026-07-28 questi tool annunciano un
`outputSchema` e restituiscono `structuredContent`
con lo stesso DTO della lettura REST firmata, senza
campi tenant o chiavi storage. Il manifesto
`mcp-structured-outputs.v1.json` fissa il set dei tool
abilitati ed è controllato staticamente e con Keycloak in CI.

Gli altri 23 tool conservano il comportamento
precedente; non vengono modificati gli scope, il
modello dati o la cancellazione Sinistri. L'estensione
a tutto il catalogo e ai client SDK esterni resta
da completare nell'issue #12.
