# Azure PaaS — IaC iniziale

Terraform in infra/ prepara: Azure Resource Group, VNet, subnet separate per Container Apps e PostgreSQL, PostgreSQL Flexible Server privato, Azure Blob Storage, Managed Identity con RBAC, Log Analytics, due Azure Container Apps (API/MCP e frontend).

Non usare immagini né credenziali del Compose locale in Azure.

## Percorso di rilascio previsto
1. Pubblicare immagini OCI API/frontend in un registry raggiungibile da Azure. Il frontend deve essere compilato con issuer OIDC corretto (runtime-config è ancora da implementare).
2. Provisionare Entra ID: app registration, scope peritial.read/peritial.write, audience e tenant_id firmato.
3. Configurare backend Terraform remoto cifrato e segreti da fonte protetta, non da file versionati.
4. terraform -chdir=infra init && terraform -chdir=infra plan && terraform -chdir=infra apply.
5. Applicare `scripts/db/001_init.sql` come amministratore tramite runner/job su VNet privata. Il ruolo `peritial_app` nasce NOLOGIN e senza privilegi elevati: abilitarlo con una credenziale generata e gestita separatamente, senza BYPASSRLS, prima di attivare l'API. Il container usa `postgres_app_login` e `postgres_app_password`, mai le credenziali amministrative. L'automazione delle migrazioni e del provisioning è ancora da completare (#6, #17); non applicare `002_dev_app_role.sql` ad Azure.
6. Verificare API, MCP OAuth discovery, client MCP e test cross-tenant prima di approvare il rilascio.

## Gate mancanti prima della produzione
Terraform non è stato applicato a una subscription; servono pipeline immagini, deploy automatico, firma/scanning, configurazione frontend runtime, migrazioni controllate, provisioning privilegi RLS e rotazione credenziali, gestione chiavi e rotazione, backup/restore, antivirus file, audit e integrazione OIDC/MCP. Il segreto DB appare nello state Terraform: richiedere backend cifrato e ACL restrittive.

## Endpoint MCP OAuth

La variabile `resource_url` deve essere l'URL HTTPS canonico dell'endpoint MCP pubblico, **incluso `/mcp`** (es. `https://api.example.org/mcp`). Il documento RFC 9728 si trova a `https://api.example.org/.well-known/oauth-protected-resource/mcp` e l'issuer di Entra deve emettere token con audience configurata, claim tenant e subject firmati e scope Peritial appropriati. La conformance con client MCP esterni e l'app registration Entra sono ancora gate aperti (#4 e #17).
