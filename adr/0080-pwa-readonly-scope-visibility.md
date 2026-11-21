# ADR 0080 — La PWA distingue letture e mutazioni in base allo scope OIDC

- Data: 22 novembre 2026
- Stato: implementato su `main`; CI end-to-end del commit da verificare
- Issue: #13 (PWA UX/a11y), #4 (OAuth security), #9 (workflow), #11 (documenti)
- Branch `death`: archivio storico, mai modificato

## Contesto

`SinistroCatalog` e `PeriziaSinistroPicker`
già ricevono un flag `canWrite` derivato da
`auth.user.scope`. Nella stessa PWA, invece,
`App` mostrava il form di creazione Perizia e
l'upload documenti a un lettore, e `PeriziaWorkflow`
offriva assegnazione, avanzamento e cancellazione
indipendentemente dallo scope. I backend REST/MCP
già negavano queste richieste con `403`.

Un controllo mutativo visibile ma sempre destinato
a fallire confonde gli operatori e rende la PWA
incoerente con il suo modello di ruoli.

## Decisione

Usare un unico hint `canWrite` calcolato con
`peritial.write` presente tra gli scope OIDC.
Quando è false:

- Non renderizzare creazione Perizia, input file,
  selezione Perizia da caricare o pulsante upload.
- In `PeriziaWorkflow` mostrare riferimento, stato
  e assegnatario, ma non assegnazione, avanzamento
  o annullamento. Il metodo di mutazione ha anche
  una guardia per chiamate di UI fuori sequenza.
- Lasciare disponibile la lettura dei metadati
  documentali con filtro e paginazione.
- Lasciare i componenti di catalogo Sinistri e
  picker in modalità di sola lettura usando il
  medesimo hint, già supportato.
- Quando è true, non cambiare flussi e conferme
  delle mutazioni già disponibili.

**Importante:** è solo UX, non controllo di accesso.
La PWA non decodifica o valida il JWT come fonte di
trust; il server REST/MCP continua a validare issuer,
firma, audience, tenant e scope, e ad applicare FORCE
RLS. Lo scope presente nel browser può essere
falsificato: non concede mai privilegi nuovi.

## Verifiche

Un test Playwright full-stack, desktop e mobile:

1. Prepara una Perizia con un JWT writer Keycloak.
2. Effettua login reale via Authorization Code + PKCE.
3. Sostituisce `access_token` e `scope` nella sola
   sessionStorage di test con un bearer reader firmato
   dello stesso soggetto e tenant (mai nella produzione).
4. Verifica GET con il bearer reader e presenza della
   Perizia, documenti consultabili, nessun bottone
   crea/upload/assegna/avanza/annulla e nessuna
   creazione Sinistro dal catalogo.
5. Conferma REST `403` su POST usando il reader.
6. Ripristina la sessione PKCE writer e attesta la
   ricomparsa dei controlli senza alterare i dati.

I test esistenti di OAuth REST/MCP, workflow, offline,
concorrenza e PWA rimangono obbligatori.

## Non obiettivi

Non aggiunge permessi, feature di IAM reale,
download documenti, audit retroattivo, ruoli
organizzativi, client OAuth extra in produzione,
né nuove operazioni REST/MCP. #13 rimane aperta
per parità funzionale e accessibilità residue.
