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
| Sinistri e collegamenti perizie | list_sinistri, search_sinistri, get_sinistro, create_sinistro, update_sinistro, link_perizia_sinistro, list_perizie_for_sinistro, get_perizia_sinistro | list/search/read/create/update Sinistri; relazione opzionale FK tenant-scoped e audit atomico, senza reparent/delete/CRUD Sinistri UI completo (#7/#9/#12/#13) |
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

Le route `GET /api/files`, `GET /api/files/{id}` e i tool
MCP `list_documents(periziaId?, limit?)`, `get_document(id)`
usano lo stesso `DocumentCatalogService` di sola lettura.
Richiedono un JWT firmato con tenant/subject e scope
`peritial.read` **oppure** `peritial.write`.
L'identità del tenant **non** è accettata dal client: ogni
query passa per `TenantDbScope` con FORCE RLS PostgreSQL.

- `GET /api/files?limit=50&periziaId=<UUID>`: restituisce i documenti
  più recenti per il tenant, eventualmente filtrati per perizia;
  limite obbligatoriamente compreso fra **1 e 100** (default 50).
  Il risultato è ordinato per `createdAtUtc DESC, id DESC`.
  Fino all'introduzione del cursor non è disponibile la lettura
  dell'intero storico oltre il limite massimo.
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
  paginazione cursor e report rimangono aperti nella issue #11.

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
e `search_sinistri` restano compatibili; la ricerca per prefisso non
dispone ancora di cursore. Non esiste audit mutativo per questa lettura.
[ADR 0039](../adr/0039-sinistri-keyset-pagination.md).
