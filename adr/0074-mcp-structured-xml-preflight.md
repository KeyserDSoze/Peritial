# ADR 0074 — Output strutturati MCP per i preflight XML

- Data: 21 novembre 2026
- Stato: implementazione su `main`, CI da verificare
- Issue: #12, #10, #4, #25
- `death`: ramo storico non modificato

## Decisione

Abilitare `UseStructuredContent=true` per i tool MCP
`preview_legacy_xml` (ritorno sincrono `LegacyXmlPreviewDto`)
e `plan_legacy_xml` (`Task<LegacyXmlImportPlanDto>`).
Il secondo DTO contiene una preview annidata; i due tipi sono
già revisionati in `docs/mcp-output-dtos.v1.json`.

Il set `docs/mcp-structured-outputs.v1.json` passa da 7 a 9
tool su 25. Il gate di CI riconosce i due ritorni C# DTO diretti
e asincroni e ammette l'opt-in dei due *soli* preflight
`effect=read-only/access=write`, senza generalizzare ad altri
tool con autorizzazione write. Un mutatore o un DTO non revisionato
non può essere ammesso al manifesto.

## Proprietà di sicurezza e prove

- Autorizzazione **immutata**: il token JWT deve contenere
  `peritial.write`; `read` da solo non autorizza questi tool
- `tools/list` MCP moderno annuncia `outputSchema`
  con tutte e sole le proprietà allowlisted, compresa la
  preview annidata del piano
- `tools/call` MCP 2026-07-28 restituisce `structuredContent`
  validato con il manifesto versionato dei DTO e comparato ai
  corrispondenti `POST /api/imports/xml/preview` e `/plan`
- Fixture Accertatore con dato anagrafico non persistibile e
  Classic con orario non rappresentabile in `DateOnly`:
  la preview marca `hasUnmappedBusinessData`, non restituisce
  PII/orario e non genera Sinistri
- Il test verifica anche il diniego a un token read-only
  e l'assenza dei dati privati nella risposta di errore

## Limiti

Si tratta di un incremento del contratto di output, non
dell'importazione. I gate #24 (identità/concorrenza) e #25
(precisione temporale) continuano a bloccare l'import completo.
Nessun cambio a DB, OAuth, FORCE RLS, auditing,
paginazione, o policy di hard-delete #22.
Restano 16 tool senza output strutturato e manca ancora la
certificazione interoperabile con client MCP esterni.
