# ADR 0113 — Budget cumulativo dei byte per backup e ripristino S3 offline

- Data: 23 novembre 2026
- Stato: implementato su `main`, test backend verde; full-stack Compose da attestare
- Issue: #6 (backup/DR), #27 (integrità documenti), #21 (coerenza degli intent)
- Branch: esclusivamente `main`; `death` resta storico

## Contesto

ADR 0112 ha reso *bounded* la lettura di ogni documento S3
rispetto a `documents.length_bytes`: un blob eccessivamente
lungo non può più saturare da solo la memoria o lo spazio
`tmpfs`. Tuttavia migliaia di file validi, ognuno entro
i propri limiti, possono **cumulativamente** saturare lo
staging plaintext durante `export`, o esaurire la capacità
del bucket di restore.

I digest SHA-256 di V013/V014 certificano l'identità dei byte
(per i nuovi documenti), non la loro somma o l'effettiva capacità
dell'ambiente di recovery.

## Scelta

Introdurre `ObjectRecoveryBudget` con default **512 MiB**
(`536870912` byte) e un override intenzionale e positivo tramite
`PERITIAL_RECOVERY_MAX_TOTAL_BYTES` nel CLI. L'override serve
a dimensionare il piano di recovery; non è una configurazione
per singolo tenant né una autorizzazione ad eseguire backup
con API/workers ancora attivi.

Il budget è valutato sull'intero inventario autorizzato a
`BYPASSRLS`, non sul singolo tenant. La somma è calcolata con
un controllo `length > limit - accumulated` per evitare
overflow su manifest malevoli. Lunghezze zero/negative o quota
zero/negativa falliscono con errore generico.

- **Capture:** la quota è verificata dopo l'inventario PG
  e **prima di qualunque GET S3**.
- **Verify:** vengono verificati sia l'insieme delle lunghezze
  del checkpoint non autenticato sia l'inventario completo
  di PostgreSQL prima di effettuare GET.
- **Export:** riutilizza `Capture` con lo stesso limite,
  **prima di creare lo staging `tmpfs`** e copiare i blob.
- **Restore/import:** verifica la quota subito dopo la
  verifica HMAC e la deserializzazione del manifest,
  **prima di camminare sui file**, dell'accesso ai
  metadati di restore e di qualsiasi PUT S3. Verifica
  nuovamente il budget del DB destinazione.
- Il CLI applica la stessa quota a `capture`, `verify`,
  `export` e `restore-objects`. Un valore non valido
  non equivale mai a "nessun limite".

La quota è per l'insieme dei **byte dei documenti**, non
comprende lo spazio overhead di manifest, directory,
HMAC, dump PostgreSQL, uso memoria dell'SDK, filesystem
e versioning S3. L'operatore deve scegliere un limite
inferiore alla capienza realmente disponibile e conservare
margine. Il default è prudenziale, non una promessa
di disponibilità di `tmpfs` da 512 MiB.

Nessuna migrazione DB né cambiamento ai privilegi
PostgreSQL/SeaweedFS o alla privacy dei DTO.

## Prove

- [Test checkpoint reale su più tenant](https://github.com/KeyserDSoze/Peritial/blob/main/tests/backend/Peritial.Infrastructures.Tests/ObjectRecoveryCheckpointContractTests.cs):
  quota inferiore alla somma dei due documenti, rifiuto
  `budget` prima di qualunque GET (i blob non sono presenti).
- [Test export/import reale su SeaweedFS separati](https://github.com/KeyserDSoze/Peritial/blob/main/tests/backend/Peritial.Infrastructures.Tests/ObjectRecoveryArchiveContractTests.cs):
  export sotto quota senza directory `tmpfs`;
  import con HMAC corretto ma budget troppo basso senza PUT S3;
  percorso normale invariato.
- [Test unitari](https://github.com/KeyserDSoze/Peritial/blob/main/tests/backend/Peritial.Infrastructures.Tests/ObjectRecoveryBudgetTests.cs):
  somma esatta ammessa, quota sotto la somma negata, `long.MaxValue`
  senza overflow, valori non positivi e inventario vuoto.
- [CI backend #45227570649](https://github.com/KeyserDSoze/Peritial/actions/runs/45227570649):
  **525/525 PASS** (23 Domain, 283 Business, 219 Infrastructure).
  La CI *Compose* relativa a questo codice e quella
  dell'HEAD documentale restano da verificare terminali.

## Limiti residui

Non risolve il requisito di snapshot atomico tra PG e S3,
la capacità effettiva disponibile e le quote concorrenti,
l'inventario di oggetti non referenziati, il budget
cumulativo *dei metadati* del manifest, il timeout globale,
le chiavi di cifratura e firma, retention/legal hold,
il backfill autorizzato dei digest NULL legacy e il test
di failover Azure/on-prem. #6 e #27 rimangono aperte.
