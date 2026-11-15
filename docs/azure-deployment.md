# Azure PaaS — IaC iniziale

Terraform in infra/ prepara: Azure Resource Group, VNet, subnet separate per Container Apps e PostgreSQL, PostgreSQL Flexible Server privato, Azure Blob Storage, Managed Identity con RBAC, Log Analytics, due Azure Container Apps (API/MCP e frontend).

Non usare immagini né credenziali del Compose locale in Azure.

## Percorso di rilascio previsto
1. Pubblicare immagini OCI API/frontend in un registry raggiungibile da Azure. Il frontend deve essere compilato con issuer OIDC corretto (runtime-config è ancora da implementare).
2. Provisionare Entra ID: app registration, scope peritial.read/peritial.write, audience e tenant_id firmato.
3. Configurare backend Terraform remoto cifrato e segreti da fonte protetta, non da file versionati.
4. terraform -chdir=infra init && terraform -chdir=infra plan && terraform -chdir=infra apply.
5. Applicare scripts/db/001_init.sql tramite runner/job con accesso alla VNet privata; la procedura automatica versionata è ancora da completare.
6. Verificare API, MCP OAuth discovery, client MCP e test cross-tenant prima di approvare il rilascio.

## Gate mancanti prima della produzione
Terraform non è stato applicato a una subscription; servono pipeline immagini, deploy automatico, firma/scanning, configurazione frontend runtime, migrazioni controllate, least-privilege DB, RLS, gestione chiavi e rotazione, backup/restore, antivirus file, audit e integrazione OIDC/MCP. Il segreto DB appare nello state Terraform: richiedere backend cifrato e ACL restrittive.
