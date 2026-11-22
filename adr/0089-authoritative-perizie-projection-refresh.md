# ADR 0089 — Un GET autenticato riconcilia la visibilità delle Perizie

- Data: 22 novembre 2026
- Stato: implementato su `main`; CI completa RED/GREEN in verifica
- Issue: #13 (PWA), correlate #4 (OAuth), #9 (workflow)
- Ramo `death`: riferimento storico intatto

## Problema

La PWA recupera `GET /api/perizie` e la pagina dei documenti con il JWT
corrente. Un refetch dopo refresh, cambio di filtro o ritentativo
deve considerare la **nuova proiezione restituita dal server**
come unica fonte autorevole della membership delle Perizie.

La riconciliazione precedente manteneva anche le righe
locali `!incoming.has(item.id)`, indipendentemente dal motivo
per cui il GET non le includeva. Un record precedentemente
mostrato poteva quindi sopravvivere alla sua rimozione
dall'elenco corrente, lasciando visibile la vecchia riga
con i controlli workflow e la relazione Sinistro.

La PWA non può sapere perché l'endpoint ometta una riga
(cancellazione, policy, cambi di autorizzazione o altra
variazione del catalogo): deve **smettere di visualizzarla**
alla successiva lettura riuscita. Un errore di autenticazione
o trasporto continua invece a seguire il percorso fail-closed
già esistente e non viene trattato come elenco vuoto.

## Decisione

- Al successo del `GET`, includere esclusivamente le
  Perizie presenti nella risposta **più** quelle il cui
  comando locale è stato confermato *dopo l'inizio* di
  quella richiesta: `changedAfterFetch`.
- Non mantenere righe semplicemente perché erano nello
  stato React prima del refresh.
- Conservare la protezione dalle risposte obsolete tramite
  `requestEpoch`, `activeToken` e cleanup `useEffect`;
  le risposte di mutazioni più recenti restano prioritarie
  rispetto a un GET avviato prima del loro commit.
- I selettori upload e filtro documenti vengono riallineati
  agli ID presenti nella nuova lista firmata, come già
  previsto dal componente, e la PWA non resta in stato
  `unavailable` quando entrambe le GET sono riuscite.
- Nessuna memorizzazione offline di record tenant, retry
  di POST o nuova autorizzazione dedotta dal browser.

## Test TDD

1. [Commit RED `eacfee1c`](https://github.com/KeyserDSoze/Peritial/commit/eacfee1c33d55f447c0223ce60854803c0b79ff9).
   Keycloak autentica un utente reale: si creano due Perizie,
   si seleziona una nei due controlli documentali e la
   si vede nella lista. Una route Playwright effettua un
   vero GET REST JWT e simula sul response body la
   successiva omissione della prima Perizia.
   Dopo refresh, la riga e le opzioni devono sparire,
   l'altra riga resta e non viene mostrato un errore API.
   Un ulteriore GET REST non alterato ripristina la riga.
2. [Commit fix `35d6fa7e`](https://github.com/KeyserDSoze/Peritial/commit/35d6fa7eeba41f9ca645a6dfa695023f06d660d4).
   Rimozione dell'unione indiscriminata dei record assenti.
3. Risultati GitHub Actions: [test-only #45060443466](https://github.com/KeyserDSoze/Peritial/actions/runs/45060443466)
   e [fix #45060457123](https://github.com/KeyserDSoze/Peritial/actions/runs/45060457123).
   Esiti RED/GREEN e CI 9/9 **da verificare** prima
   di dichiarare il criterio completato.

## Limitazioni

L'endpoint REST rimane la fonte di autorizzazione: questo
intervento non introduce record-level ACL né revoca token;
non risolve il bisogno di verifica di un OIDC reale in
produzione (#4). Un browser che rimane online ma non
riceve nuovi GET non può conoscere in tempo reale una
revoca intervenuta sul server. Restano aperti la
parità PWA, l'accessibilità e il rollout (#13).
