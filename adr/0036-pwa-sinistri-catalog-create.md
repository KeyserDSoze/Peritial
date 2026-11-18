# ADR 0036 — Catalogo e creazione Sinistri nella PWA, caricati su richiesta

**Stato:** Accepted, primo slice PWA Sinistri (#7/#13 aperte).  
**Data:** 2026-11-18

## Contesto

La PWA può già collegare una Perizia a un Sinistro esistente, ma non
crea sinistri e non espone un catalogo dedicato. La vista principale
continua a richiedere soltanto il GET delle perizie e dei metadati
documentali, per non bloccare l'esperienza sugli ambienti Compose demo
con **V001 non journaled**, privi di V003/V004 fino alla migrazione
amministrativa esplicita.

## Decisione

- Aggiungere `SinistroCatalog` sotto la lista delle Perizie, **chiuso
  inizialmente**. L'utente sceglie quando leggere
  `GET /api/sinistri?limit=50` con JWT corrente: i risultati
  sono la proiezione allowlist `SinistroDto` ordinata/filtrata
  lato PostgreSQL con `TenantDbScope` e FORCE RLS.
  Nessun recupero automatico di tutti i tenant, nessun cookie
  o tenant ID nel client, niente filtri post-limit in browser.
- Quando il token OIDC include `peritial.write`, mostrare
  un form con riferimento esterno (1–100 caratteri dopo trim),
  data evento obbligatoria `YYYY-MM-DD` e data denuncia
  facoltativa. Inviare **solo al submit esplicito**
  `POST /api/sinistri` col body
  `{externalReference,occurredOn,reportedOn}`: mai consentire
  di specificare `tenantId` o ID server. L'abilitazione del
  pulsante è **solo UX**; la policy OAuth e il Business
  con audit atomico restano i controlli di sicurezza.
- Solo una risposta **201 confermata** modifica la lista PWA,
  usando l'oggetto restituito e non un record ipotetico;
  un `400` mostra un messaggio senza creare righe locali.
  Dopo un errore di rete/503/401/403, cancellare la vista
  protetta tramite il percorso fail-closed di `App`,
  senza replay automatico di POST incerti.
- La lista visualizza al massimo 50 voci e non è una
  ricerca completa; il recente POST confermato è visibile
  fino al prossimo refresh server-side. Sul reload
  la lista viene **riletta con JWT** e non ricostruita
  da sessionStorage/LocalStorage. Il componente scarta
  risultati tardivi in caso di token modificato,
  chiusura o unmount. La PWA non usa CacheStorage
  per GET/POST autenticati e non accoda mutazioni offline.
- Modulo, pulsanti, etichette e messaggi disponibili in
  **italiano e inglese**, con controlli HTML
  `type=date`, `required`, `maxLength`, `role=alert`
  e stato `busy`. `reportedOn` resta opzionale;
  nessuna regola temporale nuova viene inventata
  rispetto alla V003.

## Verifica

La suite `tests/frontend/authenticated.spec.ts` richiede:
login PWA via **Authorization Code + PKCE**, apertura
esplicita del catalogo, creazione con date e prefisso,
fallimento 400 senza modifica locale, POST 201 con
DTO privo di `tenantId` e `Cache-Control: no-store`,
GET server per conferma, refresh pagina e persistenza,
offline senza catalogo né controllo di mutazione,
ripresa tramite nuovo fetch autenticato. I test
OAuth REST/MCP e PostgreSQL già coprono tenant
cross-isolation, audit e autorizzazioni della
creazione Sinistri.

## Limiti

Non implementa aggiornamento `PUT` con CAS dalla PWA,
cancellazione, filtri avanzati, autocomplete interattivo
per tutto il catalogo, cursor/paginazione oltre 50,
legami polizze/anagrafiche, import XML legacy o
produzione Entra ID. La PWA offre soltanto
**lista limitata + creazione** e il precedente selettore
di collegamento Perizie–Sinistri. La V003/V004 rimane
una migrazione amministrativa da eseguire prima
di usare queste funzioni in ambiente reale.
Non chiudere #7 o #13.
