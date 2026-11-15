# Architettura

## Confini
```
React PWA --OIDC PKCE--> Entra ID / Keycloak
    | access token Bearer
    v
ASP.NET Core Applications (.NET 10) ---- MCP Streamable HTTP (/mcp)
    |                                (stessa autenticazione/tenant/ruoli)
    v
Business (use cases) --> Domains (perizie / stati / invarianti)
    |
    v
Infrastructures (repository PostgreSQL; object store Azure Blob o MinIO)
```

- Un dominio: gestione di sinistri e perizie; codice organizzato per responsabilità.
- Nessuna dipendenza da Azure nel dominio o negli use case.
- PostgreSQL come database comune per evitare divergenze nelle query e nelle migrazioni; provider di storage sostituibili.
- Tenant ID derivato **esclusivamente** da JWT validato (`tenant_id` in ambito on-prem, oppure claim mappato ed emesso dal provider aziendale). Mai fidarsi di un header `X-Tenant` o di un parametro MCP.
- Ogni query, mutazione, nome di oggetto, indice unico e audit è tenant scoped; il database usa anche Row Level Security come difesa in profondità (da integrare e testare prima della produzione).
- OIDC e JWT Bearer con HTTPS, controllo issuer/audience/firma/scadenza, scope e ruoli; nessuna autenticazione anonima su dati e MCP.
- Le risorse Azure hanno secret gestiti con Key Vault/Managed Identity prima del rollout; sviluppo locale con credenziali effimere di esempio.
- REST e MCP condividono Business: nessun secondo percorso privilegiato per i tool.
- Le operazioni irreversibili o sensibili richiedono autorizzazioni specifiche, audit e idempotenza.

## Punti di attenzione
Il branch `death` è una fonte di osservazione, non di codice. Identificare e specificare i comportamenti prima della reimplementazione. `main` sarà avviato con snapshot pulito.
