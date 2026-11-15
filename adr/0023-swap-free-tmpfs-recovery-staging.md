# ADR 0023 — Scratch Linux tmpfs swap-free per backup multi-servizio in chiaro

Data: 2026-11-15. Stato: implementato e verificato nel workflow di laboratorio; #6/#18/#21 restano aperte.

## Problema

ADR 0022 cifra e autentica il bundle finale PostgreSQL+S3, ma il dump PGDMP sorgente, l'export locale dei documenti S3 e l'output di unseal restavano in chiaro su path di filesystem scelti dall'operatore. Un path `/tmp` o un bind mount `overlay/ext4` può persistire i byte del dump e dei documenti anche quando il bundle finale è cifrato. `chmod 0600` non impedisce persistenza, snapshot del disco e forensics. Anche un `tmpfs` con swap attivo può spostare pagine in chiaro su un disco.

## Decisione

- Implementare `SecureRecoveryStaging.RequirePrivateTmpfsDirectory/File/Parent` con verifica **fail-closed solo Linux** prima di letture o scritture plaintext critiche. Leggere `/proc/self/mountinfo` e selezionare il mount **più specifico** del path, imponendo filesystem effettivo `tmpfs`; verificare `/proc/swaps` e rifiutare qualsiasi swap attivo; esigere una directory esistente con ACL owner-only `0700` o più restrittive e nessun symlink in nessun antenato. Non inferire sicurezza dal nome del path.
- Applicare il guard a `ObjectRecoveryArchive.ExportAsync` (prima di leggere l'S3 remoto e creare file plaintext), `ReadAndValidateAsync` (copre anche import), `SealedRecoveryBundle.SealAsync` (dump PostgreSQL, digest e archive S3 sorgenti), `SealedRecoveryBundle.UnsealAsync` (prima di creare la stage decifrata). Il bundle **già cifrato** può essere salvato su un filesystem persistente.
- Mantenere `scripts/db/backup.sh` riutilizzabile in autonomia per gli export PostgreSQL. L'operatore che intende sigillare deve scegliere e verificare lo scratch tmpfs **prima di eseguire** il backup/PUT S3; il guard durante seal non può cancellare un dump precedentemente scritto su disco.
- CI: test xUnit negativi su `/tmp` persistente, dir `0755`, symlink e API; test positivi su `/dev/shm` privato. I runner GitHub Actions effimeri disabilitano lo swap e verificano `tmpfs`. Il drill end-to-end usa `mktemp -d /dev/shm/...` host bind-mount in container e non allarga ACL `0700` per copiare il dump restaurato.
- **Non** supportare automaticamente Windows/macOS o fallback su overlay/ext4 fino a introduzione di un backend scratch cifrato validato (es. volume LUKS controllato). Non ammettere un flag generico per bypassare il controllo.

## Limiti e rischio residuo

Il sistema non protegge da `root`, snapshot di RAM, processi con accesso a memoria, coredump, host compromesso, riattivazione dello swap durante il lavoro o possibili backup di RAM del sistema. `tmpfs` ha limiti di capacità: in caso di esaurimento memoria o spazio **fallire**, non spostare i byte su disco. Il solo `tmpfs` non fornisce KMS, segregazione/rotazione chiavi, backup immutabile offsite, retention, outbox/atomicità DB↔S3 sotto traffico, Azure Blob, Keycloak, RPO/RTO o resilienza produttiva. Il checksum PostgreSQL e i file S3 sorgenti restano temporaneamente leggibili dall'utente manutentore, per cui la finestra deve essere isolata e il cleanup governato.
