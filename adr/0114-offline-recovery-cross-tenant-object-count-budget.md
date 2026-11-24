# ADR 0114 — Limite cumulativo di documenti per checkpoint e archivio S3

- Data: 24 novembre 2026
- Stato: implementato su `main`; conferma Compose full-stack sull'HEAD ancora in attesa
- Issue: #6 (P0 backup/DR), #27 (integrità del documento)
- `death`: archivio storico immutato

## Problema

[ADR 0112](0112-offline-s3-checkpoint-archive-bounded-byte-streams.md)
limita i byte letti per documento. [ADR 0113](0113-offline-object-recovery-cumulative-byte-budget.md)
limita a 512 MiB per default la somma dei byte di tutti i documenti.
Un inventario PostgreSQL con un numero enorme di oggetti piccoli,
tutti sotto la quota di byte, potrebbe tuttavia consumare
memoria per il `List<Metadata>`, il manifest JSON e le
entry `tmpfs`, oltre a produrre milioni di GET/PUT S3.

## Decisione

Aggiungere un secondo budget **indipendente dal numero di byte**,
con `ObjectRecoveryBudget.DefaultMaxObjects = 10_000` e
`MaxSupportedObjects = 100_000`. L'operatore può ridurre o
aumentare il default tramite `PERITIAL_RECOVERY_MAX_OBJECTS`,
purché sia un intero decimale positivo entro il limite
massimo. Una configurazione invalida fallisce prima dei contatti
con database e storage. Non esiste modalità "unlimited".

- `ObjectRecoveryBudget.RequireWithin` conta ogni documento
  anche se lungo un solo byte, verifica prima la quota count
  e poi la somma overflow-safe già introdotta in ADR 0113.
- `ObjectRecoveryCheckpoint.ReadAllMetadataAsync` controlla
  il cap prima di aprire PostgreSQL e limita la query ordinata
  a **`maxObjects + 1` righe**. L'elemento in più prova la
  presenza di un inventario eccessivo; il processo interrompe
  l'enumerazione invece di materializzare tutto in RAM.
  FORCE RLS, ruolo BYPASSRLS read-only, `SHARE NOWAIT`
  e guardia pending uploads rimangono obbligatori.
- `CaptureAsync` blocca l'inventario eccedente **prima di
  qualunque GET** S3. `VerifyMetadataAsync` respinge anche
  manifest sovraffollati prima di leggere il DB, quindi confronta
  di nuovo l'inventario effettivo. `VerifyAsync` eredita
  lo stesso limite.
- `ObjectRecoveryArchive.ExportAsync` propaga il numero
  massimo a `CaptureAsync`, prima di costruire lo staging
  `tmpfs`. `ImportAsync` applica il limite alla lista
  del manifest **dopo HMAC e versione** ma prima di enumerare
  i file locali o fare qualunque PUT. La successiva verifica
  del database di destinazione è anch'essa bounded.
- Il CLI offline applica lo stesso limite ai quattro comandi
  `capture`, `verify`, `export`, `restore-objects`.
  Nessuna modifica ai DTO, alle migrazioni V001–V014, agli ACL,
  all'identità tenant o a `death`.

## Prove e stato

La suite xUnit verifica: somma sotto cap ma 3 oggetti con quota
2 vengono negati; esatta soglia 3 consentita; valori 0,
negativi e oltre 100000 rifiutati; inventario vuoto ammesso
con limite valido, rifiutato con limite zero.

I contratti con PostgreSQL e SeaweedFS reali dimostrano
il blocco di due documenti multi-tenant con `maxObjects=1`
prima del primo GET S3; il contratto export blocca prima della
directory `tmpfs`, quello import prima di un PUT verso il
server S3 indipendente anche con HMAC corretto.

Test RED inseriti prima dell'implementazione:
[4f43e1d](https://github.com/KeyserDSoze/Peritial/commit/4f43e1d93c57bc7f3edbf91a2f263f1c7b7ae8ef),
[bc94666](https://github.com/KeyserDSoze/Peritial/commit/bc946660ef026af2c86591ac6b3806339fc46357),
[81f66ac](https://github.com/KeyserDSoze/Peritial/commit/81f66ac74b9f955fc0e8e8f4775c27140dd7f1ae).
Le prime CI sono rosse a causa delle nuove signature
`maxObjects` mancanti nel codice precedente (RED di
compilazione). L'implementazione è nei commit
[068e37f](https://github.com/KeyserDSoze/Peritial/commit/068e37fe241ede5d25f6bf7133c694ee31572a4f),
[f3b489b](https://github.com/KeyserDSoze/Peritial/commit/f3b489b08b91dfb0f13d32cb1d92257352b83e05),
[869287d](https://github.com/KeyserDSoze/Peritial/commit/869287dffb98458803a6a382797e26bf8a0d5674)
e [74f0b7b](https://github.com/KeyserDSoze/Peritial/commit/74f0b7b8ccddb7a37618218056aa9df1ccbaeda7).

La [CI #45408288832](https://github.com/KeyserDSoze/Peritial/actions/runs/45408288832)
ha backend **531/531 PASS** (23 Domain, 283 Business, 225 Infrastructure),
con database-recovery passato; Compose era in esecuzione
alla verifica. L'ultimo HEAD di documentazione richiede
attestazione autonoma di tutti i 9 job; non inferirla dalla
suite backend.

## Limiti

Il cap sulla popolazione non sostituisce la quota bytes,
il limite 50 MB del manifest autenticato, il controllo
`tmpfs`/swap, la verifica SHA-256 degli oggetti o l'operatore
che arresta davvero tutti i writer. Un numero massimo di 100000
non è una garanzia di capacità dell'host o di costi S3
accettabili: richiede piano di capacity, tempo di esecuzione,
retention/offsite e test su Azure/on-prem. Snapshot atomico
PostgreSQL/S3 e legacy digest NULL (#27) restano aperti.
