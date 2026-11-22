# ADR 0088 — Single-flight delle associazioni Perizia–Sinistro nella PWA

- Data: 22 novembre 2026
- Stato: implementato su `main`, verifiche CI RED/GREEN in corso
- Issue: #7, #13; correlate #23 (versione monotona e ABA), #21 (atomicità)
- `death`: archivio storico senza nuovi commit

## Contesto

Il picker `PeriziaSinistroPicker` espone tre mutazioni
firmate e condizionali all'ultima associazione osservata:
`POST /api/perizie/{id}/sinistro` (link),
`DELETE /api/perizie/{id}/sinistro` (unlink) e
`PUT /api/perizie/{id}/sinistro` (reparent).
Le mutation UI sono soggette a CAS `expectedLinkVersion`
e, per unlink/reparent, all'identità della relazione osservata.

Un solo `setBusy` React non fornisce un mutex sincrono.
Due `button.click()` nello stesso task del browser possono
inviare due comandi con lo stesso `expectedLinkVersion`
prima che React disabiliti il controllo. Il secondo comando
non deve tentare di interpretare o sovrascrivere il risultato
del primo, nemmeno se PostgreSQL risponde 409 per CAS.

## Decisione

- Un `inFlight = useRef(false)` è acquisito in modo **sincrono**,
  prima del primo `await`, per ciascuna richiesta GET/search
  o link/unlink/reparent. La guardia è condivisa da tutte le
  operazioni del picker e blocca anche toggle, riassegnazione,
  annullamento e input nel medesimo task.
- `sequence.current` resta il controllo di generazione
  della vista e di invalidazione delle risposte tardive.
  Soltanto l'operazione che detiene ancora la generazione
  libera `inFlight` e `busy` nel `finally`.
- Un cambio di identità/Perizia o una transizione a
  stato terminale invalida la generazione corrente;
  il picker non rende proiezioni confermate da un vecchio JWT.
- Nessuna conferma ottimistica, retry automatico o scrittura
  in coda. Il commit server CAS, l'audit e le regole RLS
  rimangono l'unica fonte autorevole.

## Prove

- Test first [`1802670f`](https://github.com/KeyserDSoze/Peritial/commit/1802670f2dcb189f921b487e9f1c754d1c170e8f):
  test Playwright Keycloak desktop/mobile con dati firmati
  PostgreSQL reali. Ogni mutazione viene intercettata in
  una route di test con risposta 409 e gate esplicito
  per mantenere aperta la prima richiesta, mentre un
  doppio click sincrono tenta di avviarne una seconda.
  La relazione persiste invariata dopo le tre simulazioni.
- Fix [`efa6950c`](https://github.com/KeyserDSoze/Peritial/commit/efa6950c51308b1d5e87cca655182ed7c90394dd):
  mutex sincrono e pulizia condizionata alla generazione.
- Le prove sono catalogate in [Actions RED](https://github.com/KeyserDSoze/Peritial/actions/runs/45059559674)
  e [Actions fix](https://github.com/KeyserDSoze/Peritial/actions/runs/45059593725).
  Non dichiarare il test GREEN né la suite completa riuscita
  senza una conclusione di Actions.

## Limiti

Il mutex vale per **una singola istanza React**, non per
client distinti, tab differenti o richieste MCP/REST dirette.
Non elimina le race distribuite su dati legacy o gli
ACK incerti del backend: `expectedLinkVersion`, CAS SQL,
provenienza audit e le issue #23/#21 restano richiesti.
#7 e #13 restano aperte per copertura funzionale,
accessibilità e rollout oltre il test full-stack locale.
