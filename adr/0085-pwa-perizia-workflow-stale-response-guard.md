# ADR 0085 — Guardie di ciclo di vita per il workflow Perizie PWA

- Data: 22 novembre 2026
- Stato: implementato su `main`; CI completa da verificare
- Issue: #13 (P1), #9 (P1), #21 (P0), #4 (P0)
- Branch `death`: storico intatto

## Problema

Il parent `App.tsx` smonta e ricostruisce la
proiezione tenant quando un utente effettua
refresh autenticato, cambia token o va
offline. I componenti catalogo Sinistri e
picker assoc. usano epoch di invalidazione,
ma `PeriziaWorkflow.tsx` invocava sempre
`onUpdated` e `onUnavailable` dopo
`await` dei metodi `assign/advance/cancel`
o del GET di reload.

Una POST lenta può terminare con HTTP 503
*dopo* che l'utente ha ricaricato con successo
una vista fresca. La callback del componente
smontato svuota erroneamente l'intera nuova
proiezione attraverso `lockProtectedUi`.
Una POST 200 tardiva può invece sovrascrivere
lo stato letto dal GET più recente. Questo
è particolarmente pericoloso durante
cambi di sessione, API down e conflitti.

## Decisione

- Usare una revisione locale `requestEpoch`
  incrementata prima di ogni richiesta e
  nel cleanup di `useEffect`.
- Il cleanup invalida anche la flight aperta
  quando cambiano id, stato, assegnatario,
  token o il componente viene smontato.
- Soltanto l'ultima richiesta ancora attiva
  può invocare callback al parent, mostrare
  errori, cambiare conferme e liberare Busy.
- Usare un guard sincrono `inFlight` per
  evitare doppie POST in una stessa render
  frame. Niente retry o accodamenti.
- Una POST che riceve 200 dopo il refresh
  è stata eventualmente committata dal
  backend, ma non può mutare la nuova vista;
  un GET esplicito successivo ne ricostruisce
  il risultato autorevole.

Questa misura è lato presentazione.
Lo scope JWT firmato, l'isolamento RLS,
CAS e audit PostgreSQL non cambiano.

## Test ed evidenze

Playwright full-stack con Keycloak e
PostgreSQL reali, desktop/mobile:

1. Creazione reale della Perizia autenticata.
2. `POST /assign` intercettata e trattenuta.
3. Nuovo `GET /api/perizie` completato
   e workflow precedente smontato.
4. Vecchia risposta 503 rilasciata:
   la schermata rimane verificata e
   consente nuove operazioni.
5. Seconda richiesta trattenuta: il GET
   vede lo stato vecchio, poi la POST viene
   committata davvero (HTTP 200).
   La vecchia callback non sostituisce il
   risultato del refresh e la successiva
   lettura firmata mostra il commit.

## Limiti

Non costituisce cancellazione delle POST
già inviate né garanzia di atomicità di
sistemi esterni; non esegue replay di
operazioni. Mancano ancora audit WCAG
completo, aggiornamenti PWA multi-tab,
benchmark UX e validazione del rollout
su ambienti reali. #13 resta aperta.
