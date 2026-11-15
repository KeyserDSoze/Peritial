# Peritial

Ricostruzione **clean-room** del gestionale peritale. La sorgente storica è consultabile unicamente nel branch `death`; lo sviluppo avviene direttamente su `main`. Nessun merge da `death`.

## Obiettivo
API REST .NET 10 + server MCP Streamable HTTP OAuth, unico dominio peritale in DDD, frontend React/TypeScript PWA, isolamento multi-tenant, deployment Azure PaaS oppure on-premises con componenti open source.

## Stato di implementazione
Base di progetto in ricostruzione. Il primo vertical slice è **perizie + ingestion documentale**; tutte le altre funzionalità legacy sono registrate nella matrice `docs/legacy-inventory.md` e non si considerano implementate fino al superamento dei relativi test. Non usare in produzione prima della revisione delle autorizzazioni e dei test integrati.

## Sviluppo
- `docker compose -f docker/compose.yml up --build`: ambiente completo locale.
- `dotnet test tests/backend/Peritial.Domains.Tests`: test di dominio.
- `npm ci --prefix src/frontend && npm run test:e2e --prefix src/frontend`: Playwright (richiede browser installati).
- Gli stessi comandi sono eseguiti tramite GitHub Actions.

Leggi `docs/architecture.md`, `docs/requirements.md`, `docs/mcp.md`, `docs/development.md` e `adr/`. Le configurazioni locali sono solo dimostrative; i segreti non si committano.
