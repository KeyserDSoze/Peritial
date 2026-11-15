# ADR 0018 — Riparazione esplicita della variante PostgreSQL legacy pre-lease

Data: 2026-11-15. Stato: adottata la **sola variante pre-lease esatta**; #6 e #18 restano aperte.

## Contesto
Il comando `adopt` rifiuta ogni schema non equivalente a V001. Una variante storica dei database demo può essere priva della colonna `upload_intents.lease_expires_at_utc`, del relativo indice e del grant update. Applicare in produzione l'intero `001_init.sql` per correggerla sarebbe rischioso; auto-baseline su schemi ignoti potrebbe compromettere le policy multi-tenant.

## Decisione
- Comando distinto `adopt-prelease` con gli stessi prerequisiti del comando `adopt`: privilegi DDL separati, archivio `pg_dump -Fc` verificato con SHA-256 e `pg_restore --list`, consenso `OFFLINE_BACKUP_VERIFIED`, interruzione dei writer.
- Con advisory lock e `ACCESS EXCLUSIVE NOWAIT`, verificare che il catalogo reale equivalga a una reference V001 in uno schema temporaneo **da cui sia stata rimossa soltanto** la colonna lease (e i suoi oggetti dipendenti). Rifiutare ogni altro drift RLS/ACL/colonne/indici.
- Ripristinare colonna con default lease due minuti, indice e grant colonna, ricontrollare catalogo riparato contro la reference e committare journal V001 + V002 nella stessa transazione. Nessuna scrittura sulle righe tenant salvo la valorizzazione del nuovo campo obbligatorio.
- xUnit PostgreSQL copre dati, drift, RLS e forma corretta. Il job `database-recovery` esegue anche `run_prelease_adoption.sh` con server PostgreSQL 17, backup reale e intento pendente.

## Limiti
SHA-256 non è una firma/autenticazione; l'archivio deve essere associato operativamente all'istanza e al punto di ripristino. Il lease inizializzato non dimostra l'esistenza dei blob né autorizza il riavvio immediato del worker. Non copre altri schemi divergenti, restore DB+S3/Azure coerente, rollback multi-versione o deployment Azure/on-prem produttivi.
