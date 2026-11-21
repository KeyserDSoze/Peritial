# ADR 0069 — Dual-era MCP 2.0 su Streamable HTTP stateless

- Data: 21 novembre 2026
- Stato: sviluppo `main`, CI da verificare
- Issue: #12 e #4
- `death` intatto

## Motivazione

Il pacchetto `ModelContextProtocol.AspNetCore 2.0.0`
supporta due meccanismi di negoziazione differenti:
la revisione moderna `2026-07-28`, con
`server/discover` e metadati per richiesta, e il
legacy `2025-11-25`, con `initialize`. I precedenti
test Playwright esercitavano solo chiamate non
negoziate e `tools/list`, pur verificando 25 tools.

## Decisione

Impostare esplicitamente
`HttpServerSessionMode.Stateless` nella registrazione
del server: nessuno stato di sessione legato a JWT,
utente o nodo. Manteniamo il medesimo endpoint
`/mcp` e non alteriamo policy OAuth o tool.
La scelta evita drift delle impostazioni predefinite
SDK e permette di servire entrambi i protocolli.

Il manifesto `docs/mcp-protocol-eras.v1.json` v1.0
pinna pacchetto 2.0.0, modalità stateless, revisione
moderna/legacy, header obbligatori moderni e codici
di errore di incompatibilità `-32022`/`-32020`.

## Test di integrazione

Con JWT Keycloak firmati su Compose:

- modalità moderna: POST `server/discover`,
  `tools/list` e `tools/call(get_perizia)`, con
  `MCP-Protocol-Version:2026-07-28`,
  `Mcp-Method`, `Mcp-Name` e `params._meta`;
- modalità legacy: POST `initialize` su
  `2025-11-25`, quindi `tools/list`; sia il
  client moderno sia quello legacy vedono
  esattamente gli stessi 25 tool;
- entrambi i protocolli rimangono stateless senza
  `Mcp-Session-Id`;
- una richiesta `create_perizia` con versione
  ignota o intestazioni incoerenti deve produrre
  HTTP 400 con codice JSON-RPC rispettivamente
  `-32022` o `-32020`, senza persistere dati;
- la discovery moderna senza JWT rimane 401.

Sono test JSON-RPC/HTTP indipendenti da REST e dai
DTO C#; l'SDK completo di un client esterno non è
ancora integrato. Non cambia il gate di cancellazione
#22, né viene attivato un servizio MCP pubblico.
La #12 resta aperta per outputSchema, client esterni
e ulteriore version negotiation.
