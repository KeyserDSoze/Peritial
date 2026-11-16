# TDD e processo

Ogni use case: test rosso (xUnit / Playwright), implementazione minima, test verde, refactoring, aggiornamento docs/ e adr/.

- Backend completo: docker compose -f docker/compose.yml --profile test run --rm tests
- Frontend: cd src/frontend && npm ci && npx playwright install chromium && npm run build && npm run test:e2e
- CI: xUnit unitari e con PostgreSQL reale, build frontend, Playwright, smoke Docker Compose e job isolato `database-recovery` (backup/restore su due server PG17 più adozione V001 legacy con backup reale, consenso e drift check).
- Lavorare solo su main. Non fondere death.
- Debiti bloccanti produzione: pin digest delle immagini e supply-chain, quarantena persistente e signature freshness dello scanner, limitazione chiamate, audit completo, idempotenza, backup/restore integrati con object storage verificati e upgrade di altre varianti legacy non riconosciute, provisioning e rotazione dei privilegi RLS in produzione, test OAuth/MCP su client reali, deploy automatizzato su Azure, secret vault.

La build iniziale CI ha guidato la correzione delle dipendenze e la scelta dell'immagine S3 open-source disponibile sul registry.

La CI Docker Compose esegue ora un test Playwright autenticato reale (Keycloak, creazione perizia, tool MCP) sia desktop sia mobile, e fallisce su errori OAuth o sulle chiamate non autorizzate. Dopo il ciclo Red→Green si eliminano stampe temporanee di pagine e credenziali dai test (Refactor).

## Verifica OAuth/MCP

`tests/frontend/security.spec.ts` effettua test negativi reali contro il realm Keycloak demo in Docker Compose: discovery RFC 9728, assenza `tenant_id`, audience errata, scope insufficiente, `401` vs `403`, tool MCP non autorizzati. I client Keycloak `peritial-test-*` con password grant servono **esclusivamente** per i test isolati locali, non per i client reali. La PWA continua con Authorization Code + PKCE. Vedere ADR 0007.

## Test audit

SecurityAuditUseCaseTests verifica tenant, subject, azione ed esito senza riferimenti o payload. SecurityAuditRepositoryTests usa PostgreSQL reale e il ruolo INSERT-only per verificare RLS. La CI Docker Compose verifica audit realmente persistito dopo REST e MCP. Le fixture DDL/ruoli PostgreSQL usano un advisory lock per impedire corse del catalogo tra test paralleli. Vedere docs/audit.md.

## Transazioni atomiche audit/metadata (#21)

PeriziaAtomicAuditTests prova su PostgreSQL reale che INSERT/UPDATE perizie e audit si committano insieme, che un audit duplicato annulla CREATE o UPDATE, e che gli eventi cross-tenant non modificano dati. DocumentAtomicAuditTests applica gli stessi controlli ai metadata dei file. Tutte le mutazioni REST/MCP usano i metodi AddAuditedAsync/UpdateAuditedAsync delle porte Business; il ruolo DB resta soggetto a FORCE RLS. La lettura degli oggetti esterni e la loro compensazione in caso di errore restano fuori dalla transazione PostgreSQL: ADR 0008 e issue #5/#21.

## Fault injection documentale (#5/#21)

FileIngestionCompensationTests esercita lo storage fake con upload parziale, errore metadata dopo PUT, cleanup fallito, cancellazione HTTP a metà commit, retry dopo errore e successo senza cancellazione. L'adapter S3 e quello Azure implementano DeleteAsync, con cancellazione idempotente. Le prove reali contro entrambi i provider e il recupero persistente dopo crash non sono ancora completati: ADR 0009.

I test in Peritial.Infrastructures.Tests riutilizzano un database PostgreSQL condiviso: oltre al lock di bootstrap SQL, la suite xUnit è serializzata a livello di assembly. Il lock da solo non impedisce che DDL del test successivo interferisca con la transazione RLS di un test in esecuzione. La serializzazione riguarda solo l'assembly di integrazione, non i test Business o Domain.

## Scanner documentale (#5)

L'API richiede `Malware:Clamd:Host` e porta TCP privata (default 3310), con timeout configurabile 1–120 s. Nessun fallback permissivo: `IFileMalwareScanner` restituisce `Clean` solo per la risposta esatta `stream: OK`. In CI `MalwareAdmissionTests`, `ClamdScannerContractTests` e Playwright EICAR verificano che i file infetti/non verificati non raggiungano la persistenza. Per produzione, gestire update firme e quarantena persistente: ADR 0010.

## Upload recovery con intenzioni durevoli (#21)

`UploadIntentOrderingTests` verifica che l'intento sia persistito prima di PUT e che un errore metadata non avvii una cancellazione rischiosa. `UploadRecoveryTests` usa PostgreSQL reale e ruoli separati: crash simulato, cleanup ritentato dopo errore storage, preservazione blob referenziato quando il commit ACK è incerto, consumo atomico dell'intento con metadata+audit, rispetto del grace period e accessi recovery a colonne ristrette. In CI il job Compose controlla che, dopo Playwright REST/MCP, non rimangano intenti pendenti. Le prove reali di kill/restart multi-replica e l'idempotenza HTTP restano aperte (#21).

## E2E idempotenza upload (#21)

`UploadIdempotencyTests` usa PostgreSQL reale con richieste concorrenti, identità separate, payload mismatch, replay con audit unico, rilascio su errore e worker recovery. `UploadFingerprintTests` verifica il digest di contenuto e metadati. Nel test Compose Playwright usa la medesima chiave in REST upload, retry REST e MCP ingest_file: stesso UUID, nessuna seconda riga audit; un payload differente riceve 409. L'asserzione SQL CI verifica due documenti/azioni di audit per i due browser, non sei eventi da retry.

## Upload lease e prova riavvio reale (#21)

`UploadLeaseTests` prova FORCE RLS e lease valida/scaduta usando PostgreSQL; `UploadLeaseHeartbeatTests` prova il rinnovo periodico durante uno storage PUT bloccato e l'annullamento fail-closed se non è più possibile estendere il lease. Nel job Compose `UploadRestartContractTests` ha due fasi: `docker compose kill --signal SIGKILL api`, seed di un oggetto reale SeaweedFS e di un intento PostgreSQL scaduto mentre API è ferma, poi `docker compose start api`, polling finché l'intento non viene rimosso e verifica finale che il blob non esista e che la key idempotente sia stata liberata. Nessun reset dei volumi per il recovery. Non equiparare questa prova a un HTTP upload spezzato *durante* PUT. Vedi ADR 0013.

## Prova reale mid-PUT con proxy di caos (ADR 0014)

Il job Compose esegue `bash tests/chaos/run_mid_put.sh` dopo le normali prove OAuth/MCP, storage e scanner. Un file PDF di test viene caricato con token Keycloak firmato e idempotency key locale; l'API usa il normale adapter AWS SDK ma punta temporaneamente a un **proxy TCP S3 di test**. Il proxy inoltra l'intestazione e un prefisso del **corpo HTTP PUT** al SeaweedFS reale, segnala l'avvenuto trasferimento e blocca il flusso. La pipeline verifica sia il segnale sia che la chiamata upload non sia terminata, invia `SIGKILL` all'API, fa scadere l'intento del processo morto, riavvia l'API standard e controlla che il worker abbia eliminato intent e chiave idempotente. Un test .NET verifica inoltre con SDK S3 autenticato il 404 del blob, l'assenza del documento e di audit di successo. Il file `docker/compose.chaos.yml` non va usato in produzione; la prova NON rappresenta Azure Blob, multi-replica o timeout DB reali.

## Migrazioni PostgreSQL versionate (#6, ADR 0015)

`SchemaMigrationTests` prova installazione V001/V002, re-run, drift checksum, schema preesistente non tracciato e concorrenza in PostgreSQL reale. Il migrator esplicito `scripts/db/Peritial.DbMigrator` non parte insieme all'API e richiede credenziali DDL indipendenti. Per il runbook e i limiti della demo Compose consultare `docs/database-migrations.md`. Non equiparare i test di schema nuovo a una prova di upgrade/restore del database legacy.

## Adozione V001 legacy verificata (#6, ADR 0017)

`LegacySchemaAdoptionTests` genera schemi isolati tramite `SchemaBootstrap` e prova la baseline di dati reali, il journal V001/V002, il rifiuto di modifiche a colonne, indici, grants, policy FORCE RLS e oggetti aggiuntivi. Il job `database-recovery` esegue inoltre `tests/backup/run_legacy_adoption.sh` e `tests/backup/run_prelease_adoption.sh` con PostgreSQL 17 reale, `pg_dump -Fc`, convalida SHA-256 e `pg_restore --list`, conferma operativa e fail-closed. I comandi `adopt` e `adopt-prelease` restano **offline e manuali**, non passa dall'avvio API; vedere `docs/database-migrations.md`. Non è ancora un restore DB+S3 né un test di upgrade di un ambiente produttivo.

## Checkpoint offline integrità PostgreSQL ↔ SeaweedFS S3 (#6, #21; ADR 0019)

Il progetto `scripts/recovery/Peritial.RecoveryCheckpoint` fornisce due comandi manuali `capture` e `verify`, abilitati solo con `PERITIAL_CHECKPOINT_CONFIRM=WRITERS_STOPPED`. `ObjectRecoveryCheckpointContractTests` viene eseguito dalla CI Compose con PostgreSQL e SeaweedFS reali, in uno schema isolato; verifica su due tenant oggetto mancante, byte diversi con dimensione identica, modifica dei metadati, chiave S3 riutilizzata, intent/idempotenza pending, credenziali app RLS-filtrate e percorso positivo. Il controllo richiede identità DB di sola lettura con capacità BYPASSRLS per non ignorare tenant, e non pubblica chiavi o contenuti nei log. **Nessun oggetto è copiato e nessun ripristino è eseguito**: vedere `docs/object-recovery-checkpoint.md`. Restano aperti disaster recovery coordinato dei blob, Azure Blob, retention/firma e un vero restore end-to-end.

## Recovery reale dei byte S3 in CI — laboratorio (#6, ADR 0020)

`ObjectRecoveryArchive` esporta in una directory privata manifest più file binari ordinali, con SHA-256 byte-per-byte; `ImportAsync` verifica in anticipo gli oggetti, il DB indipendente, l'assenza di upload pendenti e il bucket di destinazione vuoto, poi usa PUT condizionali e GET di verifica. Il CLI `Peritial.RecoveryCheckpoint` aggiunge `export` e `restore-objects` (seconda conferma `EMPTY_DESTINATION_BUCKET`), ora con chiave HMAC-SHA256 esterna obbligatoria di 32 byte da `PERITIAL_ARCHIVE_HMAC_KEY_FILE` (`0600`, fuori archive), test negativi su chiave assente/errata e MAC modificato. I server di destinazione `postgres-restore` e `seaweedfs-restore` sono nel profilo Compose **`recovery-test`**, fuori dalla API. Il job Compose esegue `ObjectRecoveryArchiveContractTests` e `tests/backup/run_complete_pg_s3_restore.sh` (pg_dump + pg_restore, export e import veri). **Non** è un backup automatico o una transazione DB↔S3 sotto traffico. Vedi `docs/object-recovery-checkpoint.md`.

## Bundle cifrato offline dell'intero restore set (#6, ADR 0022)

`SealedRecoveryBundleTests` nella suite backend prova chiavi separate, PGDMP con SHA-256, manifest S3 HMAC, crittografia AES-256-GCM a blocchi (con blob di più MiB), rifiuto di ciphertext/index alterati e wrong key, e pubblicazione **soltanto** dopo verifica completa in directory privata. `scripts/recovery/Peritial.RecoveryBundle` è il nuovo CLI amministrativo `seal|unseal`, compilato via `Peritial.slnx`. Il job Compose `tests/backup/run_complete_pg_s3_restore.sh` impone export S3, sigillo del set PostgreSQL + byte S3, rifiuto di chiavi non configurate o group-readable, rifiuto AES key errata, unseal privato, `pg_restore` sul PostgreSQL indipendente e `restore-objects` su SeaweedFS indipendente con hash di verifica. **Mai eseguire il drill con dati produttivi/volumi reali**: non sono testati snapshot atomici, KMS, eliminazione sicura dei file temporanei, offsite, Keycloak/Azure e disaster recovery completo. Runbook `docs/sealed-multiservice-recovery.md`.

## Gate tmpfs/swap sul plaintext di recovery (#6, ADR 0023)

Le API di export/import dell'archive S3, sealing di PGDMP+S3 e unsealing rifiutano automaticamente filesystem persistenti, leaf directory con permessi troppo ampi, symlink e swap attivo quando trattano dump/documenti in chiaro. Il helper `SecureRecoveryStaging` verifica il filesystem del mount **più specifico** da `/proc/self/mountinfo`, controlla `/proc/swaps` e richiede una directory privata `0700` su Linux. La suite `SecureRecoveryStagingTests` prova l'accettazione tmpfs privato e il fail-closed su disco, directory `0755`, symlink e tutte le tre API. La CI backend e Compose disabilita lo swap soltanto nei runner temporanei, mentre il drill in `tests/backup/run_complete_pg_s3_restore.sh` usa `/dev/shm` host bind-mounted nel container; la copia del dump decifrato conserva i permessi privati tramite un accesso privilegiato limitato al test. Il bundle finale rimane AES-256-GCM su storage persistente. KMS, snapshot sotto traffico, RAM/capacity operativa e DR reale **non** sono coperti.

## Replica remota di bundle cifrato — gate reale di laboratorio (#6, ADR 0024)

`SealedRecoveryBundle.ValidateEncryptedAsync` valida l'HMAC dell'indice e **ogni tag AES-GCM** senza creare file plaintext. `SealedBundleOffsite.UploadAsync/DownloadAsync` trasferisce esclusivamente i ciphertext, manifest e HMAC verso un nuovo prefisso S3; GET di verifica SHA-256 dopo le PUT condizionali, `complete` come ultima scrittura, download atomico in directory privata, rifiuto di prefissi incompleti, chiavi errate, hash corrotti e destinazioni esistenti. Test `SealedBundleOffsiteContractTests` sul terzo server `seaweedfs-backup` della Compose CI; il drill `run_complete_pg_s3_restore.sh` ora sigilla, trasferisce e scarica il bundle **prima** di ripristinare PGDMP e documenti. Non è immutabilità Object Lock, offsite geografico, multipart o gestione KMS; issue #6/#18/#21 aperte.

## Retention S3 fail-closed (issue #6, ADR 0025)

`OffsiteObjectLockPolicy` impone versioning attivo e Object Lock `COMPLIANCE` di almeno 30 giorni tramite API S3. `SealedBundleOffsite.UploadAsync` chiama il preflight prima delle PUT, valida `VersionId` e `GetObjectRetention` dopo ogni PUT, incluso il marker finale; `DownloadAsync` verifica il bucket e la retention di ogni versione corrente prima di accettare un backup remoto. La suite `OffsiteObjectLockPolicyTests` prova stati/configurazioni/retention senza accesso cloud; `SealedBundleOffsiteContractTests` sul server SeaweedFS demo prova che i percorsi predefiniti rifiutano il deposito non compatibile senza creare oggetti o file locali. Solo i test del server interno `seaweedfs-backup` con `PERITIAL_OFFSITE_CI_HTTP=DISPOSABLE_TEST_ONLY` possono usare `disposableUnprotectedDemo` per i drill precedenti. Non c'è ancora un test positivo su AWS S3 Object Lock COMPLIANCE reale, né un controllo di KMS/retention effettiva su un provider remoto di produzione.

## Prova positiva S3 Object Lock WORM (ADR 0026)

Il job CI **`worm-recovery`**, separato dai cinque job esistenti, usa PGSTY SILO (fork S3 compatibile) fissato per digest, su Linux `tmpfs` swap-free. `ObjectLockWormIntegrationTests` crea un bucket con Object Lock **già all'atto della creazione**, versioning `Enabled`, retention `COMPLIANCE` 32 giorni; esercita le vere API SDK `push-sealed`/`pull-sealed` senza flag demo, verifica `VersionId` e `GetObjectRetention`, controlla che un `DeleteObject` con `VersionId` sia rifiutato e che un controllo non protetto sia eliminabile, legge una versione protetta tramite ID dopo un delete marker e verifica che il download per chiave corrente fallisca chiuso. Il set dimostrativo contiene un dump PGDMP sintetico e manifest S3 autenticato. La CI Compose continua ad esercitare PostgreSQL 17 e 4 documenti reali, ma sul SeaweedFS **senza** garanzia WORM (modalità test esplicita). Non sono testati Object Lock su AWS reale, recupero di set storici completi, KMS, geo-offsite e backup di produzione.

## Historical WORM restore con VersionId espliciti (ADR 0027, #6)

`SealedBundleOffsite.DownloadHistoricalAsync` usa un insieme di versioni richiesto dall'operatore per **tutti** i ciphertext, index e marker del backup remoto. Non ricorre mai implicitamente alla versione corrente o a una ricerca arbitraria nelle versioni: ogni `GetObject` specifica `VersionId`; l'insieme deve coincidere esattamente con le key dell'inventario **HMAC autenticato e vincolato al backup ID**. Verifica `GetObjectRetention` per ogni versione COMPLIANCE, hash e tag GCM prima di rinominare la directory privata. La CLI `pull-historic` usa `PERITIAL_BUNDLE_CONFIRM=ISOLATED_HISTORICAL_DOWNLOAD` e `PERITIAL_OFFSITE_VERSION_SELECTION_FILE` (parser JSON v1 stretto, duplicati/path traversal/missing keys rifiutati), **senza bypass CI**. Il job `worm-recovery` estende `ObjectLockWormIntegrationTests` con un blob cifrato oltre 1 MiB e due delete marker, e verifica il recupero completo e le condizioni negative. Per il formato JSON e il runbook vedere `docs/sealed-multiservice-recovery.md`. Restano esclusi AWS S3 di produzione, key lifecycle KMS, IAM minimo e recovery automatizzato delle versioni storiche.

## Due ruoli S3 scoped nella CI WORM (ADR 0028, #6)

La suite `worm-recovery` genera due IAM JSON con `scripts/recovery/render_worm_iam.py`, li valida con `tests/backup/test_worm_iam_policies.py` (nomi bucket e scope, rifiuto di wildcard/DELETE/admin, permessi 0600 e nessun overwrite), e usa `mcli admin` sul solo server SILO effimero per associare policy a utenti **separati**. `ObjectLockWormIntegrationTests` continua a creare e configurare il bucket con identità admin, ma esegue `SealedBundleOffsite.UploadAsync` soltanto con il writer scoped, `DownloadAsync/DownloadHistoricalAsync` soltanto con il reader scoped. I test rifiutano DELETE su un bucket di controllo senza Object Lock, scritture del reader, modifiche del bucket da parte del writer, elenco dello storico da parte del writer e accesso alle key fuori da `sealed-v1/`. Credenziali CI dimostrative; AWS IAM role/STS e KMS produttivi non ancora integrati. Vedere il runbook di recovery e ADR 0028.

**Revisione IAM:** il server SILO tratta `GetObjectVersion` come `GetObject` e può permettere `ListBucketVersions` con il normale `ListBucket`. L'uploader non ha perciò più `GetObject` né `GetObjectVersion`. `SealedBundleOffsite.UploadAsync` richiede in modalità reale un secondo client read-only `verificationSource`, distinto dall'uploader, per i GET di confronto SHA-256 dopo ogni PUT. Il comando `push-sealed` reale richiede credenziali separate `PERITIAL_OFFSITE_VERIFY_S3_ACCESS_KEY` e `PERITIAL_OFFSITE_VERIFY_S3_SECRET_KEY`. La CI WORM prova che l'uploader non può leggere ciphertext correnti o storici e che il reader non può modificarli; il mancato elenco di VersionId **non** è verificato su SILO. La simulazione AWS IAM/STS e la rotazione restano aperte.
