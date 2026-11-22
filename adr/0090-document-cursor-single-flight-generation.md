# ADR 0090 — Continuazione documenti single-flight e generation fencing

- Data: 22 novembre 2026
- Stato: implementato su `main`, CI RED/GREEN da verificare
- Issue: #13 (PWA), correlate #11 (catalogo documentale), #4 (OAuth)
- Branch `death`: archivio storico, nessuna modifica

## Contesto

La PWA usa `GET /api/files/page` con cursore opaco, perizia
facoltativa e Bearer JWT per caricare altri metadati documentali.
L'interfaccia disabilitava il pulsante con `documentsLoadingMore`,
ma `setState` non è una guardia sincrona. Due `click()`
nello stesso task JavaScript possono spedire due GET con
lo stesso cursore prima che React aggiorni il DOM.

La deduplica per UUID in fase di append non elimina la race
fra `nextCursor` concorrenti. Un risultato tardivo può
ripristinare un cursore obsoleto e rischiare salti o replay
di pagine. Un refetch base causato dal cambio lingua, a
parità di token ed `requestEpoch`, deve ugualmente rendere
obsolete le pagine aperte sul catalogo precedente.

## Decisione

- Un lock `activeDocumentsPage: useRef<number | null>`
  è acquisito sincronicamente per una continuazione, con
  identificatore monotono `documentsPageSequence`.
  Un secondo evento nello stesso task ritorna senza inviare
  una nuova richiesta.
- Il risultato è accettato solo se coincidono: operazione
  ancora proprietaria del lock, `documentsProjectionGeneration`,
  `requestEpoch` e JWT firmato corrente. La sola
  operazione proprietaria può liberare il lock in `finally`.
- Ogni nuova GET della **prima pagina** incrementa la
  generazione della proiezione e cancella la continuation
  precedente. Refresh, cambio filtro, token, offline e
  UI fail-closed azzerano anche il lock/pulsante di paging,
  senza trattare una vecchia risposta come autorizzazione
  a ripristinare documenti.
- Nessuna cache runtime di API/tenant, retry automatico,
  modifica alla paginazione PostgreSQL o esposizione di
  chiavi di storage/download.
- Un cursore rifiutato resta fail-closed nella vista:
  metadata parziali e cursore scartati, richiesta esplicita
  di nuova lettura firmata. Invece un risultato
  **obsoleto** non altera una vista più recente.

## Verifiche

- Test-first [`5f5f5a5f`](https://github.com/KeyserDSoze/Peritial/commit/5f5f5a5fc50f60a016ab4509d863f52c73b18647):
  Keycloak reale in Playwright desktop/mobile, risposta
  della prima pagina intercettata, due click sincroni
  su `Carica altri documenti` e gate che trattiene la
  seconda pagina. La verifica esige **una sola GET**
  con quel cursore, un'append visibile e nessun
  link/URL di storage nei metadata.
- Fix [`68891bcc`](https://github.com/KeyserDSoze/Peritial/commit/68891bcc756f492cd270458fc42b1f06f77c1bba):
  lock per operazione e generation fencing, senza
  alterare il contratto REST.
- Eseguire e verificare il RED/GREEN in GitHub Actions:
  [test-only #45062738787](https://github.com/KeyserDSoze/Peritial/actions/runs/45062738787),
  [fix #45062794465](https://github.com/KeyserDSoze/Peritial/actions/runs/45062794465).
  Non dichiarare CI 9/9 prima della conclusione del run.

## Limiti

La single-flight garantisce una richiesta per gesto della
**singola vista**. Non garantisce lo snapshot di più pagine
in PostgreSQL contro mutazioni concorrenti di un altro
client; il server resta responsabile di controllo cursori,
tenant, autenticazione, ordinamento e consistenza.
Resta necessaria la copertura accessibilità e release PWA
completa (#13) e il download autorizzato/auditato (#11).
