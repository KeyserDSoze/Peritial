# ADR 0073 — Output strutturato per la paginazione globale Sinistri

- Data: 21 novembre 2026
- Stato: implementazione su `main`, CI da verificare
- Issue: #12, #7 e #4
- Branch storico `death`: sola consultazione, non modificato

## Decisione

Abilitare `UseStructuredContent = true` per il tool read-only
`page_sinistri`, che restituisce già `Task<SinistroPageDto>`
tramite lo stesso servizio tenant-scoped della API REST
`GET /api/sinistri/page`. Il DTO e i suoi campi annidati
sono vincolati dal manifesto `docs/mcp-output-dtos.v1.json`.

Il manifesto `docs/mcp-structured-outputs.v1.json` dichiarerà
7 tool su 25. Il gate statico fallisce se il catalogo e l'opt-in
nel sorgente divergono. Nessun nuovo schema database,
ruolo, tool, endpoint o trasformazione del cursore.

## Prove di contratto

- `tools/list` MCP 2026-07-28 deve pubblicare `outputSchema`
  del tipo pagina con proprietà `items` e `nextCursor`, senza
  chiavi storage o tenant internamente sensibili
- `tools/call` con JWT read-only Keycloak deve restituire
  `structuredContent` conforme al `SinistroPageDto` allowlisted
- due pagine consecutive `limit=1` vengono confrontate con
  le stesse pagine REST per contenuto e cursori reali
- il test non presume che le fixture siano le sole nel tenant;
  controlla però UUID distinti, shape e paginazione effettiva
- il gate di regressione rifiuta rimozione dell'opt-in dal C#
  o del tool dal manifesto

## Limiti e rischi residui

Il cursore non è un token di autorizzazione; il tenant
deriva unicamente dal JWT. Lo schema pubblico non costituisce
prova di interoperabilità con client SDK esterni diversi.
Restano 18 tool senza output strutturato, la negoziazione
effettiva delle versioni e gli altri criteri della #12.
La funzionalità di hard-delete #22 resta bloccata.
