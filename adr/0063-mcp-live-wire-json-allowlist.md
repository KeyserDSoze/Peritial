# ADR 0063 — MCP: prove dei JSON effettivi `tools/call`

- Data: 21 novembre 2026
- Issue: #12 (ancora aperta), #4 (JWT/tenant)
- Stato: introdotto su `main`, CI sul commit da verificare
- Branch storico `death`: non modificato

## Motivazione

ADR 0061 e 0062 proteggono rispettivamente le firme C# dei 25 tool
MCP e le proprietà CLR dei 10 DTO raggiungibili. Non osservano
direttamente i risultati del trasporto MCP, che possono cambiare
per effetto del serializer o delle scelte dell'SDK.

## Decisione

`tests/frontend/mcp-wire-output.spec.ts` esegue un controllo
ad allowlist direttamente su risposte JSON-RPC di `tools/call`:
- Chiama `get_perizia`, `get_sinistro`, `get_perizia_sinistro`,
  `search_sinistri_page`, `get_document` e `page_documents`
  con un JWT read-only realmente firmato dal Keycloak del job Compose.
- Crea tramite REST autenticato un set sintetico isolato di perizia,
  sinistro e documento, prima della sola fase di lettura MCP.
  Non usa dati di produzione.
- Decodifica il blocco JSON `content[].text` (o
  `structuredContent` quando manca testo JSON) e confronta
  le proprietà esatte, incluse le annidate, con
  `docs/mcp-output-dtos.v1.json`, usando nomi lower-camel-case.
- Rifiuta nuove proprietà, campi **non-nullable** mancanti,
  tipi cambiati, UUID invalidi, date/date-time non ISO,
  numeri non interi e nullabilità non prevista. Per i campi
  nullable l'SDK può **omettere la proprietà** quando il valore
  è `null`: un campo nullable presente è comunque tipizzato
  e controllato. Sono inclusi test negativi
  eseguiti anche senza Compose.

L'intero test gira in Playwright desktop e mobile sul Compose
effettivo, oltre a una suite di regressioni statiche senza
autenticazione nella CI frontend. Questo integra, ma non
sostituisce, i test JWT/RLS/audit già esistenti.

## Garanzie e limiti

È il primo snapshot **wire JSON reale** per sei tool di lettura
e relativi sei tipi di output (anche DTO annidati). Non è un
catalogo JSON Schema di output completo: gli altri tool,
`outputSchema` prodotto dall'SDK, negoziazione di versioni
con client MCP esterni, serializzazioni alternative e
idempotenza generale restano aperti in #12.

Non muta la firma di alcun tool, autorizzazioni, dominio,
database, deployment o percorso di import XML. La #12
rimane aperta finché i criteri completi non sono soddisfatti.

## Verifica RED / correzione dell'omissione nullable

La prima prova live [CI RED #44905796126](https://github.com/KeyserDSoze/Peritial/actions/runs/44905796126)
ha individuato una vera particolarità di `tools/call`:
l'SDK ha omesso `assignedTo` quando il valore C# era `null`
su `get_perizia`, mentre il primo test richiedeva erroneamente
la presenza di ogni campo CLR. La logica ora richiede tutte le
proprietà non-nullable e nessuna proprietà fuori allowlist;
per una nullable accetta la proprietà assente o presente con
valore `null`/del tipo previsto. Il test positivo del DTO
non assegnato si aspetta esplicitamente `assignedTo` assente.
Non è stata cambiata la serializzazione applicativa.
