# ADR 0027 — Recupero esplicito e non distruttivo del backup cifrato da VersionId protetti

Data: 2026-11-16. Stato: implementazione e test su server WORM compatibile S3 in laboratorio; non approvazione di DR produttivo.

## Problema

L'ADR 0026 dimostra che la versione Object Lock COMPLIANCE del marker `complete` sopravvive a un delete marker, ma il download ordinario `pull-sealed` usa soltanto oggetti correnti e fallisce. Rimuovere i marker, scegliere l'ultima versione antecedente automaticamente o ripristinare oggetti disomogenei da punti di backup diversi è pericoloso: richiede un insieme coerente autenticato.

## Decisione

- Aggiungere il comando **separato** `pull-historic` con consenso `PERITIAL_BUNDLE_CONFIRM=ISOLATED_HISTORICAL_DOWNLOAD`, backup ID originale, chiave AES originale e file locale `PERITIAL_OFFSITE_VERSION_SELECTION_FILE`. L'operatore fornisce `VersionId` per **tutte** le key relative canoniche in un JSON v1 (es. `complete`, `transfer.json`, `transfer.hmac`, `index.json`, `index.hmac` e tutti i `payload/*.enc`). Il parser rifiuta directory relativa, file symlink, JSON non conforme, nomi duplicati, key non canoniche, VersionId assente/nullo e quantità inattese.
- `DownloadHistoricalAsync` legge `complete`, inventario e signature **solo** con `GetObject(VersionId=...)`, autentica l'inventario con HMAC **vincolato al backup ID** e verifica che la selezione contenga esattamente tutte e sole le key dell'inventario autenticato. Le versioni non sono scelte automaticamente e nessun GET può ricadere su `VersionId` corrente. Verificare l'attuale policy bucket Object Lock e `GetObjectRetention` **per ciascun VersionId scelto** (COMPLIANCE non scaduta), poi i digest di ogni ciphertext e il MAC e i tag GCM del bundle prima della pubblicazione via rename in una nuova directory privata.
- Non compiere **alcuna** operazione remota di scrittura, cancellazione o eliminazione di delete marker. Non permettere `disposableUnprotectedDemo` nella modalità storica. La successiva `unseal` usa sempre `tmpfs` privata e swap spento, prima del restore PostgreSQL/S3 su nuovi server.
- Estendere il job `worm-recovery` con provider PGSTY SILO effettivamente versionato/COMPLIANCE: dump PGDMP sintetico e documento cifrato multi-blocco, delete marker sia su `complete` che su ciphertext, rifiuto del download corrente, recupero storico esplicito integro, rifiuto di selezione mancante/falsa/chiave errata e destinazione esistente. Preservare il drill separato con PostgreSQL 17 e 4 blob reali in Compose.

## Confini e rischi residui

Il file di selezione delle versioni **non è un'evidenza firmata**: l'autenticità viene provata da HMAC, SHA-256 e tag AES-GCM del backup originale, e dalla retention verificata di ciascun oggetto. Un operatore può scegliere volontariamente un diverso backup passato valido: la policy di recovery e il controllo del punto temporale non sono automatizzati. Il recupero è limitato a versioni WORM COMPLIANCE ancora protette, non c'è bypass per versioni scadute e nessun fallback silenzioso. Non sono coperti AWS S3 gestito, KMS e rotazione delle chiavi, IAM di sola lettura con GetObjectVersion/GetObjectRetention, offsite geografico, automatismi, recovery da guasto di bucket/regione o consistenza PostgreSQL↔S3 sotto traffico. #6/#18/#21 restano aperte.
