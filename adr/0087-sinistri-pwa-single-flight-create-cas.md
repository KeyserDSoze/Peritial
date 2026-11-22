# ADR 0087 — Single-flight per creazione ed editing CAS Sinistri nella PWA

- Data: 22 novembre 2026
- Stato: implementazione su `main`, verifiche CI in corso
- Issue: #7 (Sinistri), #13 (PWA), correlazione #24 (unicità XML), #21 (idempotenza/audit)
- `death`: ramo storico, nessun commit, merge o cherry-pick

## Contesto

Il catalogo Sinistri e l'editor CAS avevano solo uno stato React `busy`.
Due `HTMLFormElement.requestSubmit()` nello stesso task possono
invocare due handler prima del render che rende disabled il bottone.
Il catalogo può quindi spedire due `POST /api/sinistri`; l'editor
può spedire due `PUT /api/sinistri/{id}` con la stessa
`expectedVersion`. Il CAS PostgreSQL già impedisce al secondo
aggiornamento di sovrascrivere il primo, ma ciò non rende sicuro
inviare due operazioni non richieste da una singola azione UI.
La creazione Sinistro non ha una garanzia generally exactly-once
fra client e il semplice riferimento esterno non è UNIQUE
per scelta esplicita (#24).

## Decisione

- `SinistroCatalog` usa `createInFlight = useRef(false)`:
  controllo/acquisizione **sincroni** prima del `POST`, con
  release solo in `finally` per l'epoch proprietario.
  Durante il POST si impedisce anche l'avvio di catalog load,
  load-more, cambio filtro e toggle nella medesima istanza.
- `SinistroEditor` usa `saveInFlight = useRef(false)`:
  controllo/acquisizione **sincroni** prima del `PUT`;
  reload e toggle non possono annullare la semantica di una
  richiesta ancora in corso. Release condizionata all'epoch
  proprietario.
- Nel cleanup di token, id e unmount si invalida l'epoch
  e si libera soltanto il ref della vista precedente:
  risultati tardivi non devono ripristinare dati sensibili
  né rilasciare una nuova operazione. Le garanzie
  esistenti sui GET firmati e sulle risposte 409 rimangono.
- Nessun retry implicito, success ottimistico, sequenza offline
  o cambiamento di autorizzazione, audit, RLS o CAS nel backend.

## Regressione

- Commit test-first [`149ec010`](https://github.com/KeyserDSoze/Peritial/commit/149ec010c643feaaab0572f337409838a0eaff3b):
  Playwright Keycloak desktop/mobile ripete due `requestSubmit()`
  sincroni sulla create e sul salvataggio CAS. Route POST/PUT
  intercettate con risposta HTTP 409/400 controllata, senza
  doppia mutazione reale; un seed REST firmato permette
  una verifica dell'editor reale con versione iniziale 1.
- Catalog single-flight [`95925131`](https://github.com/KeyserDSoze/Peritial/commit/95925131477f7a11b086175d00d4abb983a1f220).
- Editor CAS single-flight [`36fc3ab3`](https://github.com/KeyserDSoze/Peritial/commit/36fc3ab39bd8bd99e102e236fdef68193c5c120d).
- RED/GREEN e CI completa saranno riportati sulle issue
  solo dopo verifica dei run GitHub Actions dell'HEAD.

## Limiti e criteri non chiusi

La guardia vale solo per la singola istanza React.
Non sostituisce chiavi di idempotenza server fra dispositivi,
interazione con import XML, arbitraggi PostgreSQL, né
gestione di commit con ACK incerto. Restano aperte
le issue #7, #13, #21 e #24 per parità, produzione,
idempotenza distribuita, test/approvazioni di dominio.
