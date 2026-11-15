# ADR 0020 — Ripristino offline dei byte S3 su istanza indipendente

Data: 2026-11-15. Stato: drill PostgreSQL+SeaweedFS implementato in Compose; disaster recovery produttivo non chiuso (#6/#18/#21).

## Contesto
ADR 0019 verifica solo l'integrità di oggetti S3 già presenti tramite manifest SHA-256, senza copiarli. L'obiettivo di questa tranche è provare con due server PostgreSQL e due server SeaweedFS **fisicamente distinti nel laboratorio** che i contenuti associati ai metadata possono essere salvati, trasferiti e reimportati.

## Decisione
- `ObjectRecoveryArchive.ExportAsync` crea un nuovo archive locale con directory 0700, file 0600, nomi ordinali opachi (`objects/00000000.blob`) separati dalle S3 key e manifest `ArchiveVersion=1` che include checkpoint, SHA-256, lunghezze e Content-Type. Ogni byte viene riscaricato in streaming e ricontrollato prima di pubblicare il bundle; non si sovrascrivono archive già presenti.
- `ImportAsync` richiede un database di destinazione già ripristinato, inventory completa dei metadata e zero intenti/idempotency pending, fa hash di **tutti i file prima di scrivere**, rifiuta bucket non vuoto e invia PUT S3 condizionali `If-None-Match: *`. Rilegge con GET i byte restaurati e ricontrolla dimensione e hash per tutti i documenti. La CLI richiede consenso `WRITERS_STOPPED` e `EMPTY_DESTINATION_BUCKET` e non logga tenant/chiavi/credenziali.
- Test TDD e CI Compose con PostgreSQL 17 e SeaweedFS di destinazione separati; test negativo di corruption, manifest modificato, target non vuoto, doppio import e ripristino hash. Un secondo drill interrompe l'API, usa `pg_dump` e `pg_restore` per trasferire i metadata e porta gli oggetti S3 nel nuovo server.

## Limiti e sicurezza
Nessuna transazione DB+S3 esiste: il controllo `WRITERS_STOPPED` è un atto operativo, non una prova programmatica. Errori di import possono lasciare un bucket **parzialmente popolato**, da scartare prima di ricominciare. Il tool non copia orfani S3, versioni bucket, upload multipart pendenti, Azure Blob, configurazione Keycloak o segreti. Il bundle locale non è cifrato, immutabile/offsite né dotato di retention. L'autenticazione HMAC simmetrica del manifest è stata introdotta **successivamente**, in ADR 0021, senza firmare i dump PostgreSQL. Non dichiarare soddisfatti #6/#18/#21 fino a drill Azure/on-prem, ripristini coordinati e security/release governance.
