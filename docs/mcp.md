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
| Perizie | list_perizie, create_perizia | vertical slice 1 |
| Documenti | ingest_file (base64 limitato); REST multipart per grandi file | vertical slice 1 |
| Sinistri, anagrafiche, stati, import XML, documenti, report, messaggi, contabilità, KPI | tool dedicati per ciascun use case | iterazioni successive |

Per file grandi: non trasportare payload di gigabyte via JSON MCP; fornire un tool per ottenere URL di upload firmati/scoped e una procedura di completamento (da implementare). Una tool call MCP non equivale ad accesso indiscriminato al filesystem.

## E2E e compatibilità

I test `tests/frontend/authenticated.spec.ts` e `tests/frontend/security.spec.ts` verificano login PWA Authorization Code + PKCE, presenza claim `sub`, token Keycloak di sola lettura, senza tenant, senza scope e con audience errata, discovery e autorizzazioni REST/MCP contro Docker Compose, in modalità browser desktop/mobile. I client `peritial-test-*` nel realm Docker usano *solo per i test locali* grant password: non sono registrazioni da copiare in produzione.

Keycloak 26 richiede il client scope `basic` con mapper `oidc-sub-mapper` per il subject nel JWT; il realm demo lo definisce esplicitamente. In Azure Entra l'emissione dei claim firmati e l'interoperabilità con almeno due client MCP esterni devono ancora essere verificate nella issue #4. I log strutturati di audit delle mutazioni restano tra i gate di sicurezza aperti (#4/#19).
