# ADR 0076 — `CreatedAtUtc` canonico per nuovi aggregate PostgreSQL

- Data: 21 novembre 2026
- Issue: #26, #12, #7, #9, #21
- Stato: implementato su `main`; CI del commit da verificare
- `death`: non modificato

## Problema verificato

In [CI #44949476077](https://github.com/KeyserDSoze/Peritial/actions/runs/44949476077)
MCP `create_perizia` serializzava `createdAtUtc`
`2026-11-21T21:05:17.4168791Z`; REST dopo
persistenza PostgreSQL rendeva
`2026-11-21T21:05:17.416879Z`.
La differenza è un tick da 100 ns fra `DateTime`
in-memory e la granularità microsecondo PostgreSQL.
La CI precedente #44949957436 con tolleranza 1 µs
è verde, ma non risolve la disuguaglianza dei due output.

## Decisione

`PersistedUtcTimestamp.ForNewRecord(DateTime timestampUtc)`
nel dominio:

1. Accetta solo `DateTimeKind.Utc`; non converte
   implicitamente Local/Unspecified.
2. Tronca verso il basso i tick a multipli di 10
   (1 µs = 10 tick da 100 ns), senza arrotondare
   al secondo/giorno successivo.
3. È chiamato **solo** da `Perizia.Create`
   e `Sinistro.Create`, prima di INSERT/audit.
4. `Perizia.Restore` e `Sinistro.Restore` non
   normalizzano timestamp precedenti. Nessun UPDATE
   retroattivo, migrazione schema o modifiche ad audit.
5. `OccurredOn` e `ReportedOn` restano `DateOnly`
   invariati. La precisione del timestamp legacy
   d'incidente è un problema separato (#25).

## Test

- Domini: tick 0, 1, 5, 9, 10, 11, 19, idempotenza,
  data di fine anno/secondo, UTC, Local/Unspecified
  rifiutati e `Restore` storico invariato.
- PostgreSQL 17: INSERT con audit riuscito e rilettura
  della Perizia e del Sinistro dalla stessa tenant;
  `DateTime.Ticks` uguali in memoria e dopo DB,
  versione e date uguali, tenant estraneo invisibile,
  esattamente un audit di successo.
- Playwright Keycloak desktop/mobile: output
  `structuredContent` dei due tool di creazione
  confrontato byte-per-byte su ogni campo con REST,
  inclusi `createdAtUtc` e gli eventuali nullable;
  nessuna tolleranza temporale né change di scope JWT.

## Impatto e limiti

Nessuna modifica alla precisione degli XML importati,
alla persistenza di documenti, all'audit trail o alle
policy di cancellazione. La policy rappresenta
volontariamente la precisione **persistibile** di
PostgreSQL fin dal momento della creazione; è limitata
ai due aggregate revisionati. Qualsiasi estensione ad
altri timestamp richiede ADR e test specifici.
