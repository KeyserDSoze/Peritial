# ADR 0015 — Migrazioni schema esplicite e fallimento controllato

Data: 2026-11-15 — Stato: adottata parzialmente, issue #6.

## Contesto
`001_init.sql` fornisce una definizione ripetibile per test/Compose demo, ma non registra uno storico. In produzione i processi REST/MCP non devono possedere privilegi DDL né applicare schema on startup. Le installazioni legacy possono avere revisioni divergenti: un auto-baseline non verificato mentirebbe sullo stato e rischierebbe perdita dati o policy RLS errate.

## Decisione
Creare `SchemaMigrator` e il progetto CLI standalone. L'identità DDL è separata dai ruoli app/recovery. Gli script incorporati sono ordinati e immutabili; il migrator calcola SHA-256 sui byte UTF-8, legge e convalida tutte le revisioni, acquisisce advisory transaction lock e committa DDL e journal insieme. Un database con oggetti Peritial ma senza journal viene rifiutato; non si applica una "baseline" automatica. Versione 1 rappresenta il bootstrap storico, versione 2 aggiunge un indice su audit tenant/action/time. `002_dev_app_role.sql` rimane riservato alla demo.

## Alternative scartate
- Eseguire `001_init.sql` ad ogni avvio in produzione: richiede DDL nell'API e non prova lo stato dell'installazione.
- Marcatura automatica V001 di ogni vecchio database: non può garantire colonne, policy e permessi corretti.
- Applicare script senza transaction lock: rischia corse tra processi di deploy e versioni duplicate.
- "Down" automatico su tabelle con dati: pericoloso senza un backup ripristinabile e pianificazione dell'object storage.

## Conseguenze e limiti
Il percorso iniziale è riproducibile per DB vuoti; la CI esercita drift e concorrenza con PostgreSQL reale. Esistono ancora ostacoli P0: acquisizione/restore di backup effettivi, onboarding legacy controllato, compatibilità delle versioni app/DB, proof-of-upgrade Azure/on-prem, rollback coordinato e release signing. Non dichiarare produzione pronta fino alla chiusura di #6, #17 e #18.
