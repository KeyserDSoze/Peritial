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
  omesso dal serializer MCP. La [ADR 0076](0076-canonical-created-at-utc-postgres.md)
  impone una granularità UTC di microsecondi ai nuovi
  `createdAtUtc` prima della persistenza. Il confronto
  MCP/REST torna **esatto**, senza tolleranze di timestamp;
  i dati legacy vengono preservati senza riscrittura.
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

## Risoluzione del follow-up #26

La [ADR 0076](0076-canonical-created-at-utc-postgres.md)
risolve nel dominio la diversa granularità tra il tick
100 ns di `DateTime.UtcNow` e i microsecondi PostgreSQL:
si canonicalizza prima di creare il nuovo aggregate, e si
ripristina nel test l'uguaglianza esatta della creazione MCP
con la lettura REST. Non si modificano `Restore`, le date
legacy o l'audit persistente. La conferma end-to-end dipende
dalla CI dello specifico commit ADR 0076.
