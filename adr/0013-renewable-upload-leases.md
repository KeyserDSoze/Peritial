# ADR 0013 — Lease rinnovabili degli upload e cleanup protetto

Stato: implementata e verificata in PostgreSQL/Compose; fault injection reale mid-PUT aggiunta in ADR 0014 (15 novembre 2026).

## Problema
ADR 0011 attende almeno un'ora prima di rimuovere intenti abbandonati, ma l'età del record non distingue un processo morto da un PUT remoto legittimo ancora bloccato. Il worker poteva cancellare l'oggetto mentre l'upload era ancora in corso, anche se l'intento era protetto da lock durante la rimozione dei metadata.

## Decisione
1. `upload_intents.lease_expires_at_utc` è un timestamp PostgreSQL UTC aggiunto da bootstrap idempotente per installazioni già avviate. All'atto della prenotazione è impostato a due minuti dopo l'ora DB.
2. L'applicazione può aggiornare **soltanto la colonna del lease** (grant `UPDATE (lease_expires_at_utc)`, FORCE RLS tenant-scoped). `PgUploadIntentLedger.RenewAsync` prolunga il lease di due minuti soltanto se l'intento esiste, appartiene al tenant e il lease precedente non è ancora scaduto: un lease scaduto non può essere recuperato unilateralmente dal produttore.
3. `UploadLeaseHeartbeat` rinnova il lease ogni 20 secondi mentre `IObjectStore.PutAsync` è in corso. Se il rinnovo fallisce o il record non esiste più, annulla il token dell'upload e impedisce un commit successivo, anche se l'adapter ha ignorato la cancellazione. Al termine di PUT richiede un ulteriore rinnovo prima di aprire la transazione metadata.
4. `PgDocumentRepository.AddAuditedAsync` elimina il record d'intento **soltanto se il lease è ancora valido** nella stessa transazione che crea il documento, l'audit e il completamento della chiave idempotente. Se il lease è scaduto non scrive successi.
5. Il worker recupera un upload soltanto quando **entrambi** i criteri sono veri: intento più vecchio di un'ora e lease scaduto secondo l'orologio PostgreSQL. `FOR UPDATE SKIP LOCKED` e check di `documents(tenant_id,storage_key)` rimangono necessari per evitare corse e cancellazioni di documenti già committati.
6. Se PUT fallisce, il cleanup sincrono è idempotente e conserva un intento per il worker quando la cancellazione fallisce; un commit con ACK incerto continua a essere riconciliato, mai cancellato preventivamente.

## Verifiche
- `UploadLeaseTests` usa PostgreSQL reale per controllare che un intento vecchio con lease rinnovato sia ignorato, che un lease scaduto sia recuperabile, che un altro tenant non possa rinnovarlo e che il repository rifiuti metadata/audit senza lease valida.
- `UploadLeaseHeartbeatTests` verifica con timer breve il rinnovo ripetuto durante PUT bloccato, l'annullamento del PUT quando il lease si perde e il rinnovo finale prima del commit.
- `UploadRestartContractTests`, eseguito in CI Compose, uccide l'API con `SIGKILL`, prepara **dopo l'arresto** un oggetto S3 realmente salvato con intento backdatato e lease scaduto, riavvia l'API e verifica cleanup in SeaweedFS, rimozione dell'intento e rilascio idempotency key. Dimostra recovery dopo riavvio su servizi reali, **non** un kill esattamente nel mezzo del PUT HTTP.

## Limitazioni e gestione operativa
- Questi lease sono un meccanismo di liveness con timeout. Un outage PostgreSQL oltre la durata residua o un PUT remoto che ignora la cancellazione può produrre una gara con il worker: la richiesta viene bloccata prima del commit e il cleanup è best effort. La nuova prova mid-PUT verso S3 è in ADR 0014; restano outage PostgreSQL, multi-replica e provider Azure reali.
- La soglia di una ora è un periodo di grazia addizionale, non una sostituzione del lease. Per produzione servono metriche su rinnovi mancati, worker backlog, cleanup falliti, e alert (#19).
- Migrare la colonna e il grant limitato prima di distribuire nuove istanze; il solo cambio applicativo senza migrazione impedisce PUT/commit. Test su più repliche, failure distribuite e backup/restore sono ancora gate aperti (#6/#17/#18/#21).
