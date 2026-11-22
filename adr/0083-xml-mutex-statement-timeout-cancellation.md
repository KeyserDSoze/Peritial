# ADR 0083 — Timeout del mutex XML e cancellazione del chiamante

- Data: 22 novembre 2026
- Stato: implementato su `main`, CI del commit da attestare
- Issue: #24 (P0), #10, #21, #6
- Branch `death`: sola storia, mai modificato

## Contesto

L'ADR 0082 introduce un `lock_timeout` limitato
a 1500 ms nella **sola** acquisizione del mutex
V012 da parte dell'import XML case-only interno.
L'operazione può terminare con due SQLSTATE diversi:

- **55P03**: `lock_timeout` scaduto durante la
  richiesta di advisory lock
- **57014**: query cancellata perché un DBA ha
  configurato `statement_timeout` più breve
  del timeout del mutex oppure per altro motivo
  lato PostgreSQL

La versione precedente trasformava soltanto
55P03 in `LegacyXmlImportBusyException`.
Con un timeout di statement più breve il chiamante
riceveva invece la `PostgresException` non
classificata e poteva confondere disponibilità
e duplicazione dell'identità XML.

## Decisione

All'interno del solo `try/catch` che esegue
`SELECT pg_advisory_xact_lock(...)`, riconoscere
55P03 e 57014 se e soltanto se il token del
chiamante **non** è stato cancellato. Esporre
`LegacyXmlImportBusyException` senza contenuti
sensibili; lasciare propagare eventuali
cancellazioni richieste dal client.
Non prendere decisioni di retry, non classificare
come 409/reference conflict e non intercettare
gli stessi SQLSTATE nelle altre query.

Dopo un fallimento l'intera transazione si
annulla prima della prenotazione dell'import
ledger: 0 Sinistri, 0 audit successo e 0 key.
`SET LOCAL lock_timeout` viene ripristinato
dopo acquisizione riuscita; quando la SELECT
fallisce il rollback della scope azzera i
setting transazionali. Le altre operazioni
REST/MCP e il trigger V012 mantengono la propria
politica PostgreSQL. Non è aggiunto un timeout
globale.

## Prove

Nel test `SinistroPersistenceTests`, PostgreSQL
17 reale con due pool con credenziali applicative:

1. Sessione A trattiene
   `pg_advisory_xact_lock` su tenant/reference
2. Pool B avviato con `-c lock_timeout=350ms`
   osserva davvero SQLSTATE 55P03
3. Pool B avviato con
   `-c statement_timeout=350ms` osserva
   davvero SQLSTATE 57014
4. L'import interno sui due pool solleva
   `LegacyXmlImportBusyException`; in
   entrambi i casi non scrive ledger,
   Sinistro o audit succeeded
5. Il pool conserva i timeout iniziali,
   e dopo il rilascio del mutex la stessa
   key viene creata e poi riprodotta
   idempotentemente
6. Un `CancellationToken` cancellato
   dal client prima dell'operatore timeout
   non si traduce in Busy e non produce
   scritture parziali

## Limiti e governance

L'errore 57014 può derivare anche dalla
cancellazione amministrativa di una query;
in questa sola fase viene rappresentato
come mancata acquisizione temporanea,
non come prova di un duplicato o invito a
retry automatico. La mappatura HTTP,
le metriche, i limiti di carico e le
deadlock multi-reference richiedono policy
prima di abilitare import. Nessun commit
XML viene esposto a REST/MCP.

Restano aperte #24 e le issue collegate
per identità XML, provenance, campi temporali,
allegati e requisiti di produzione.
