# ADR 0048 — V009: metadati documentali senza UPDATE/DELETE applicativo

- Data: 19 novembre 2026
- Stato: implementata su `main`; risultati CI da verificare
- Issue: #5, #6, #11, #21
- Branch: solo `main`; `death` invariato

## Contesto

V001 concedeva `SELECT, INSERT, UPDATE, DELETE` al ruolo `peritial_app`
sulla tabella `documents`. I servizi esposti REST/MCP permettono
l'ingestione con `INSERT`, i cataloghi permettono la lettura
con `SELECT`. Non esiste un use case autorizzato per modificare
`file_name`/`storage_key`, cancellare i metadati o sincronizzare
questo tipo di intervento con l'object store. Una query SQL diretta
con credenziali applicative avrebbe potuto alterare la referenza
del file o eliminarla senza il protocollo di ingestione e audit.

## Decisione

La migrazione forward-only
`V009__documents_revoke_app_mutations.sql` esegue soltanto
`REVOKE UPDATE, DELETE ON documents FROM peritial_app` e
`FROM PUBLIC`: non cancella e non aggiorna alcun documento,
non altera file in S3/Azure Blob, migrazioni V001–V008 o i loro
checksum. `peritial_app` mantiene `SELECT`/`INSERT` con
FORCE RLS; `peritial_recovery` conserva il suo `SELECT`
limitato a tenant e chiave, senza DML sui documenti.
I privilegi DDL dell'owner/migrator restano separati: questa misura
non è WORM e non blocca un amministratore con privilegi superiori.

L'eventuale implementazione di versione metadata, rettifica,
anonimizzazione o cancellazione sarà un caso d'uso distinto:
autorizzazione e audit atomici, approvazione e retention/legal hold,
coerenza del blob con recovery verificata e una nuova migrazione
least-privilege. Non reintrodurre un grant globale senza questi gate.

## Prove e rollout

Il test PostgreSQL `V009_document_metadata_cannot_be_rewritten_or_deleted_by_app_sql`
crea una Perizia e un documento con il ruolo app, verifica
`SELECT`/`INSERT` consentiti, rifiuto SQLSTATE `42501`
per modifica di chiave/nome e cancellazione, integrità del record
e visibilità del catalogo. Il migrator registra V009 e checksum;
upgrade V005, adozioni legacy, restore e Compose CI devono
includere la nona versione. I volumi reali richiedono backup
verificato, stop writer e applicazione esplicita del migrator
prima di riaprire il traffico. La demo Compose V001
resta intenzionalmente non journaled, mentre CI applica
V003→V009 soltanto su database effimeri.

La misura non implementa download controllato, quarantena,
retention, purge approval, policy WORM o restore provato
su ambienti reali; #5/#6/#11/#21 restano aperte.
