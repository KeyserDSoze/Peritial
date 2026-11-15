# ADR 0016 — Backup PostgreSQL verificato su seconda istanza

Data: 2026-11-15. Stato: tranche database accettata, disaster recovery completo aperto (#6/#18/#21).

## Contesto
`SchemaMigrator` introduce uno storico DDL ma non permette il ripristino dopo un errore *post-commit* o il recupero di dati reali. Peritial mantiene dati di perizie/audit/intent/idempotenza in PostgreSQL e i byte dei documenti separatamente in S3/Blob. Serve un primo contratto di ripristino che non confonda snapshot DB e restore end-to-end.

## Decisione
- Due script espliciti `backup.sh`/`restore.sh` per archivi `pg_dump -Fc` e `pg_restore` (PostgreSQL 17), connessioni PG via ambiente e privilegi amministrativi esterni all'API.
- Non sovrascrivere file: destinazione unica, permessi 0600 tramite `umask 077`, file parziali in stesso filesystem, checksum SHA-256 e verifica del formato. Non loggare dati/credenziali.
- Restore consentito solo dopo consenso `EMPTY_DATABASE`, su DB nuovo verificato vuoto con ruoli minimi disponibili e checksum intatto. Singola transazione `pg_restore`, preservando grants e FORCE RLS; nessun `--clean` né auto-migrazione.
- CI con due server PG17 differenti, fixture cross-tenant e prova di recovery di journal/audit/intents, di divieti su dati cross-tenant e di rifiuto di restore distruttivi.

## Rischi espliciti
I checksum non sono firme, `pg_dump` non contiene ruoli globali né Keycloak, e PostgreSQL non offre snapshot transazionale coordinato con S3. Prima di produzione serve sospensione mutazioni, backup oggetti e restoration drill integrale, offsite cifrato, schedulazione, retention, compatibilità versioni, runbook e alert. Le issue #6/#18/#21 rimangono aperte.
