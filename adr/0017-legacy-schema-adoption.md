# ADR 0017 — Adozione esplicita e verificata di schemi PostgreSQL V001 legacy

Data: 2026-11-15 — Stato: implementata la **sola** baseline di schema V001 esatta su PostgreSQL 17; produzione #6/#18 non chiuse.

## Contesto
Le installazioni demo/on-prem inizializzate con `scripts/db/001_init.sql` non hanno un journal. Eseguire `SchemaMigrator.ApplyAsync` su quelle tabelle è vietato per evitare falsi storici o regressioni RLS. Ricreare lo schema o cancellare i volumi distruggerebbe dati tenant, audit e intenti. Esistono ora script PostgreSQL-only di backup+restore (#6), ma i file documentali rimangono fuori dal DB.

## Decisione
- Separare due percorsi CLI: `apply` per DB nuovi/versionati; `adopt` **solo** per database legacy senza journal, con conferma `OFFLINE_BACKUP_VERIFIED`, archivio `pg_dump -Fc` assoluto e checksum SHA-256, validazione del formato `pg_restore --list` e credenziale DDL indipendente dalla API.
- La verifica di catalogo è **positiva e strutturale**: una V001 nuova viene costruita in uno schema temporaneo all'interno di un savepoint; tabelle, colonne, vincoli, indici, opzioni RLS FORCE, grants, policy, funzioni e trigger sono confrontati con la struttura legacy, normalizzando solo i qualificatori del nome schema. Il savepoint viene annullato, non resta DDL di riferimento.
- Il migrator serializza con advisory lock e `LOCK TABLE ... ACCESS EXCLUSIVE NOWAIT`, rifiuta ruoli app/recovery privilegiati e qualunque drift; non riesegue V001 sulle righe legacy. Soltanto dopo il match scrive journal V001 con checksum e applica V002 nella **medesima transazione**.
- CI: xUnit PostgreSQL confronta V001 esatta, dati preservati e drift di colonne, permessi, indici, policy; un'ulteriore prova con PostgreSQL 17 reale esercita il CLI completo con archivio effettivo, mancato consenso, checksum manomesso, idempotenza e journal.

## Limiti e rischi residui
Il file SHA-256 non è firmato e non prova che il dump rappresenti proprio l'istanza/istante da adottare: l'operatore deve verificarlo e interrompere tutti i writer e il worker. Non è un meccanismo di autorizzazione autonoma o snapshot cross-sistema. Il codice rifiuta in modo intenzionale schemi pre-V001, V001 parziali o modificati. Non copre restore coordinato dei byte S3/Azure Blob, rollback di release multi-versione, procedure migrate-then-switch con downtime reale, ruoli/segreti cloud, rollout Azure/on-prem. #6, #18, #21 restano aperte.
