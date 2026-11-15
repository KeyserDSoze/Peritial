# TDD e workflow

- Per ogni use case: scrivere il test rosso xUnit / Playwright, osservare il fallimento, implementare il minimo per renderlo verde, rifattorizzare.
- Per repository e adapter usare test di integrazione reali con PostgreSQL/MinIO dove possibile; non sostituire i test di isolamento multi-tenant con soli mock.
- Branch di sviluppo: solo `main`. Non creare PR da `death`; non fonderlo.
- Controlli obbligatori: `dotnet test`, `npm ci`, `npm run build`, `npx playwright test`, `docker compose config`, scansioni sicurezza prima del rilascio.
- Gli script di provisioning e l'infrastruttura non devono contenere credenziali reali.
- Allineare ADR, API, catalogo MCP e user-facing docs nello stesso commit funzionale.
