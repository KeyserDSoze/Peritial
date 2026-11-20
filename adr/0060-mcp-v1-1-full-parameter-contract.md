# ADR 0060 — MCP v1.1: compatibilità dei parametri opzionali e tipi JSON

- Data: 20 novembre 2026
- Stato: implementato su `main`, CI Compose finale da confermare
- Issue: #12; dipendenze #4 e #6
- `death` rimane storico, mai modificato

## Contesto

Il catalogo versione 1.0.0 dell'ADR0059
già elenca 25 tool MCP e i loro
`inputSchema.required`. Tuttavia un
client può utilizzare parametri opzionali
che, se rinominati, rimossi, resi
obbligatori o cambiati di tipo, rompono
l'integrazione senza alterare
necessariamente la lista dei required.
Esempi sensibili:
`page_sinistri(cursor?, limit=50)`,
`search_sinistri(occurredFrom?,occurredTo?)`,
`create_sinistro(reportedOn?)`,
`ingest_file(idempotencyKey?)`.

## Decisione

Aggiorniamo il **manifesto di controllo**
`docs/mcp-catalog.v1.json` a
`version=1.1.0`, mantenendo
`format=1` e gli stessi 25 nomi.
Il file dichiara 55 argomenti visibili
e, per ciascuno, tre proprietà:

1. `type`: `string`, `uuid`,
   `date`, `integer`. I tipi
   C# `Guid` e `DateOnly` si
   serializzano come JSON string,
   `long` e `int` come integer.
2. `optional`: vero se il C#
   prevede un valore default,
   anche quando il valore non può
   essere `null`.
3. `nullable`: vero soltanto
   quando il tipo C# contiene `?`.
   Omissibilità e nullabilità
   non sono sinonimi.

`CancellationToken ct=default` è
un dettaglio interno al runtime:
non compare nello schema MCP.

Il gate Python già obbligatorio
nel job `supply-chain-actions`
verifica, con parser che fallisce
chiuso su sintassi non supportate,
le firme complete, l'ordine,
la lista `required`, le categorie
di accesso e le annotazioni.
I test avversariali ora coprono
anche cambio nome di opzionale,
mutazione `Guid`/`DateOnly`/`long`,
alterazione della nullabilità
e del default. I test statici
passano da 7 a 12; TDD RED
sulla baseline 1.0.0,
GREEN con v1.1.0.

Playwright nella Compose CI
confronta il vero JSON Schema
`tools/list` SDK, con JWT Keycloak
read e write: verifica esattamente
**tutte** le proprietà dello
schema, inclusi gli opzionali,
gli argomenti required e
l'appartenenza del wire type
primario (`string`/`integer`)
anche quando lo schema usa
`anyOf`, `oneOf` o `allOf`.

## Tradeoff e limiti

Non sono introdotti nuovi endpoint,
tool, autorizzazioni, grant,
storage o migrazioni. Questo non
costituisce un protocollo di
version negotiation runtime
fra client con manifesti diversi:
la versione rappresenta lo
snapshot di compatibilità sottoposto
a test. Il gate live verifica
solo il tipo JSON primario; non
stabilizza tutte le keyword JSON
Schema (`format`, `default`,
`nullable`, `description`,
i vincoli), né le proprietà di
output o tutte le semantiche di
Business. I test reali
JWT/RLS/CAS restano autoritativi.
Le future versioni per client
esterni e la matrice completa
di compatibilità restano
aperte nella issue #12.
