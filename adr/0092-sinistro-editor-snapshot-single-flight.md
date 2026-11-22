# ADR 0092 — Snapshot autenticato dell'editor Sinistro single-flight

- Data: 22 novembre 2026
- Stato: implementato su `main`; CI RED/GREEN da verificare
- Issue: #7 Sinistri, #13 PWA; si affianca ad ADR 0087, 0090 e 0091
- `death`: solo archivio storico

## Rischio e decisione

L'editor Sinistro consente il PUT CAS soltanto dopo una lettura firmata
`GET /api/sinistri/{id}` che restituisce `version` e dati
autorizzati dal tenant. Il salvataggio era già protetto dal ref
sincrono `saveInFlight`, ma `reload()` e `toggle()`
controllavano solo `busy` React. Con due click nello stesso task,
prima del render, potevano partire **due GET identiche**.

Il precedente epoch scartava la prima risposta se la seconda richiesta
era iniziata, ma questa seconda richiesta superflua diventava l'unica
capace di sbloccare lo snapshot: un esito tardivo o fallito poteva
impedire l'editing nonostante una lettura precedente valida.
La protezione di epoch non equivale all'arbitraggio di avvio.

La PWA ora utilizza un `readInFlight` distinto dal lock
`saveInFlight`, acquisito sincronicamente **prima di await**
per l'operazione `reload()`. Ingresso del toggle e del
salvataggio CAS sono sospesi mentre una GET è in volo;
la risposta è accettata solo con `epoch.current` corrente.
Solo il suo `finally` valido rilascia il lock. La cleanup su
ID/token/unmount invalida entrambi i lock e l'epoch, in modo
che una risposta del contesto vecchio non ripristini dati.

Non cambia alcun endpoint o requisito di autorizzazione.
La GET continua a usare il token OAuth e non viene memorizzata
offline; la conferma di salvataggio resta esclusivamente la PUT CAS
riuscita, senza replay automatici. HTTP 404/503 rimangono fail-closed.

## Copertura

- [Test-first `9bbf55fe`](https://github.com/KeyserDSoze/Peritial/commit/9bbf55feab908ead41d0693e72ea3c2804f840b9): Playwright Keycloak reale desktop/mobile crea un Sinistro
  persistente via REST JWT, lo trova nel catalogo e fa due
  click sincroni sul pulsante «Modifica sinistro» mentre la
  prima GET è bloccata. La rete deve ricevere **una sola**
  GET, la versione 1 deve diventare visibile e una successiva
  chiusura/riapertura deve eseguire una nuova GET autorizzata.
- [Fix `8e43dc23`](https://github.com/KeyserDSoze/Peritial/commit/8e43dc2327557fc5719dd38f3b0bc5df52a89945):
  guardia sincrona sulle letture dell'editor e lifecycle cleanup.
- Verificare gli esiti in
  [Actions test-only #45069748186](https://github.com/KeyserDSoze/Peritial/actions/runs/45069748186)
  e nella pipeline del fix/HEAD. RED/GREEN e 9/9 da
  certificare **solo dopo** la conclusione del run relativo.

## Limiti

Il lock vale per la singola istanza React. La garanzia contro
lost update rimane lato PostgreSQL tramite `expectedVersion`
e audit atomico; le modifiche fatte da altri client dopo
la GET possono ancora causare legittimamente HTTP 409.
L'intero catalogo, l'accessibilità e il rollout restano
tracciati in #7 e #13.
