# ADR 0066 — JSON wire dei mutatori Perizie e Sinistri

- Data: 21 novembre 2026
- Stato: introdotto su `main`, CI HEAD da verificare
- Issue: #12, con relazioni a #4, #7, #9, #21
- `death` intatto come storico

## Scopo

Le ADR 0064 e 0065 hanno verificato output JSON reali
`tools/call` per 14 tool MCP non mutanti. Per i mutatori,
un output alterato dall'SDK può nascondere la versione CAS,
riferimenti e stati del workflow, anche con gli stessi
tipi pubblici del codice .NET.

## Decisione

Estendiamo `tests/frontend/mcp-wire-output.spec.ts` a
**sei mutatori** su Keycloak/Compose:
`create_perizia`, `assign_perizia`, `advance_perizia`,
`cancel_perizia`, `create_sinistro` e `update_sinistro`.
Tutti sono eseguiti con un JWT `peritial.write`.

Ogni risposta JSON `tools/call` viene parsata e validata
contro il `returnType` del catalogo MCP 1.2 e le proprietà
del DTO pubblicato `mcp-output-dtos.v1.json`. I record
creati o modificati vengono riletti indipendentemente via
REST `peritial.read`, con verifica di assegnazione, stato
workflow, annullamento e versione Sinistro **1 -> 2**.
I dati sono sintetici e generati su schema Compose effimero.

Si provano inoltre gli stessi **sei** tool con il token
read-only: l'SDK deve rispondere con errore e una lettura
REST deve confermare l'assenza di cambiamenti. Gli
identificativi non appaiono nelle risposte di errore.
Un test statico richiede che l'insieme dei mutatori MCP
del catalogo coincida con la somma dei **sei coperti**
e dei **cinque esplicitamente deferiti**, così nessun nuovo
mutatore può essere ignorato silenziosamente.

## Residui

Restano cinque mutatori (hard-delete con gate produttivo,
ingestion base64, link/unlink/reparent CAS) per il successivo
incremento. Servono anche JSON `outputSchema` SDK formali,
negoziazione di versioni e compatibilità client esterni.
La #12 rimane aperta. La policy di cancellazione #22
non viene in alcun modo indebolita.

Non sono modificate le implementazioni applicative, il
database, gli scope né le regole di import XML.
