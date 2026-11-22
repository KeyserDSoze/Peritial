# ADR 0091 — Letture Sinistri PWA single-flight per refresh e keyset

- Data: 22 novembre 2026
- Stato: implementato su `main`; evidenze CI RED/GREEN da verificare
- Issue: #7 (Sinistri), #13 (PWA)
- `death`: archivio storico, non modificato

## Problema

La PWA carica il catalogo Sinistri tramite
`GET /api/sinistri/page?limit=50` e, con ricerca attiva,
`GET /api/sinistri/search/page` con cursore opaco.
`SinistroCatalog` disabilitava i controlli usando soltanto
lo stato `busy` React, mentre `create` POST disponeva già
di una guardia sincrona dedicata `createInFlight`.

Due click sul pulsante di continuazione o di refresh
nello stesso task del browser possono entrare in
`loadMore()`/`load()` prima del nuovo render: due GET con
lo **stesso cursore** o due letture del primo blocco
partono simultaneamente. La deduplica degli ID in `setRows`
non garantisce ordine, validità di `nextCursor`, né
coerenza di errore fra due letture sovrapposte.

## Decisione

- Un `readInFlight = useRef(false)` condiviso tra
  `load` e `loadMore` è acquisito in modo sincrono
  all'ingresso di ogni GET, prima di qualsiasi `await`.
  Nessuna seconda lettura viene avviata mentre la prima
  detiene il lock.
- Il controllo è applicato anche a toggle, form di ricerca,
  cancellazione filtro e creazione: il cambio di filtro o
  una POST non può sovrapporsi alla lettura del catalogo
  ancora in volo. La guardia POST precedente resta attiva.
- Il `finally` rilascia `readInFlight` e `busy` solo se
  la richiesta possiede ancora `epoch.current`. Al cambio
  token/unmount, la cleanup invalida l'epoch e azzera il
  lock, impedendo a una risposta vecchia di ripristinare
  righe o liberare una richiesta di un'altra generazione.
- Resta invariato il comportamento fail-closed per GET
  scaduta, errore trasporto/API, cursor invalido e nessuna
  paginazione/replay offline. Il server, non il browser,
  è autorevole per tenant, ordinamento, RLS e keyset.

## Test

Il test [`1b35e382`](https://github.com/KeyserDSoze/Peritial/commit/1b35e3827eeb4fda08a2d164797eaa519e0a3755)
usa Keycloak reale in Playwright desktop/mobile, un catalogo
con due pagine intercettate solo nelle risposte REST e un
gate che mantiene la GET in volo. Due click sincroni su
`Carica altri sinistri` devono produrre **un solo GET**
con lo stesso cursore. Dopo l'append della seconda riga,
due click sul refresh devono produrre **una sola GET**
di base; la riga della seconda pagina deve sparire e la
prima restare visibile.

La correzione è nel commit
[`b2e1724e`](https://github.com/KeyserDSoze/Peritial/commit/b2e1724e5ab48f0a22c957d25a9ca16e61c63755).
La verifica test-first e la verifica del fix sono su
[Actions test-only #45066082969](https://github.com/KeyserDSoze/Peritial/actions/runs/45066082969)
e [Actions fix #45066113157](https://github.com/KeyserDSoze/Peritial/actions/runs/45066113157).
Non attestare RED/GREEN o 9/9 prima di aver controllato
gli esiti dei job.

## Limiti

Il lock vale per la singola vista React, non sostituisce
il controllo distribuito di concorrenza in PostgreSQL,
l'ACL OAuth o la verifica dei cursori lato server.
Non prova la consistenza di snapshot fra più pagine sotto
scritture concorrenti di client diversi.
#7 e #13 restano aperte per la parità funzionale, il
rollout, l'accessibilità e altri requisiti residui.
