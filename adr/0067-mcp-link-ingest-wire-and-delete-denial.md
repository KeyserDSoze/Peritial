# ADR 0067 — Wire MCP di link CAS e ingestion, cancellazione negata

- Data: 21 novembre 2026
- Stato: implementato su `main`, CI da verificare
- Issue: #12, #5, #11, #21, #22, #23
- `death` resta solo come archivio

## Decisione

Dopo 20 tool con prove JSON `tools/call` riuscite
(ADR 0066), il test `tests/frontend/mcp-wire-output.spec.ts`
esegue i quattro successivi mutatori con JWT Keycloak writer:

- `link_perizia_sinistro`: unlinked, CAS revision 1→2;
- `reparent_perizia_sinistro`: sorgente attesa e nuova
  destinazione, unica transazione CAS 2→3;
- `unlink_perizia_sinistro`: revisione attesa 3→4,
  `sinistroId` null/omesso come nullable;
- `ingest_file`: PDF sintetico base64 con
  `idempotencyKey`, ClamAV e S3 reali, ritorno DTO
  senza storage key e retry identico.

Ogni risposta viene confrontata con il `returnType`
del catalogo MCP v1.2 e l'allowlist v1.0, quindi con
la lettura REST firmata dello stato persistito.
La versione di link obsoleta 1 deve produrre un errore
dopo la sequenza CAS 1→4; nessun ABA silente.
Le stesse quattro richieste con token `peritial.read`
devono essere rifiutate senza modificare l'associazione.

## Cancellazione Sinistri

`delete_sinistro` è intenzionalmente escluso dai
test di risposta positiva: il gate di hard-delete
richiede protezioni di retention, legal hold, approval
e recovery ancora aperte nella #22.
Il test verifica che un normale writer venga negato
(`isError:true`) e che i Sinistri restino disponibili.
Non usare credenziali privilegiate o attivare
feature gate in CI per forzare un esito positivo.

## Perimetro e limiti

La copertura è **24/25 tool con output MCP di successo
verificato**, **1/25 (delete) con prova negativa**.
Si estende solo la suite di test, la documentazione e
il registro decisionale: nessuna modifica a API,
business rules, database, audit, tenant enforcement,
configurazioni o allo storico `death`.

Non si dichiara completo #12: mancano contratti
`outputSchema` del SDK formalmente comparati,
negoziazione delle versioni e integrazione di client
esterni, oltre agli altri use case futuri.
