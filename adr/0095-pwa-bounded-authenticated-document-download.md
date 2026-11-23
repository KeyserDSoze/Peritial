# ADR 0095 — Download PWA tramite REST autenticato con lettura bounded

- Data: 23 novembre 2026
- Stato: implementato su main; validazione CI full-stack in corso
- Issue: #11, #13, #5
- Dipende da [ADR 0094](0094-tenant-scoped-audited-document-download.md)

## Contesto

Il backend REST sa leggere un documento tenant-scoped e auditato, ma la
PWA prima mostrava soltanto la proiezione metadata. Un link diretto al
backend non trasporterebbe il JWT in header e non deve includere token in
query string o URL di object storage.

## Decisione

1. Ogni record caricato da /api/files/page offre un pulsante accessibile
   in italiano e inglese; non vengono pubblicati URL di storage o link
   persistenti. La lista resta metadata-only.
2. GET /api/files/{id}/download con JWT nel solo Authorization header;
   redirect error, credentials omit e cache no-store. MIME obbligatorio
   application/octet-stream; Content-Length, quando presente, verificato.
3. Il browser legge lo stream entro la lunghezza metadata già validata
   (intero >0 e <=25 MiB): alloca al massimo il budget di un documento,
   verifica byte esatti anche senza Content-Length e cancella lo stream
   in caso di eccesso/troncamento. Soltanto dopo costruisce un Blob locale
   e un object URL transitorio, revocato dopo l'avvio del salvataggio.
4. Un lock ref sincrono evita doppio GET anche con eventi simultanei.
   Cambio identità/token, offline, filtro, refresh e unmount abortiscono
   la richiesta. Prima di creare il Blob scaricabile, la PWA ricontrolla
   token e rete correnti già in render, epoch e generazione della lista.
   Nessuna coda offline né replay; i 401/403/5xx invalidano la vista.
5. L'audit PostgreSQL document.download/succeeded è ancora soltanto la
   preparazione autorizzata del corpo al confine Business, NON una ricevuta
   certa di salvataggio/completa consegna al browser.

## Prove e limiti

- Playwright full-stack: upload PDF reale, download Bearer con filename e
  byte uguali, doppio click una GET, nessun anchor storage cloud, risposta
  fittizia 200 con lunghezza errata bloccata, catalogo ricaricabile.
- Frontend build e lint TypeScript nella CI; integrazione Keycloak e
  Playwright desktop/mobile affidata al job Compose del HEAD.
- Limiti: un budget per scheda non è un rate-limit server o un limite
  aggregato per tenant/multi-replica. Non risolve quarantine/retention,
  rescan, autorizzazione per ruolo oltre lo scope JWT, revoche nel corso
  di una risposta già iniziata, privacy del filesystem dell'utente,
  Azure/Entra/on-prem di produzione, né trasporto binario MCP.
- death resta ramo storico intatto. Lavoro solo su main.
