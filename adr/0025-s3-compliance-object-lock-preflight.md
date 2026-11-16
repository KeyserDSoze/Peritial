# ADR 0025 — Preflight fail-closed per S3 Object Lock COMPLIANCE del backup cifrato

Data: 2026-11-16. Stato: implementato in codice e testato con unit test + rifiuto su SeaweedFS demo; **non** validato contro S3 Object Lock di un servizio reale. Issue #6/#18/#21 aperte.

## Problema

ADR 0024 dimostra replica cifrata dei byte dei backup PostgreSQL+S3 su una terza istanza SeaweedFS e marker `complete`, ma `If-None-Match: *` non impedisce a un amministratore di cancellare o nascondere oggetti. Un marker HMAC assicura integrità ma non disponibilità né retention. Non si devono presentare i risultati del laboratorio come immutabilità verificata.

## Decisione

- Modalità predefinita del `push-sealed` **fail-closed**: `GetBucketVersioning` deve restituire `Enabled`, `GetObjectLockConfiguration` deve restituire Object Lock `Enabled` e una `DefaultRetention` `COMPLIANCE` di almeno 30 giorni, in giorni o anni. `GOVERNANCE`, stato sospeso, regola mancante, durata inferiore, accesso negato o API non supportate rifiutano l'upload **prima della prima PUT**.
- Per ogni PUT di ciphertext, inventario, MAC e `complete`, controllare il `VersionId` specifico restituito dall'API e chiamare `GetObjectRetention` per **quel version ID**: `COMPLIANCE` e scadenza valida. Un server che dichiara policy ma non offre versioni/retention effettive non consente il completamento della replica.
- La via predefinita di `pull-sealed` riverifica sia configurazione bucket sia **versione corrente e retention** di tutti gli oggetti che compongono il backup, senza salvare plaintext. Un delete marker o una sostituzione non protetta devono portare al rifiuto; il recovery da versioni storiche non correnti sarà una tranche distinta.
- L'unica eccezione demo è richiesta esplicitamente con `PERITIAL_OFFSITE_CI_HTTP=DISPOSABLE_TEST_ONLY` e accettata dal CLI soltanto con host `http://seaweedfs-backup`, mai su endpoint HTTPS o host esterni. Il codice API usa `disposableUnprotectedDemo=false` per default; non rivendicare retention sul test locale.
- TDD: test puri di configurazioni corrette e deboli e retention effettiva delle versioni; test SeaweedFS reale che dimostra il rifiuto automatico e assenza di PUT/dir pubblicate; drill funzionale di trasferimento e restore rimane **solo demo**.

## Sicurezza residua e prossimi passi

La retention COMPLIANCE protegge versioni S3 ma non garantisce accessibilità del nome corrente (delete markers), recupero da versioni precedenti, integrità della configurazione IAM, sopravvivenza del provider, replica geografica né backup delle chiavi crittografiche. La verifica API è una garanzia condizionata all'implementazione corretta del server. Non è stata eseguita la prova di end-to-end su AWS S3/MinIO con Object Lock effettivamente abilitato e tentativo reale di cancellazione: questo rimane requisito aperto. Sono inoltre mancanti policy di retention offsite, KMS/rotazione delle chiavi, restore Keycloak/Azure, multipart upload per archivi grandi e snapshot DB↔S3 coerenti sotto traffico. Non chiudere #6/#18/#21.

## Stato successivo (ADR 0026, 2026-11-16)

La nota iniziale sull'assenza della prova **positiva** WORM era corretta quando fu scritta. In seguito è stato aggiunto il job indipendente `worm-recovery` con backend SILO compatibile S3 e bucket COMPLIANCE/versioning reali: verifica sia il rifiuto della cancellazione protetta sia il successo di cancellazione nel controllo non protetto, e il mantenimento della versione storica dopo un delete marker. Rimangono da verificare un provider AWS gestito, policy IAM minime, recupero dell'intero backup dalle versioni precedenti e offsite geografico/KMS. Consultare l'ADR 0026.
