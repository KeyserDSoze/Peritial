# MCP come superficie applicativa

## Obiettivo
Esporre le funzionalità tramite Model Context Protocol (SDK ufficiale .NET, HTTP stateless), raggiungibile via `/mcp`. Ogni tool invoca i **medesimi** use case dell'API REST e riceve il tenant esclusivamente dal JWT validato.

## OAuth
- Authorization Code + PKCE per i client interattivi; access token Bearer con audience dell'API e scope `peritial.read`/`peritial.write`.
- API/MCP sono OAuth **protected resource**: autorizzazione delegated al provider OIDC configurato, Entra in Azure o Keycloak on-prem.
- Discovery metadata `/.well-known/oauth-protected-resource` e challenge Bearer secondo RFC 9728/6750. I client devono poter scoprire l'authorization server; issuer/audience/scopes configurati.
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

Il test in `tests/frontend/authenticated.spec.ts` verifica il token OAuth reale, l'autorizzazione MCP e il catalogo tool con il protocollo Streamable HTTP, contro l'intera stack Docker Compose. Questo non sostituisce test con più client MCP esterni e più tenant.
