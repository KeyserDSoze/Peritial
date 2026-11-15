# ADR 0019 — Manifest di verifica offline DB ↔ SeaweedFS S3

Data: 2026-11-15. Stato: prima tranche di verifica read-only, disaster recovery completo aperto (#6/#18/#21).

## Contesto
I precedenti drill coprono `pg_dump`/`pg_restore` con database e journal, ma i byte dei documenti sono in S3. Un database ripristinato correttamente può referenziare un blob assente, più corto o danneggiato a parità di lunghezza. La semantica delle intent di upload/recovery impone che un checkpoint non venga raccolto con operazioni pendenti.

## Decisione
Introdurre `ObjectRecoveryCheckpoint` e CLI manuale `Peritial.RecoveryCheckpoint` con comandi `capture`/`verify`. Requisiti: ruolo audit PostgreSQL non filtrato da FORCE RLS, lock condivisi NOWAIT durante la lettura dei metadati, nessun upload intent/chiave idempotente pending, ambiente offline attestato dall'operatore, S3 GET in streaming, digest SHA-256 dei byte reali e confronto di dimensione con il DB. Manifest JSON di versione 1, file temporaneo creato con permessi 0600 su Unix (senza finestra di chmod) e salvataggio senza overwrite. Verifica a posteriori di numero esatto dei metadati, chiavi, tenant/documenti e digest dei byte. Niente stampa di chiavi, tenant, nomi file o segreti.

## Trade-off e conseguenze
La scelta privilegia una prova indipendente d'integrità per referenze DB (inclusa corruzione a lunghezza invariata), testabile su SeaweedFS reale. Non salva oggetti, non enumera orfani privi di metadata, non crea uno snapshot atomico DB+bucket, non autentica il manifest e non copre Azure Blob. Il ruolo privilegiato e il contenuto del manifest sono risorse molto sensibili: usare secret manager, ACL minime, manutenzione e cancellazione governata. Prima del deploy produttivo servono copia/verifica integrale degli oggetti, storage immutabile/offsite, firma e chiavi, restore coordinato e trial realistici on-prem/Azure. La #6 rimane aperta.
