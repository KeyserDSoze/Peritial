# ADR 0059 — Snapshot v1 del catalogo MCP e gate anti-drift

- Data: 20 novembre 2026
- Stato: implementato su `main`, CI finale da confermare
- Issue: #12 (tool MCP), #4 (OAuth enforcement), #6 (riproducibilità)
- Branch `death` preservato come storia, senza scritture

## Contesto

`McpTools.cs` esporta 25 strumenti, con nomi
stabili pubblicati mediante `McpServerTool(Name=...)`.
Il protocollo MCP espone `tools/list` con
`inputSchema` generato dal SDK. Prima di questa
tranche un cambio a una firma C# poteva
modificare i parametri obbligatori dello
schema senza un gate automatico per un
client terzo. La semplice presenza degli
strumenti in test funzionali non tutela
l'intero catalogo da rename/rimozioni.

## Decisione

Definiamo un **manifesto versionato**, iniziale
`docs/mcp-catalog.v1.json` (`format=1`,
`version=1.0.0`), ordinato per nome.
Per ogni strumento include:

- `name`: nome MCP pubblico;
- `required`: nomi esatti dei parametri
  obbligatori; parametri opzionali con default
  C# e `CancellationToken` esclusi;
- `access`: `read`, `write` oppure
  `write+delete` (scope aggiuntivo richiesto
  a `delete_sinistro` e gate di ambiente);
- `effect`: `read-only` o `mutating`.
  `preview_legacy_xml` e `plan_legacy_xml`
  sono letture, pur richiedendo lo scope write.

### Due controlli indipendenti

1. **Statico, fail-closed**: script Python
   `scripts/ci/mcp_catalog_contract.py` e
   sette test `tests/security/test_mcp_catalog_contract.py`
   nel job `supply-chain-actions` confrontano
   le 25 firme C# con il manifesto e il set
   delle classificazioni autorizzate.
   Rifiutano tool nuovi/mancanti, nomi
   duplicati, modifiche degli argomenti
   required, versioni/shape sconosciute
   e firme non riconosciute. Test negativi
   verificano rename, rimozione di
   `expectedVersion`, alterazione del
   rischio di delete, duplicati e versioni.
2. **Live SDK, OAuth**: test Playwright
   in Compose invoca `tools/list` con due
   client Keycloak distinti e scope
   `peritial.read` e `peritial.write`,
   compara i nomi reali e la lista
   `inputSchema.required` e verifica
   ogni `properties` richiesto. Non
   modifica dati. Questo individua divergenze
   tra parser delle firme, SDK e API runtime.

## Tradeoff e limiti

Lo snapshot **non** è un'API di negoziazione
di protocollo/semver runtime, né promette
compatibilità semantica degli argomenti o
dei tipi opzionali. Un cambiamento dei
metodi e del manifesto coordinati richiede
review e rilascio esplicito della versione:
il gate blocca il drift *non documentato*,
non sostituisce una review umana sul significato.
La classificazione `access` è un contratto
dichiarativo testato, non una policy che
viene applicata dall'SDK; i controlli
veri rimangono in JWT/Business/PostgreSQL,
inclusi tenant RLS, audit e CAS.

L'issue #12 resta aperta per catalogo
sostanzialmente completo del dominio,
version negotiation evolutiva, client MCP
esterni reali, schema opzionale/type
compatibility e idempotenza di tutte le
scritture. Nessuna migrazione SQL,
endpoint addizionale, privilegi o
modifica ai dati di produzione in
questa tranche.
