# ADR 0037 — Modifica Sinistri nella PWA con controllo CAS e reload esplicito

**Stato:** Accepted (incremento parziale #7/#13).  
**Data:** 2026-11-18

## Contesto

La V003 e il backend REST/MCP possiedono già `SinistroService.UpdateAsync`
con `expectedVersion`, SQL compare-and-swap e audit `sinistro.update`
atomico. La PWA implementa catalogo e creazione, ma non espone la
modifica: sovrascrivere campi da un elenco potenzialmente vecchio
senza verifica del numero di versione produrrebbe perdita di aggiornamenti
tra operatori, anche se il backend dispone di CAS.

## Decisione

- Aggiungere `SinistroEditor` in ogni riga del
  `SinistroCatalog`, **chiuso per default** e visibile
  soltanto con scope OIDC `peritial.write`. Il controllo
  nella UI non sostituisce la policy server: il backend
  verifica JWT, tenant, scope e FORCE RLS.
- All'apertura eseguire un **GET firmato e autoritativo**
  `/api/sinistri/{id}`. Non assumere che il
  `version` dell'elenco sia attuale. Solo il DTO
  restituito dal GET inizializza i campi del form:
  `externalReference`, `occurredOn`, `reportedOn`
  (opzionale) e `version` da mostrare all'operatore.
- Salvare esplicitamente via
  `PUT /api/sinistri/{id}` con
  `{externalReference,occurredOn,reportedOn,expectedVersion}`,
  usando **esclusivamente il numero di versione
  dell'ultimo GET/PUT riuscito**. Il client non
  incrementa autonomamente la versione, non legge la
  versione più nuova per sovrascrivere modifiche altrui
  e non invia un `tenantId`.
- Soltanto un **200 confermato** aggiorna la riga
  e la snapshot di lavoro. I risultati vengono
  ordinati per `occurredOn DESC,id` e limitati
  alle 50 voci del catalogo. Nessuna scrittura
  ottimistica o audit fittizio nella PWA.
- Per **400** mantenere il form e la versione
  originale, mostrare un errore di validazione e
  non mutare la riga. Per **409 CAS**, invalidare
  immediatamente la snapshot e i campi del
  form, mascherare nella lista i valori non
  più affidabili e disabilitare il salvataggio.
  Mostrare «Ricarica sinistro» che effettua un
  **nuovo GET firmato**, riporta a schermo
  la versione vincente e consente solo allora
  un nuovo `PUT`. Non implementare replay,
  retry automatici o merge ciechi.
- Un **404** sul GET o PUT significa che
  la risorsa non è più visibile: invalidare
  la vista protetta, come per 401/403,
  errore di rete, timeout o 5xx. Lo stesso
  vale per offline: non conservare le
  risposte autenticata in CacheStorage o
  in una coda di mutazioni. Un epoch locale
  ignora risposte di richieste precedenti
  dopo chiusura, cambio JWT o unmount.
- Form e feedback accessibili tramite
  etichette, `role=alert` e `role=status`,
  messaggi completi in italiano e inglese.

## Verifiche

Test Playwright con login PWA PKCE autentico
desktop/mobile e PostgreSQL V003:
creazione iniziale via REST, apertura
dell'editor e verifica della versione 1,
simulazione PUT 400 senza modifica visibile,
PUT riuscito da v1→v2 con payload
`expectedVersion:1`, verifica del GET
autoritativo; modifica concorrente da
secondo client a v3, PUT stale 409,
nascondimento dei dati non affidabili,
assenza di save/ripetizione automatica,
ricarica esplicita e successivo
PUT con `expectedVersion:3` a v4,
persistenza al refresh. La suite backend
esistente copre già CAS PostgreSQL e
audit atomico dell'update.

## Limiti

L'incremento non introduce database migration,
nuovi scope o logica di business: riusa
V003 e REST. Non implementa eliminazione,
paginazione oltre 50, ricerca avanzata/fuzzy,
assegnazioni di polizze/anagrafiche, import
legacy o certificazione Entra/WCAG.
Il cambio di sinistro di una perizia rimane
vietato (relazione V004 one-shot).
#7 e #13 restano aperte.
