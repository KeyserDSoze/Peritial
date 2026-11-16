# ADR 0029 — Concorrenza ottimistica sulle mutazioni perizie

- Stato: adottata nel vertical slice `main` il 16 novembre 2026
- Ambito: issue [#9](https://github.com/KeyserDSoze/Peritial/issues/9)
  e [#21](https://github.com/KeyserDSoze/Peritial/issues/21)
- Decisione: compare-and-swap in PostgreSQL con transazione audit atomica

## Problema osservato e prova RED

Il vecchio `PgPeriziaRepository.UpdateRowAsync` aggiornava righe
`tenant_id + id` senza confrontare lo stato dal quale la richiesta era
partita. La sola cancellazione aveva un filtro contro gli stati terminali,
ma un `assign` / `advance` obsoleto poteva riscrivere `Cancelled`.
Doppie assegnazioni concorrenti potevano entrambe creare un audit
`perizia.assign/succeeded`, lasciando soltanto l'ultimo assegnatario.

I quattro test con **due snapshot distinti** e PostgreSQL 17 reale,
aggiunti in `PeriziaAtomicAuditTests`, hanno fallito su quella
implementazione: [CI RED #43879181694](https://github.com/KeyserDSoze/Peritial/actions/runs/43879181694);
141 test Infrastructure esistenti verdi e 4 nuovi test falliti senza
eccezione (lost update): assign dopo cancel, advance dopo cancel,
cancel dopo assign e doppia assign.

## Decisione

1. `Perizia` conserva `PersistedStatus` e `PersistedAssignedTo`, cioè
   i valori caricati da PostgreSQL tramite `Restore` o confermati
   nell'ultimo commit. `Assign`, `Advance` e `Cancel` modificano
   **solo** lo stato corrente dell'aggregato, non la snapshot.
2. Qualunque `UpdateRowAsync`, inclusi i metodi legacy/test-only, usa:
   ```sql
   UPDATE perizie
      SET status=@status, assigned_to=@assignee
    WHERE tenant_id=@tenant
      AND id=@id
      AND status=@expected_status
      AND assigned_to IS NOT DISTINCT FROM @expected_assignee
   ```
   Il predicate viene verificato da PostgreSQL sotto lock di riga;
   le transazioni concorrenti che hanno aggiornato lo stato/assignee
   fanno fallire il secondo aggiornamento.
3. Le mutazioni **auditate** validano anche la transizione richiesta:
   `perizia.assign` soltanto `Registered → Assigned`;
   `perizia.advance` soltanto il successore previsto nello state
   machine; `perizia.cancel` soltanto da stati non terminali.
   Non è ammessa una seconda emissione audit da stato `Cancelled`.
4. In caso di zero righe aggiornate, una ricerca **nello stesso
   contesto di tenant PostgreSQL FORCE RLS** distingue:
   ID invisibile/assente → `KeyNotFoundException`/HTTP 404;
   record visibile ma mutato nel frattempo →
   `PeriziaConcurrencyConflictException`/HTTP **409**.
   Il database fa rollback della transazione e **non** scrive un
   audit di successo per il loser.
5. `ConfirmPersisted` aggiorna la snapshot **solo dopo**
   `CommitAsync` riuscito; nessun avanzamento in memoria della
   snapshot dopo rollback, errore o commit non confermato.
   REST e MCP riusano il medesimo servizio/adapter: MCP rappresenta
   il conflitto come errore del tool, anziché promettere un codice
   HTTP JSON-RPC identico alla REST API.

## Alternative e trade-off

Un `SELECT ... FOR UPDATE` all'inizio di ogni richiesta
serializzerebbe le operazioni ma non risolverebbe da solo le snapshot
già lette dal chiamante, né l'update stale fuori lock. Un campo
`row_version` monotono sarebbe più generale e offrirebbe
un token ETag pubblico, ma richiederebbe un nuovo schema/migrator e
cambiamenti dei contratti client; non è introdotto in questa tranche.

Il confronto status+assigned_to può soffrire di **ABA** se futuri
workflow riportano una pratica esattamente allo stato/assegnatario di
partenza. Lo state machine attuale procede in avanti e gli stati
terminali non tornano indietro; aggiungere un revision counter
monotono prima di introdurre riaperture, riassegnazioni o operazioni
su una pratica in stato invariato.

## Verifiche, limiti e follow-up

- Suite: `tests/backend/Peritial.Infrastructures.Tests/PeriziaAtomicAuditTests.cs`
  include i quattro interleaving stale contro il vero PostgreSQL,
  controlla lo stato persistito finale e l'assenza di audit-success
  della richiesta rifiutata.
- Test precedenti: rollback dell'audit, FORCE RLS cross-tenant,
  transizione terminale, REST/MCP OAuth e replay della cancellazione.
- Non sono ancora coperte race multi-replica su più processi,
  guasti durante commit/ACK, ETag/If-Match esposti al client,
  idempotenza globale, nuove entità o accesso Azure reale.
  #9/#21 non vanno chiuse prima di tali gate, e la concorrenza
  va ridefinita se la state machine aggiunge transizioni inverse.
- Un commit PostgreSQL e la registrazione audit sono atomici;
  i successi/rollback del database **non** implicano
  exactly-once a livello di rete o distribuzione.

`death` è riferimento storico; nessun merge o modifica del branch.
