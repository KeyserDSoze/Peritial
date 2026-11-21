# ADR 0065 — MCP: output JSON preflight XML con scope di scrittura

- Data: 21 novembre 2026
- Issue: #12, #4, #10, #25 (tutte aperte)
- Stato: sviluppo pubblicato su `main`; CI da verificare
- `death` non modificato

## Decisione

La CI della [ADR 0064](0064-mcp-all-read-tools-wire-output.md)
ha verificato 12 tool `access:read`. Aggiungiamo ora due tool
`effect:read-only` ma `access:write`:
`preview_legacy_xml` e `plan_legacy_xml`.

Il test `tests/frontend/mcp-wire-output.spec.ts` esegue
`tools/call` con JWT Keycloak writer su XML sintetici
Accertatore (anagrafica non persistibile) e Classic (orario
legacy non rappresentabile dal modello DateOnly). Deriva il
`returnType` dal catalogo MCP 1.2, valida i DTO reali
mediante il manifesto versionato dei 10 output e controlla
che valori privati e timestamp non vengano restituiti.
Le chiamate non devono creare nuovi Sinistri, verificato con
ricerca autenticata prima e dopo ogni input. Un JWT read-only
non deve invocare i due tool: il protocollo deve restituire
un errore senza i dati XML del mittente.

Il gate statico controlla che l'insieme degli strumenti con
`effect:read-only/access:write` coincida esattamente con i due
preflight sotto test; future aggiunte senza copertura falliranno.

La copertura live aumenta a **14/25 tool MCP**. Nessuna modifica
al runtime, alle firme, ai permessi o ai dati. L'import XML
completo resta disabilitato e le issue #10 e #25 aperte.

## Limiti

Non sono ancora testati gli output degli 11 tool mutatori,
le versioni dei client esterni né lo `outputSchema` JSON SDK
completo. #12 resta aperta.
