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
| Sinistri, anagrafiche, stati, import XML, documenti, report, messaggi, contabilità, KPI | tool dedicati per ciascun use case | iterazioni successive |

Per file grandi: non trasportare payload di gigabyte via JSON MCP; fornire un tool per ottenere URL di upload firmati/scoped e una procedura di completamento (da implementare). Una tool call MCP non equivale ad accesso indiscriminato al filesystem.

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
