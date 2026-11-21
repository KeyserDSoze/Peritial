# ADR 0077 — Output MCP strutturati per mutazioni auditate di stato

- Data: 21 novembre 2026
- Issue: #12, #7, #9, #4, #21
- Stato: implementazione su `main`, CI da confermare
- Branch `death`: storico, non modificato

## Decisione

Abilitare `UseStructuredContent=true` per quattro mutatori MCP
già disponibili:

| Tool | Risultato | Invarianti Business |
|---|---|---|
| `assign_perizia` | `Task<PeriziaDto>` | assegnazione solo da Registered |
| `advance_perizia` | `Task<PeriziaDto>` | target simbolico e transizione valida |
| `cancel_perizia` | `Task<PeriziaDto>` | terminalità e niente replay |
| `update_sinistro` | `Task<SinistroDto>` | `expectedVersion` obbligatoria, SQL CAS |

Il manifesto `docs/mcp-structured-outputs.v1.json` passa da
11 a **15/25** tool. Il gate Python ammette le due creazioni e le
quattro mutazioni di stato **per nome esplicito**, soltanto con
`effect=mutating`, `access=write` e DTO allowlisted;
`delete_sinistro`, `ingest_file`, `link_perizia_sinistro`,
`unlink_perizia_sinistro` e `reparent_perizia_sinistro` sono
ancora esclusi. Il gate delle annotazioni MCP preserva
`destructiveHint=true` e `openWorldHint=false` per tutti e quattro.

## Test e proprietà garantite

- `tools/list` moderno espone `outputSchema` per ciascun
  mutatore, con tutti e soli i campi approvati del DTO.
- `tools/call` con JWT `peritial.read` produce errore e
  il successivo REST GET è identico al precedente: nessun side-effect.
- Il JWT `peritial.write` produce `structuredContent` naturale,
  identico campo per campo al REST GET dopo commit, inclusi
  `createdAtUtc` canonici (ADR 0076), nullability e versioni.
- Il test esercita assegnazione → avanzamento → annullamento
  sulla stessa Perizia e update `version=1→2` di un Sinistro.
- Il replay di cancellazione terminale e il replay con CAS stale
  vengono negati, senza un nuovo stato o incremento di versione.
- I test avversariali statici rilevano mismatch fra source
  `UseStructuredContent` e manifesto e bloccano mutatori non reviewati.

## Non obiettivi

Non cambia il catalogo degli input, la forma dei tool, gli scope,
l'ID tenant derivato dal token, il DB, gli audit SQL, il
workflow PWA o il gate per la cancellazione Sinistri (#22).
Non introduce idempotenza generalizzata delle mutazioni;
#12 rimane aperta per 10 tool non strutturati, client esterni,
negoziazione delle versioni e i verticali ancora da coprire.
