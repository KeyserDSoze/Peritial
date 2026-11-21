# ADR 0070 — Output schema e structuredContent per i dettagli MCP

- Data: 21 novembre 2026
- Stato: introdotto su main, validazione CI da completare
- Issue: #12, #4
- death resta branch storico

## Scelta

Abilitare il flag SDK C# 2.0 `UseStructuredContent = true`
solo per `get_perizia` e `get_sinistro`.
Sono tool read-only con ritorni DTO già allowlisted.
Su MCP 2026-07-28 gli utenti ricevono
`tools/list[].outputSchema` e
`tools/call.result.structuredContent`.

La lista dei due opt-in è salvata in
`docs/mcp-structured-outputs.v1.json`, agganciata al
catalogo 1.2. I test Python controllano omissioni,
aggiunte e abilitazioni di writer non approvate.
Il Playwright con JWT Keycloak verifica i campi dello
schema, i valori strutturati naturali, l'assenza di
chiavi private e la concordanza con REST.

## Compatibilità

Il vecchio campo `content` e i client legacy
non vengono disabilitati. Gli altri 23 tool non
dichiarano ancora output schema strutturati.
Non cambiano OAuth, FORCE RLS, audit, DB o il
gate produttivo per `delete_sinistro`.
La #12 resta aperta per il resto del catalogo
e l'integrazione con client SDK terzi.

## Verifica RED e allineamento nullability

La prima pipeline [#44923971662](https://github.com/KeyserDSoze/Peritial/actions/runs/44923971662)
ha verificato la presenza di `outputSchema` e il contenuto naturale
ma ha fallito su confronto `toMatchObject` col REST: REST include
`assignedTo:null`, mentre l'SDK MCP omette la proprietà nullable.
Il test è stato corretto per accettare omissioni soltanto per
campi nullable dal manifesto DTO, confrontando esattamente ogni
campo non nullo e continuando a rifiutare proprietà ignote.
