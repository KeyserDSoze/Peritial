# ADR 0061 — MCP v1.2: firme di risultato versionate

- Data: 21 novembre 2026
- Stato: implementato su `main`, CI HEAD da verificare
- Issue: #12 (resta aperta), dipendenze #4
- `death` rimane storico, non modificato

## Contesto

Il manifesto v1.1 bloccava i 25 nomi e 55 argomenti, tipi,
nullabilità, default letterali, ordine e accessi. Il ritorno C#
di un tool poteva invece cambiare lasciando intatti gli input.

## Decisione

Il manifesto `docs/mcp-catalog.v1.json` passa a 1.2.0 con campo
obbligatorio `returnType` per ogni tool. Il parser fail-closed
`scripts/ci/mcp_catalog_contract.py` estrae il tipo di ritorno
dichiarato dai metodi annotati `[McpServerTool]`, includendo
metodi sincroni, `Task<T>` e liste annidate `IReadOnlyList<T>`.
Esempi: `list_perizie` → `Task<IReadOnlyList<PeriziaDto>>`;
`page_documents` → `Task<DocumentPageDto>`; `delete_sinistro`
→ `Task<bool>`; `preview_legacy_xml` → `LegacyXmlPreviewDto`.

Quattro test ulteriori rifiutano drift C# a input invariati,
drift di generic annidati, manifesti manomessi e campi assenti.
La suite statica viene eseguita nel job obbligatorio
`supply-chain-actions`; l'E2E JWT Keycloak continua a confrontare
`tools/list` del runtime per input e autorizzazione.

## Limiti

È un gate sui **nomi di tipi C#**, non sulla rappresentazione
JSON degli output: cambi a proprietà dei DTO senza mutazione
della firma non vengono rilevati. Non stabilizza `outputSchema`,
serializzazione, formati, negoziazione fra versioni MCP o client
esterni. Tutto ciò resta nella #12, che non deve essere chiusa.
Nessun nuovo tool, privilegio, schema/migrazione o comportamento
applicativo viene introdotto. Non scrivere su `death`.
