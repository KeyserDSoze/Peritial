# ADR 0062 — MCP: allowlist dei DTO pubblici di risposta

- Data: 21 novembre 2026
- Stato: implementazione su `main`; CI HEAD da verificare
- Issue: #12, dipendenza #4
- `death` resta storico intatto

## Problema

La v1.2 del catalogo fissa il tipo di ritorno dichiarato in C#
di ciascun metodo MCP, ma una modifica alle proprietà pubbliche
di `SinistroDto` o `DocumentSummaryDto` non modifica necessariamente
la firma `Task<T>`. Per esempio, un campo interno come `TenantId`
potrebbe entrare per errore nel modello di risposta senza far
fallire il controllo sulle firme.

## Decisione

Introdurre `docs/mcp-output-dtos.v1.json` (`format=1`,
`version=1.0.0`, `mcpCatalogVersion=1.2.0`).
Il documento contiene le proprietà pubbliche e i tipi nominali C#
di tutti i **10 record DTO** raggiungibili dai 25 ritorni MCP,
comprese le liste e i risultati annidati.

Il gate Python `validate_output_dtos` impone una chiusura
transitiva del grafo DTO e rifiuta tipi fuori snapshot,
manifesti disallineati o tipi non revisionati. Quattro test
avversariali vi aggiungono controlli su DTO mancanti, annidati,
extra e versione incoerente.

Nel progetto Business.Tests una risorsa JSON embedded e test xUnit
leggono via reflection **le proprietà realmente pubbliche** dei 10
record, incluse proprietà aggiunte nel body, e ne confrontano
nome, tipo e nullabilità C# con il manifesto. Un test aggiuntivo
prova modifiche avversariali con campo extra, cambio tipo e
nullabilità, più un controllo positivo. La CI backend esegue
questi test insieme agli altri già previsti.

## Non-obiettivi

Non cambia alcun endpoint, DTO, runtime, scope, RLS, migrazione,
schema PostgreSQL, scanner o regola di import. Il contratto
protegge i tipi CLR: **non è** uno snapshot del JSON effettivo,
non controlla `outputSchema` SDK, naming policy, custom converters,
serializzazione o compatibilità di client esterni. I requisiti
residui di #12 restano aperti; non chiudere l'issue solo per questo.
