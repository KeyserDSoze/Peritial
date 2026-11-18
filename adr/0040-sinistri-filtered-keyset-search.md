# ADR 0040 — Ricerca Sinistri filtrata con paginazione keyset

- Data: 18 novembre 2026
- Stato: implementata; attestazione CI dell'HEAD in verifica
- Issue: #7, #12, #13
- Branch: `main`; `death` rimane solo archivio storico.

## Problema

ADR 0038 introdusse la ricerca PostgreSQL per prefisso e date,
limitata a 50 risultati. ADR 0039 aggiunse keyset solo al catalogo
non filtrato. I sinistri più vecchi oltre il limite della ricerca
restavano quindi irraggiungibili dalla PWA, pur presenti nel tenant.

## Decisione

- Nuovo REST `GET /api/sinistri/search/page` e tool MCP read-only
  `search_sinistri_page(prefix,occurredFrom?,occurredTo?,limit=50,cursor?)`.
  Manteniamo **intatti** `/api/sinistri/search` e
  `search_sinistri` per autocomplete del collegamento Perizia–Sinistro.
- `SinistroService.SearchPageAsync` valida lo scope
  `peritial.read|write`, normalizza il prefisso 2–100 caratteri e
  convalida range inclusivo e `limit` 1–50 prima dell'accesso ai dati.
  Richiede al repository `limit+1` per impostare `nextCursor`.
- `PgSinistroRepository.SearchPageAsync` esegue in **una sola SELECT**
  la proiezione tenant-scoped con `ILIKE` parametrizzato, escaping
  letterale dei caratteri `%`, `_`, `!`, filtri date,
  seek `occurred_on < afterDate OR (occurred_on=afterDate AND id>afterId)`,
  ordine `occurred_on DESC,id ASC` e `LIMIT` bounded.
  Transazione `TenantDbScope` e `FORCE RLS` sono applicate a ogni pagina.
- `SinistroSearchPageCursor` usa un formato separato
  `s1.<base64url(yyyy-MM-dd|UUID)>.<digest>`:
  digest di 16 caratteri esadecimali (SHA-256 troncato a 64 bit) del
  prefisso trim e dei due limiti data, senza identificativi tenant
  o riferimenti completi nel cursore. Limite 128 caratteri;
  ricodifica canonica; filtro modificato o cursore invalido porta 400.
  **Non è un token firmato**: il digest rileva il riuso involontario,
  non previene manipolazioni deliberate, e non autorizza l'accesso.
- La PWA carica prima pagina solo su richiesta e permette «Carica
  altri sinistri» anche durante un filtro; append senza duplicati,
  reset/refresh e epoch invalidano risultati vecchi. Un POST 201 durante
  ricerca applicata rilegge la prima pagina da PostgreSQL e recupera
  il `nextCursor` del filtro. Label e hint it/en aggiornati.
- Nessuna migrazione V005: lo schema V003 con indice
  `(tenant_id,occurred_on DESC,id)` resta invariato. Nessun nuovo
  endpoint espone `tenantId`, audit, credenziali o dati oggetto.

## Verifica

Test-first in `SinistroPaginationTests` e
`SinistroPersistenceTests`: escaping, date, tenant RLS, cursor legato
ai filtri, stessa data e UUID, cap, assenza audit letture e inserimenti
concorrenti. Playwright con OAuth Keycloak desktop/mobile prova
REST, MCP, 400/401, assenza di tenant ID, pagine multiple della PWA,
refresh e dedup. La CI completa sul commit definitivo resta il
criterio di accettazione.

## Limiti

Il keyset previene duplicati e salti da **nuove inserzioni con data
più recente del boundary**. Non garantisce uno snapshot transazionale
se un altro operatore modifica `occurred_on` mentre si pagina; il
browser deduplica gli ID. Ricerche full-text/fuzzy, tuning ILIKE,
benchmark, virtualizzazione lunghi elenchi, DELETE, anagrafiche,
polizze, import legacy e verifica WCAG/Entra produttiva rimangono
nelle issue aperte.
