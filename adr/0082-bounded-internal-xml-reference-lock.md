# ADR 0082 — Timeout fail-closed del mutex per l'import XML interno

- Data: 22 novembre 2026
- Stato: implementato su `main`, CI da verificare sul commit
- Issue: #24, #10, #21, #6
- Branch storico `death`: non modificato

## Problema

La migrazione V012 ha introdotto un mutex SQL
transazionale comune a **tutti** i writer Sinistri
(REST, MCP, import e SQL del ruolo applicativo).
L'import XML case-only, che resta interno e
non ha endpoint, prenota lo stesso mutex prima
di inserire ledger, Sinistro e audit in un solo
commit PostgreSQL.

`pg_advisory_xact_lock` può attendere
indefinitamente se una transazione concorrente
trattiene il riferimento. Questo è un rischio
operativo per bulk import o sessioni sospese.
Non è prova che il riferimento sia duplicato.

## Decisione

Limitare **solo** la fase di acquisizione del mutex
da `PgLegacyXmlCaseImportStore.CommitAsync`:

1. Leggere `pg_settings.setting` per
   `lock_timeout` (unità PostgreSQL: millisecondi).
2. Se il valore originale è `0` (nessun
   limite) o più lungo di 1500 ms, impostare
   `SET LOCAL` tramite
   `set_config('lock_timeout','1500ms',true)`.
   Mai allungare un timeout operatore più severo.
3. Acquisire il mutex **identico** alla V012,
   parametrizzato da tenant firmato e riferimento
   normalizzato.
4. Se `55P03` interviene nella sola
   acquisizione, sollevare
   `LegacyXmlImportBusyException`, distinto
   dal conflitto reale
   `LegacyXmlReferenceConflictException`.
   La transazione PostgreSQL abortita viene
   automaticamente annullata dalla
   `TenantDbScope`.
5. Se il lock viene acquisito, ripristinare
   immediatamente il precedente `lock_timeout`
   locale prima di qualunque ledger/SQL/audit.
   Il commit originale resta invariato.

Non applichiamo un timeout globale ai writer
ordinari, non introduciamo retry silenziosi,
non esponiamo il nuovo errore al pubblico
e non trasformiamo la contesa in un falso
conflitto di identità HTTP 409.

## Evidenze e rischi residui

Test PostgreSQL con ruolo app e **due pool
distinti**: lock held da una transazione;
commit XML fallisce con Busy prima di metadata;
altro tenant con stesso reference avanza;
dopo rilascio lo stesso idempotency key crea
un record e il replay ritorna lo stesso UUID.
Verifica esplicita che Sinistro, ledger e
audit successo non rimangano dopo il timeout.

Il timeout è una misura di disponibilità,
**non** una soluzione alla chiave composita
legacy, alle deadlock multi-reference, ai
retry/ACK incerti, all'analisi di contesa
di larga scala o alla correzione di campi
sconosciuti. #24, #10, #21, #25 restano
aperte. V011/V012 e i checksum delle
migrazioni preesistenti non cambiano;
nessuna esposizione REST/MCP del commit.
