# ADR 0024 — Replica S3 con marker di completamento di bundle AES-GCM

Data: 2026-11-16. Stato: tranche di laboratorio; DR e backup offsite produttivo non chiusi (#6/#18/#21).

## Contesto

ADR 0022 ha introdotto bundle cifrati del set PostgreSQL+SeaweedFS; ADR 0023 ha confinato i file plaintext allo scratch \`tmpfs\`. Mancava una prova del trasferimento **del solo ciphertext** su un server di backup distinto e della capacità di ripristinare da quella copia recuperata, anziché dalla directory locale della sorgente.

## Decisione

- Aggiungere validatore read-only dei file cifrati \`SealedRecoveryBundle.ValidateEncryptedAsync\` che verifica index HMAC, percorsi/lunghezze canoniche e **ogni tag AES-256-GCM** in RAM, azzerando il buffer temporaneo. Non genera plaintext persistente.
- \`SealedBundleOffsite.UploadAsync\` usa una chiave AES esterna già presente, ID backup UUID esadecimale univoco e prefisso \`sealed-v1/<id>/\`. Si rifiuta se esiste qualsiasi oggetto sotto il prefisso. Per ciascun file cifra già sigillato esegue PUT \`If-None-Match: *\` e GET di verifica SHA-256/length. Genera \`transfer.json\` con nomi tecnici canonici, hash e lunghezze, più \`transfer.hmac\` con sottochiave domain-separated; verifica entrambi a distanza. Scrive \`complete\` **per ultimo**. Un upload interrotto non è un backup completo.
- \`DownloadAsync\` deve trovare \`complete\`, autenticare index transfer HMAC con confronto costante, verificare elenco **esatto** di chiavi remote, GET SHA-256 di ogni file in stage privata 0700 e poi validare di nuovo index HMAC/tag AES-GCM. Pubblica la directory cifrata tramite rename solo a check conclusi. Da lì \`unseal\` su \`tmpfs\`, \`pg_restore\` e S3 import standard; mai overwrite.
- CLI \`Peritial.RecoveryBundle push-sealed|pull-sealed\`: HTTPS obbligatorio per qualsiasi server reale, credenziali S3 da secret manager, chiavi esterne 32 byte owner-only, conferme \`REMOTE_ENCRYPTED_UPLOAD\` e \`ISOLATED_ENCRYPTED_DOWNLOAD\`. Unica eccezione HTTP ammessa: host CI esplicito \`seaweedfs-backup\` e attestazione \`DISPOSABLE_TEST_ONLY\`.
- Test reali Compose su **tre server SeaweedFS separati**, PostgreSQL source e restore indipendenti; alterazione ciphertext remoto, inventario incompleto, chiave errata, doppio upload, download idempotente e restore solo dai byte cifrati recuperati dalla terza istanza.

## Limiti

Questo protocollo protegge l'integrità e rileva un tentativo incompleto, **non garantisce immutabilità o disponibilità**: l'operatore/gestore S3 con privilegio amministrativo può eliminare o riscrivere gli oggetti. Non configura Object Lock/compliance retention, bucket policies, versioning, cross-region replication, sistemi offsite geografici, KMS/HSM, rotazione delle chiavi, scheduler, multipart upload grandi volumi o recovery Keycloak/Azure. Se la connessione cade prima di \`complete\`, restano oggetti parziali: ripartire con nuovo ID e riconciliare manualmente quelli incompleti. L'ID è un identificatore, non una prova della data dello snapshot o della sua coerenza DB↔S3; serve ancora un protocollo offline senza writer. Il percorso HTTPS reale non è collaudato in CI (demo HTTP soltanto nel container isolato). #6, #18 e #21 restano aperte.
