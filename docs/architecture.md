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
Infrastructures (repository PostgreSQL; object store Azure Blob o S3 compatibile (SeaweedFS))
```

- Un dominio: gestione di sinistri e perizie; codice organizzato per responsabilità.
- Nessuna dipendenza da Azure nel dominio o negli use case.
- PostgreSQL come database comune per evitare divergenze nelle query e nelle migrazioni; provider di storage sostituibili.
- Tenant ID derivato **esclusivamente** da JWT validato (`tenant_id` in ambito on-prem, oppure claim mappato ed emesso dal provider aziendale). Mai fidarsi di un header `X-Tenant` o di un parametro MCP.
- Ogni query, mutazione, nome di oggetto, indice unico e audit è tenant scoped; il database applica policy PostgreSQL FORCE RLS con un ruolo non privilegiato e contesto tenant locale alla transazione. Il tenant continua a provenire dal JWT firmato; vedere ADR 0006.
- OIDC e JWT Bearer con HTTPS, controllo issuer/audience/firma/scadenza, claim di tenant e soggetto e policy scope a livello REST/MCP, con autorizzazioni aggiuntive negli use case. Vedere ADR 0007; nessuna autenticazione anonima su dati e MCP.
- Le risorse Azure hanno secret gestiti con Key Vault/Managed Identity prima del rollout; sviluppo locale con credenziali effimere di esempio.
- REST e MCP condividono Business: nessun secondo percorso privilegiato per i tool.
- Le operazioni irreversibili o sensibili richiedono autorizzazioni specifiche, audit e idempotenza.

## Punti di attenzione
Il branch `death` è una fonte di osservazione, non di codice. Identificare e specificare i comportamenti prima della reimplementazione. `main` sarà avviato con snapshot pulito.

- Registro audit persistente security_audit: ruolo applicativo INSERT-only con FORCE RLS, accesso amministrativo separato per ispezione, nessuna API pubblica. Perizie e metadata documentali sono committati insieme all'audit del successo, con una sola TenantDbScope transazionale (ADR 0008). La consistenza con lo storage S3/Azure Blob esterno, l'idempotenza e la retention certificata restano da implementare (#5/#6/#19/#21).

- Recovery degli upload con `upload_intents` (tenant-scoped, FORCE RLS). Il commit `documents` + `security_audit` consuma l'intento in un'unica transazione. Un `PgUploadReconciler` in background con ruolo `peritial_recovery` distinto, senza BYPASSRLS e senza privilegi sui nomi file/audit, usa `FOR UPDATE SKIP LOCKED` per recuperare chiavi scadute e verifica prima l'assenza di referenze. ADR 0011; restano da provare idempotenza, kill/restart e provisioning cloud (#21).

- Gli upload REST e MCP condividono un registro `upload_request_keys` tenant/subject-scoped: SHA-256 di key/soggetto e fingerprint del contenuto canonico, unique index e `ON CONFLICT DO NOTHING` per la concorrenza; completamento atomico con document+audit; il worker libera richieste abbandonate. ADR 0012 / #21.
