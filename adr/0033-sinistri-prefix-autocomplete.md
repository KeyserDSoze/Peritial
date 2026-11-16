# ADR 0033 — Autocomplete Sinistri: prefisso letterale, date, tenant e limiti

**Stato:** Accepted (primo slice di ricerca, #7/#12 ancora aperte).  
**Data:** 2026-11-16

## Contesto

V003 offre un catalogo Sinistri e un indice `(tenant_id, occurred_on DESC,id)`,
ma `ListAsync(limit)` non consente di trovare un riferimento esterno
non incluso nei primi 100 eventi del tenant. Filtrare nella PWA o
in memoria dopo il limite produce falsi negativi, soprattutto
durante l'autocomplete. Nessun criterio di ricerca dal ramo
`death` va importato come semantica di sicurezza.

## Decisione

- Esporre `GET /api/sinistri/search?prefix=...&occurredFrom=YYYY-MM-DD&occurredTo=YYYY-MM-DD&limit=10`
  e il tool MCP `search_sinistri(prefix,occurredFrom?,occurredTo?,limit=10)`,
  entrambi diretti allo **stesso** `SinistroService.SearchAsync`
  con `ITenantContext` derivato dal JWT e
  `peritial.read` oppure `peritial.write`. Le date sono opzionali.
- I filtri di data sono **inclusivi** sulla data evento `occurred_on`,
  non sulla data denuncia. Il prefisso è obbligatorio e normalizzato
  con trim: minimo **2**, massimo **100** caratteri.
  Il limite default è **10**, intervallo **1–50**.
  Data default, intervallo invertito, prefisso nullo/vuoto
  o limite fuori cap vengono rifiutati con **400 REST**
  prima della query.
- Il prefisso è confrontato **case-insensitive** tramite PostgreSQL
  `ILIKE`, non è ricerca fuzzy/full-text. Caratteri `%`,
  `_` e l'escape `!` nell'input sono trattati come
  **letterali**, mai metacaratteri SQL. Si usa il pattern
  parametrizzato con `ESCAPE '!'`: prima `!`→`!!`,
  quindi `%`→`!%` e `_`→`!_`, infine il solo
  wildcard `%` **aggiunto dal server** alla fine.
  Niente interpolazione di input nel SQL.
- PostgreSQL filtra `tenant_id=@tenant`, il prefisso e l'eventuale
  intervallo data **prima** di `ORDER BY occurred_on DESC,id`
  e `LIMIT`, usando la transazione `TenantDbScope` e FORCE RLS.
  Non leggere i primi 50/100 record per poi filtrare nel browser.
  Non esporre tenant ID, subject o metadata di storage nei DTO.
  Le risposte REST/MCP rimangono `Cache-Control: no-store`.
- La tabella e la migrazione **V003 restano intatte**. Gli indici
  esistenti supportano il filtro tenant/data ma non garantiscono
  piani efficienti per ogni prefisso `ILIKE` case-insensitive su
  grandi cataloghi o collations arbitrarie. Prima di dichiarare
  pronta la ricerca per dataset produttivi occorrono benchmark
  `EXPLAIN ANALYZE`, un indice funzionale/trigramma
  **in una nuova migrazione forward-only** se giustificato e
  paginazione cursor; il cap 50 da solo non risolve il costo
  di scansioni ad alta cardinalità.

## Verifica e limiti

TDD RED compilazione prima del nuovo metodo Business; GREEN su
test xUnit Business (normalizzazione, date, scope e limiti),
PostgreSQL reale con due tenant (prefisso misto, wildcard letterali,
filtro server-side prima di limit e controllo date).
Il test Playwright con due JWT Keycloak e due tenant esercita
REST/MCP, `401/403/400`, DTO senza tenant ID, e la mancanza di
righe altrui. Il bootstrap demo V001 non applica V003:
solo CI effimera la carica; gli upgrade reali richiedono il
migrator esplicito.

**Fuori scope:** autocomplete UI PWA, ricerca per compagnia,
polizza, persona o perizia (#8 e #7), testo libero/fuzzy,
paginazione storica e DELETE. Non chiudere #7 né #12.
