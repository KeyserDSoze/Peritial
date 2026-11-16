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
| Perizie | list_perizie, create_perizia, cancel_perizia | creazione/lettura + annullamento auditato (#9) |
| Documenti | ingest_file (base64 limitato); REST multipart per grandi file | vertical slice 1 |
| Sinistri, anagrafiche, stati, import XML, documenti, report, messaggi, contabilità, KPI | tool dedicati per ciascun use case | iterazioni successive |

Per file grandi: non trasportare payload di gigabyte via JSON MCP; fornire un tool per ottenere URL di upload firmati/scoped e una procedura di completamento (da implementare). Una tool call MCP non equivale ad accesso indiscriminato al filesystem.

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
