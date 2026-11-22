# ADR 0086 — Single-flight nelle mutazioni PWA di primo livello

- Data: 22 novembre 2026
- Stato: implementato su `main`; verifica CI completa in corso
- Issue: #13 (P1); correlazioni #21 (P0), #9 (P1)
- Branch `death`: storico in sola lettura, nessun merge

## Contesto e rischio

`App.tsx` gestisce due mutazioni di primo livello: `POST /api/perizie`
(creazione) e `POST /api/files` (upload). Il solo stato React
`busy` disabilita pulsanti *dopo* un render: due eventi `submit`
sincroni, incluso `HTMLFormElement.requestSubmit()`, possono
attraversare entrambi il controllo prima che il bottone sia disabilitato.
Inoltre le due operazioni condividono lo stato Busy, ma non avevano
un mutex sincrono comune. Il backend resta il confine autorevole,
ma una doppia creazione perizia o un doppio tentativo di upload
non devono essere causati dall'interfaccia.

## Decisione

- `activeMutation: useRef<number | null>` è un lock sincrono
  condiviso fra `create()` e `sendFile()`. Si acquisisce **prima**
  del primo `await` e si rifiutano ulteriori POST simultanei.
- `mutationSequence` assegna un identificatore alla singola
  operazione. Il `finally` libera il lock e Busy solo se appartiene
  ancora a quell'operazione, evitando che una vecchia risposta
  sblocchi una nuova sessione.
- Al cambio di token autenticato o alla transizione offline,
  il lock dell'identità precedente viene scartato e Busy resettato:
  la vecchia `Promise` non può alterare la nuova sessione.
- Si mantengono i controlli `requestEpoch` e `activeToken`
  sulle risposte tardive introdotti nel precedente intervento,
  e i fallimenti della richiesta **corrente** restano fail-closed.
- Nessun retry automatico, nessuna coda offline, nessun risultato
  ottimistico; solo una risposta di commit certa può aggiornare
  la proiezione.

## Verifica TDD

- RED: commit [`c436b860`](https://github.com/KeyserDSoze/Peritial/commit/c436b860907b66865272123d39c1003fed6c6368).
  Nuovo test full-stack OAuth/Keycloak desktop e mobile:
  `requestSubmit()` due volte nella stessa task JavaScript,
  doppio click su upload, e tentativo di creazione concorrente
  mentre upload è in corso. Le route bloccano le risposte e
  simulano HTTP 409 senza scrivere file o creare pratiche.
- GREEN candidato: commit [`083102bd`](https://github.com/KeyserDSoze/Peritial/commit/083102bdcae558bae574a3177d545353e39a1130).
  Verifica [CI test-only #45053844398](https://github.com/KeyserDSoze/Peritial/actions/runs/45053844398)
  e [CI fix #45053873934](https://github.com/KeyserDSoze/Peritial/actions/runs/45053873934).
  **Non considerare concluse** le prove fino a conferma Playwright,
  Compose e 9/9 job sull'HEAD finale.

## Confini e lavoro residuo

Il lock riduce errori da doppio evento **nella singola istanza PWA**:
non è idempotenza server, non serializza sessioni/browser/dispositivi,
non arresta una POST già arrivata al backend e non garantisce
exactly-once dopo timeout o connessione persa. Il backend deve
continuare a imporre autorizzazione JWT, RLS, CAS, audit,
idempotenza persistente quando applicabile e controlli di recovery.
La #13 resta aperta per parità UI, accessibilità sistematica
e validazione mobile/produttiva; la #21 resta aperta per le
garanzie distribuite mancanti.
