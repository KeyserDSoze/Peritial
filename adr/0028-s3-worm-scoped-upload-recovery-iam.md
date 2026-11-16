# ADR 0028 — IAM separato per upload e recupero WORM dei backup cifrati

Data: 2026-11-16. Stato: implementato e verificato su storage S3 compatibile in CI; AWS IAM production hardening ancora aperto.

## Contesto

Le precedenti prove Object Lock (ADR 0025-0027) usavano nel laboratorio un unico account root dello storage compatibile S3 anche per upload/download. L'immutabilità COMPLIANCE delle versioni non è sostitutiva di accessi minimi: una credenziale di recovery non deve poter scrivere, cancellare né cambiare l'amministrazione del bucket. È anche necessario provare il divieto di DELETE su un oggetto **non bloccato**, altrimenti un errore di cancellazione COMPLIANCE può mascherare permessi eccessivi.

## Decisione

1. Introdurre `scripts/recovery/render_worm_iam.py`, generatore Python senza dipendenze esterne di policy IAM AWS-style per **un bucket esplicito** e namespace cifrato fisso `sealed-v1/*`. Rifiutare ARN wildcard, nomi bucket non validi, output non assoluto o preesistente; produrre policy JSON separate in directory 0700 e file 0600. Il generatore non crea utenti e non contiene credenziali.
2. **Uploader (write-only purpose, con letture minime di verifica)**: autorizzare `GetBucketVersioning`, `GetBucketObjectLockConfiguration` e `ListBucket` con condizione `s3:prefix=sealed-v1/*` sul bucket; `PutObject` e `GetObjectRetention` soltanto sotto `sealed-v1/*`, **nessun `GetObject` o `GetObjectVersion`** (GET delegati al reader di verifica). Non concedere `ListBucketVersions` (SILO può comunque consentire l'elenco tramite `ListBucket`), né, cancellazioni, modifica retention o configurazione di bucket.
3. **Recovery reader**: stesse letture di sicurezza; `ListBucket`, `ListBucketVersions` limitate al prefisso; `GetObject`, `GetObjectVersion`, `GetObjectRetention` limitate alle key cifrate. Nessuna PUT/DELETE né amministrazione bucket.
4. Il job `worm-recovery` usa il client amministrativo SILO `mcli` **solo per creare account e associare policy** al server usa-e-getta, con due credenziali dimostrative separate. Nel test xUnit l'admin continua soltanto a creare bucket con Object Lock, configurare retention e il bucket non protetto di controllo; `UploadAsync` usa solo l'uploader, `DownloadAsync` e `DownloadHistoricalAsync` solo il reader. Verificare con API reali il fallimento di `DeleteObject(VersionId)` su una versione **non protetta** per entrambi gli utenti, del PUT del reader, dei GET correnti/storici dell uploader, del cambio configurazione bucket, nonché dell'accesso a key fuori prefisso. Conservare il drill storico WORM con blob cifrato >1 MiB e due delete marker.
5. Verificare le policy prodotte via `unittest` senza servizi esterni: azioni/scope esatti, nessun `s3:*`, `Delete*`, amministrazione o retention write, nessun overwrite, bucket invalidi e file privati.

## Confini e lavoro residuo

Il generatore rende unicamente JSON; non esegue provisioning IAM presso AWS, non configura trust policy, federazione, STS, SCP o IAM Access Analyzer. La CLI remote recovery attuale usa variabili d'ambiente con access key + secret key; IAM Role/STS con credenziali temporanee e rotazione automatica **non sono ancora implementati** e sono raccomandati prima di produzione. Infrastruttura AWS reale, KMS/HSM, separazione cross-account, MFA/CloudTrail, retention/audit offsite, compromissione dell'host, multipart, Keycloak/Azure e atomicità DB↔S3 live restano aperti. Nessuna chiusura delle issue #6/#18/#21.

## Revisione dei permessi GET (16 novembre 2026)

Il test iniziale ha rivelato una differenza di autorizzazione su SILO: omettere `ListBucketVersions` **non impedisce** l'enumerazione con `ListBucket`; omettere `GetObjectVersion` **non impedisce** la lettura `GetObject(VersionId)` se il ruolo ha `GetObject`. La politica è stata quindi corretta eliminando **ogni GET dell'uploader**. Il nuovo `UploadAsync` reale esige un verifier read-only separato e usa quel client per tutti i GET di verifica; le credenziali supplementari sono obbligatorie nel CLI reale, non nella modalità demo SeaweedFS. Il test WORM verifica divieto di lettura di byte correnti e storici per il writer, oltre a permessi di scrittura negati al reader. Non viene rivendicata confidenzialità dei soli VersionId su SILO; la verifica AWS IAM/STS del provider scelto resta aperta.
