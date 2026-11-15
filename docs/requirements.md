# Requisiti vincolanti

1. Lavorare su `main` senza merge, cherry-pick o copy/paste indiscriminato dal branch `death`.
2. Backend .NET 10 REST, un solo dominio peritale, separazione Applications / Business / Domains / Infrastructures; tenant esplicito su ogni risorsa.
3. React + TypeScript, PWA responsive, componenti shadcn/ui, tema dark/light e i18n it/en.
4. Azure PaaS (Container Apps, Azure Database for PostgreSQL Flexible Server, Blob Storage, Entra ID) e on-premises (container Docker, PostgreSQL, MinIO, Keycloak); stesso codice applicativo.
5. Ogni use case esposto via REST e, se appropriato e sicuro, via MCP. L'MCP è resource server OAuth, non identity provider; la validazione JWT e l'autorizzazione sono a livello di richiesta e di tool.
6. Ingestion file tramite upload binario REST e tool MCP controllato, con quarantena/scansione antivirus pianificata prima di produzione, limite dimensione, estensioni/MIME consentiti, isolamento tenant e audit.
7. TDD Red → Green → Refactor obbligatorio; xUnit, test di integrazione e Playwright, esecuzione locale/CI.
8. `adr/` per decisioni; `docs/` per documentazione aggiornata insieme al codice.

## Definizione di completamento
Una funzionalità è completata solo quando dispone di: contratto API, modello/validazioni/permessi, implementazione REST e MCP (ove sensato), test di dominio, sicurezza multi-tenant, test E2E se UI, documentazione aggiornata e build CI verde.

## Non obiettivi del primo vertical slice
Non simulare o dichiarare implementati import XML Classic/Accertatore, report Word/PDF, fatturazione, email, KPI e riconciliazione dati del legacy: seguono iterazioni TDD dedicate.
