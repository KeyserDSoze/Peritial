# ADR 0064 — Copertura live di tutti i tool MCP access:read

- Data: 21 novembre 2026
- Stato: implementato su `main`; verifica CI HEAD da registrare
- Issue: #12 (aperta), #4 (scope e controllo JWT)
- `death`: non modificato

## Contesto

La [ADR 0063](0063-mcp-live-wire-json-allowlist.md) ha
introdotto il primo gate JSON `tools/call` per sei tool di
lettura. Il test wire è stato convalidato da
[CI #44906149885](https://github.com/KeyserDSoze/Peritial/actions/runs/44906149885)
con **9/9 job SUCCESS**. Il manifesto DTO v1.0 fissa
le proprietà CLR ma un tool ancora non esercitato può
serializzare risultati diversi rispetto al contratto.

## Decisione

Estendere `tests/frontend/mcp-wire-output.spec.ts` a
**tutti i 12 tool con `access:read`** del catalogo MCP v1.2.
Le sei nuove probe sono:
`list_perizie`, `list_sinistri`, `page_sinistri`,
`search_sinistri`, `list_documents`,
`list_perizie_for_sinistro`.

Il test deriva il tipo di output da `returnType` nel
catalogo per ogni prova; rifiuta nomi, scope, effetto o
firme non previste. Un altro test confronta l'insieme
esatto dei tool `access:read` con la lista delle probe,
per impedire che nuove API di lettura siano aggiunte
senza copertura.

Il validatore riconosce ora anche
`IReadOnlyList<Guid>` e verifica ogni UUID.
Sono inclusi quattro controlli positivi/negativi sulle
liste e sui GUID, oltre a quelli dei DTO. Il percorso
live costruisce dati sintetici tramite REST writer e
verifica il catalogo con JWT **read-only** firmato
da Keycloak. Per la lista UUID, crea una associazione
con CAS REST e verifica la lista non vuota con MCP.

L'allowlist resta stretta: campi non-nullable
obbligatori; nullable omissibili per la regola SDK
osservata nella precedente prova RED; nessun
campo fuori manifesto, nessun tipo non revisionato.

## Limiti

Non cambia nessun endpoint, DTO runtime, token scope,
schema DB, RLS, audit, OCI o processo import XML.
Due tool di preflight XML sono `effect:read-only`
ma `access:write`: non rientrano nella copertura
con JWT reader. I tool mutatori rimangono da provare
sui loro output e invarianti.

Il confronto riguarda **risultati effettivi di 12 tool**
ma non produce una `outputSchema` JSON del Model
Context Protocol formalmente versionata e non verifica
SDK/client esterni o negoziazione delle versioni.
#12 resta aperta.
