# ADR 0071 — Structured output MCP: documento e associazione

- Data: 21 novembre 2026
- Stato: implementato su main; CI da verificare
- Issue: #12, #11, #23, #4
- Nessuna modifica al branch death

## Contesto e decisione

Dopo la CI 9/9 verde della ADR 0070, estendere
in modo esplicito `UseStructuredContent=true` ai
due tool read-only `get_document` (solo metadati
sicuri) e `get_perizia_sinistro` (associazione
versionata e nullable). Con `get_perizia` e
`get_sinistro` sono ora quattro tool a pubblicare
`tools/list[].outputSchema` e dati naturali in
`tools/call.result.structuredContent`.

La allowlist `docs/mcp-structured-outputs.v1.json`
rimane v1.0, vincolata al catalogo MCP v1.2;
l'estensione è esplicita e testata dal gate
`validate_structured_output_tools`. Ulteriore
regressione Python: non è ammesso introdurre tool
read-only nel manifesto senza abilitarli nella
dichiarazione C#.

## Prove live

Playwright/Keycloak usa token writer soltanto
per creare Perizia e Sinistro, collegarli con
CAS `expectedLinkVersion:1` e caricare un PDF
sintetico via REST, realmente scansionato da
ClamAV e archiviato. Con JWT reader si verifica:

- `get_perizia_sinistro` senza link:
  `linkVersion=1`, `sinistroId` nullable;
- `get_perizia_sinistro` con link:
  stesso ID perizia, sinistro collegato,
  `linkVersion=2`;
- `get_document`: metadati sicuri con
  `CreatedAtUtc` e `PeriziaId`, mai
  URL/chiave storage privata.

Ogni tool annuncia soltanto le proprietà DTO
allowlisted nello schema JSON e restituisce
`structuredContent` valido e consistente con
REST autorizzato. L'equivalenza `null`/campo
assente si applica esclusivamente a proprietà
nullable già nel manifesto.

## Limiti

Nessun cambiamento a OAuth, FORCE RLS,
audit, gestione storage, DB, tool mutativi
o cancellazione Sinistri (#22).
La #12 rimane aperta: 21 tool ancora senza
`outputSchema` strutturato e client
SDK esterni non certificati.
