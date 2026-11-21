# ADR 0081 — Test MCP con secondo client indipendente e versione negoziata

- Data: 22 novembre 2026
- Stato: implementato su `main`; CI del commit da verificare
- Issue: #12 (MCP client interoperability), #4 (OAuth/authorization)
- Branch `death`: storico, non modificato

## Contesto

Il progetto dispone già di una suite Playwright/TypeScript
che usa JSON-RPC HTTP raw e i protocolli MCP 2026-07-28 e
2025-11-25. Quella suite condivide il runtime Node,
gli helper di request e una parte del parsing del
transport. Può quindi non rilevare differenze
d'implementazione fra client.

## Decisione

Aggiungere una verifica CI con un client **Python
standard library** indipendente (urllib, json, nessun
SDK MCP o Playwright), in un processo separato dopo
la prova Playwright del job Compose. Il client
usa solo un'istanza Keycloak e REST/MCP su localhost
e un grant password di fixture demo, mai produzione.

Casi di prova live:
- Modern `server/discover`, autenticazione JWT
  e annuncio versioni 2026-07-28.
- Modern `tools/list`: 25 tool del manifesto,
  esattamente 19 con `outputSchema`; schema
  `get_perizia` con soli cinque campi `PeriziaDto`.
- Writer REST crea una Perizia; reader MCP moderno
  ottiene `structuredContent` dello stesso ID,
  riferimento, stato e timestamp esatto.
- Legacy 2025-11-25 `initialize`, `tools/list`
  e lettura dello stesso ID con lo stesso reader.
- Reader `create_perizia` negato tramite risposta
  `isError=true`; errori di versione/header
  sconosciuto sono HTTP 400 con codici JSON-RPC
  versionati, senza creare il riferimento ostile.
- Tutte le risposte protette REST/MCP devono avere
  `Cache-Control: no-store`; risposta MCP contenuta
  in massimo 2 MiB, no leak `tenantId`/`storageKey`
  nel risultato/schema.

Un decoder Python indipendente deve supportare
JSON-RPC JSON e SSE a più eventi, fallendo se la
risposta richiesta manca, è duplicata, ha id errato,
ha sia result sia error, ha versione non-2.0 o
supera il limite. Queste invarianti sono testate
offline nel job statico supply-chain-actions.

## Impatti e non obiettivi

Non cambia l'applicazione, il catalogo o le sue
firme, alcuna policy OAuth, schema DB/RLS/audit
né il comportamento delle tool call. Il secondo
client è indipendente **per implementazione** e
non va descritto come certificazione di compatibilità
con gli SDK MCP di fornitori terzi; #12 resta aperta
per almeno due client/prodotti esterni reali,
negoziazione aggiornata in deployment autenticato,
presigned upload e altri use case.

I client Keycloak con password grant e credenziali
demo sono ammessi **solo** nell'ambiente CI locale.
La PWA di prodotto usa PKCE e non recupera mai
le credenziali di test. Il client Python rifiuta
URL esterni e non registra bearer o corpi di
richieste/risposte. #4 resta aperta per Entra
e provisioning OAuth reali.
