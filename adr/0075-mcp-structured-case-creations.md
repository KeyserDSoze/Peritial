# ADR 0075 — Output MCP strutturati per le creazioni Perizia/Sinistro

- Data: 21 novembre 2026
- Stato: implementato su `main`, CI del nuovo commit da verificare
- Issue: #12, #7, #9, #4, #21
- `death` resta storico e non viene modificato

## Decisione

Estendere `UseStructuredContent=true` esclusivamente a
`create_perizia` (`Task<PeriziaDto>`) e
`create_sinistro` (`Task<SinistroDto>`).
Entrambi restituiscono DTO pubblici già revisionati in
`docs/mcp-output-dtos.v1.json`, eseguono i medesimi use case
e transazioni audit/tenant di REST e rimangono mutativi
(`effect=mutating`, `access=write`).

Il manifesto `docs/mcp-structured-outputs.v1.json`
dichiara 11/25 tool con risultato strutturato.
Il controllo statico ammette `access=write/effect=mutating`
**soltanto** per i due nomi esplicitamente approvati;
non autorizza una classe generale di writer a pubblicare
output strutturati. Questo è un contratto di serializzazione,
non una variazione di autorizzazione.

## Verifiche di sicurezza e interoperabilità

- Il protocollo MCP 2026-07-28 espone `outputSchema`
  soltanto con campi allowlisted dei due DTO.
- `tools/call` con JWT `peritial.read` produce un
  diniego; il token `peritial.write` può creare
  rispettivamente una sola Perizia e un solo Sinistro.
- Gli output `structuredContent` sono validati
  rispetto al manifesto CLR e confrontati campo per campo
  con `GET /api/perizie/{id}` e `GET /api/sinistri/{id}`
  tramite token read; il solo `null` nullable può essere
  omesso dal serializer MCP. Per `createdAtUtc` su risposta
  di **creazione** il confronto ammette provvisoriamente un
  massimo di **1 microsecondo** per la perdita del tick .NET
  da 100 ns nel round-trip PostgreSQL; la canonicalizzazione
  puntuale è tracciata in [#26](https://github.com/KeyserDSoze/Peritial/issues/26).
  Non si applica la tolleranza ad altri campi o a letture.
- Regressioni statiche rifiutano un cambiamento non
  revisionato dell'opt-in e l'aggiunta di
  `assign_perizia`, `update_sinistro`, `ingest_file`
  o `delete_sinistro` al manifesto.

## Limiti

La prova avviene contro lo SDK MCP e Keycloak in CI,
non certifica ancora SDK client di terze parti.
Il set non include flussi di delete e non elude
i gate di retention (#22) e import XML (#24/#25).
Nessuna modifica a JWT, scopes, DB, RLS, audit
o semantica delle mutazioni. Rimangono 14 tool
senza `outputSchema` e requisiti aperti nella #12.

## Evidenza CI e follow-up di precisione

La [run #44949476077](https://github.com/KeyserDSoze/Peritial/actions/runs/44949476077)
ha completato 8 job con successo e ha fallito la prova
Keycloak/Playwright desktop e mobile sulla differenza
tra il `DateTime` in-memory dell'output di creazione
e la lettura dopo commit PostgreSQL (100 ns osservati).
L'output MCP strutturato è stato prodotto correttamente
prima del confronto. È stata aperta la [issue #26](https://github.com/KeyserDSoze/Peritial/issues/26)
per definire una rappresentazione identica dei timestamp
persistiti. La tolleranza stretta del test non dimostra
che questa canonicalizzazione sia stata implementata.
