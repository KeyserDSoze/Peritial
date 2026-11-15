# ADR 0022 — Sealing cifrato e autenticato di un singolo set PostgreSQL+S3

Data: 2026-11-15 — stato: implementazione per laboratorio offline e CI, non processo DR produttivo.

## Contesto

I dump `pg_dump -Fc` della #6 hanno un checksum SHA-256 ma non erano cifrati o autenticati. L'archive SeaweedFS S3 (ADR 0020) ha un HMAC-SHA256 separato per il manifest (ADR 0021), ma lascia in chiaro i byte e non autentica il dump PostgreSQL nello stesso set. I file possono contenere dati di più tenant, audit e documenti personali.

## Decisione

Introdurre `SealedRecoveryBundle.SealAsync/UnsealAsync` in `Infrastructures` e CLI manuale `Peritial.RecoveryBundle -- seal|unseal` nella soluzione .NET 10:

- **Preflight:** richiedere un PGDMP con sidecar SHA-256 valido e un archive S3 con HMAC valido e blob SHA-256 integri. Richiedere chiave AES-GCM casuale di 32 byte **distinta** da quella HMAC S3, in file esterno privato 0600/0400; nessuna chiave su CLI argv, repository o dentro bundle.
- **Index:** formato versione 1, salt casuale 32 byte, percorsi canonici hardcoded (PG dump/digest; S3 manifest/MAC; file S3 numerati) e lunghezze. Index visibile ma autenticato da HMAC-SHA256 con sottochiave HMAC derivata dalla chiave AES; questo vincola l'elenco file e le dimensioni.
- **Cifratura:** AES-256-GCM per file, chiave derivata via HMAC-SHA256 usando master+salt+file ID, blocchi da 1 MiB con nonce progressivo univoco per la chiave e tag da 16 byte. L'AAD include SHA-256 dell'index, file ID e numero del blocco. Il sistema non carica l'intero dump in RAM.
- **Unseal:** verificare index MAC in constant-time e l'esatta lista di file prima di decifrare; verificare GCM di tutti i blocchi, digest PGDMP e MAC del manifest S3; usare directory temporanea privata 0700 e file 0600. Non pubblicare output parziale, non sovrascrivere destinazioni già esistenti.
- **Test:** test xUnit con input sintetici multi-MiB per corruzione, wrong key, indice modificato, riapertura idempotente e filesystem. CI Compose con veri `pg_dump`/`pg_restore`, archivio S3 e restore da bundle decifrato su nuove istanze PostgreSQL e SeaweedFS; prova rifiuto di permessi su chiavi e chiavi errate.

## Limiti e conseguenze

La cifratura riguarda il **bundle sigillato**, non i file sorgente intermedi o la directory di unseal, che rimangono in chiaro finché l'operatore non li elimina da storage scratch cifrato. L'index lascia osservabili numerosità e dimensioni dei file. L'HMAC simmetrico non fornisce non-ripudio e il sistema è vulnerabile a compromissione/perdita di entrambe le chiavi; sono necessari KMS, rotazione e lifecycle della chiave. Il bundle non crea uno snapshot atomico PostgreSQL+S3, non esporta orfani/versioni bucket, non copre Azure Blob, Keycloak o backup offsite cifrato/immutabile e non automatizza retention o restore produttivo. La #6 resta aperta insieme a #18 e #21.
