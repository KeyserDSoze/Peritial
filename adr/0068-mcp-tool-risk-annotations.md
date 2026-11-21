# ADR 0068 — Hint di rischio MCP espliciti e versionati

- Data: 21 novembre 2026
- Stato: implementato su `main`, CI corrente da verificare
- Issue: #12, #4, #22
- `death`: preservato come storico

## Problema

Il catalogo v1.2 pubblica 25 tool con firma, `access`
e `effect`, ma gli attributi dell'SDK C# 2.0 non
impostavano i relativi MCP Tool Annotations. Il valore
predefinito `readOnlyHint:false` può far classificare
come mutanti anche 14 tool che effettuano soltanto
letture/preflight. La distinzione conta per consenso,
pianificazione e UX di client MCP terzi.

## Decisione

Tutti i 25 attributi `[McpServerTool]` dichiarano ora
tre hint in modo esplicito: `ReadOnly`, `Destructive`
e `OpenWorld`. Il nuovo manifesto
`docs/mcp-tool-annotations.v1.json` (v1.0, agganciato
al catalogo v1.2) fissa per nome i tre corrispondenti
valori JSON esposti da `tools/list`.

- `readOnlyHint:true` soltanto sui 14 tool con
  `effect:read-only`; include preview e planning XML,
  che continuano a richiedere JWT `peritial.write`;
- `destructiveHint:true` per assegnare, avanzare,
  annullare o aggiornare stato esistente, mutare link
  o cancellare Sinistri, sempre con CAS/autorizzazione;
- `destructiveHint:false` per tool di lettura e
  creazioni additive di Perizie, Sinistri e documenti;
- `openWorldHint:false` per tutti: nessun tool
  compie ricerche sul web o chiamate open-ended;
- `idempotentHint` non viene dichiarato `true`:
  la chiave opzionale dell'ingestion non rende
  automaticamente idempotenti gli altri writer.

Il parser statico Python è aggiornato per riconoscere
i tre attributi, fallire chiuso in caso di omissione o
drift, e confrontarli con il manifesto e il catalogo.
I test negativi coprono riduzione di rischio, rimozione
di un attributo e open-world non autorizzato.
Playwright verifica tutti gli hint osservati nei
risultati reali `tools/list` con JWT reader e writer
Keycloak, oltre agli input schema esistenti.

## Sicurezza e limiti

Sono **metadati advisory**, non token di privilegio:
OAuth, tenant RLS, audit e gate hard-delete restano
invariati. `delete_sinistro` mantiene
`destructiveHint:true` e continua a rifiutare
cancellazioni senza i requisiti produttivi #22.
L'API e il database non cambiano. Rimangono aperti
output schema JSON del SDK, client esterni e
negoziazione delle versioni nella #12.
