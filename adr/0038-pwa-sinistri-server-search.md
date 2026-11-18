# ADR 0038 — Ricerca Sinistri nel catalogo PWA, tenant scoped e bounded

- Stato: accettata per l'incremento #7 / #13
- Data: 18 novembre 2026
- Branch di implementazione: `main`; `death` resta read-only storico.

## Contesto

`SinistroCatalog` apriva la lista degli ultimi 50 sinistri via
`GET /api/sinistri?limit=50`. Il backend dispone già di
`GET /api/sinistri/search`, con prefisso letterale, intervallo data evento,
FORCE RLS, scope `peritial.read` e limit 1–50. Filtrare solo gli ultimi 50
nel browser escluderebbe sinistri storici non presenti nella pagina.

## Decisione

- La ricerca nel catalogo è esplicita (non parte durante la digitazione),
  con prefisso obbligatorio 2–100 caratteri e date `occurredFrom` /
  `occurredTo` ISO 8601 facoltative e inclusive, controllate anche
  lato server; intervalli invertiti non vengono inviati.
- `searchSinistri` invia i parametri con `URLSearchParams`; `%`, `_`,
  spazi e simboli non vengono interpolati in SQL. Il repository PostgreSQL
  applica la semantica di prefisso letterale e filtra prima di LIMIT 50.
  La ricerca picker Perizia–Sinistro mantiene cap 20 per non cambiare UX.
- Ricerca, refresh e ripristino senza filtri richiedono una nuova GET JWT,
  con `Cache-Control: no-store`. L'UI indica la ricerca applicata e
  distingue i risultati (max 50) dagli ultimi 50 della vista predefinita.
- Nessun filtro tenant o paginazione è simulato nel client. Solo i record
  già presenti nella finestra di ricerca possono essere riconciliati dopo
  una lettura/modifica confermata da REST; un record appena creato provoca
  una nuova GET della ricerca attiva, non viene iniettato come match fittizio.
  Senza ricerca resta la ricevuta del POST 201 e la vista recent-50.
- I risultati della GET precedente vengono svuotati immediatamente quando
  parte una nuova interrogazione, e le risposte tardive sono scartate con
  epoch. Chiusura catalogo, cambio token e offline invalidano la vista;
  401/403/503 o rete indisponibile bloccano la PWA protetta. Nessuna cache
  tenant/offline o retry di POST/PUT, e CAS 409 invariato.
- Label e testo di aiuto it/en, campi date nativi e ricerca utilizzabile
  in sola lettura; la permission server-side resta autorevole.

## Verifica e limiti

Test Playwright OAuth Keycloak desktop/mobile in
`tests/frontend/authenticated.spec.ts`: tre sinistri con prefisso
contenente `%_`, date differenti e decoy senza caratteri letterali;
verifica query REST, date, limit=50, escaping letterale, refresh del filtro,
azzeramento filtri e invalidazione offline. Il test è stato introdotto
prima del codice UI. Test e build della CI sono l'evidenza definitiva,
non il solo documento.

Non implementa full-text/fuzzy, cursor/paginazione oltre 50, benchmark
su grandi tabelle, filtri per polizze/anagrafiche, DELETE/reparent,
Entra ID produzione o certificazione accessibilità WCAG.
V003/V004 restano migrazioni amministrative esplicite.
