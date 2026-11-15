# ADR 0014 — Fault injection S3 mid-PUT e riavvio API

Stato: provata in CI su `main` (15 novembre 2026), solo ambiente Compose di test.

## Requisito
La precedente prova `UploadRestartContractTests` uccideva l'API prima di scrivere un orfano S3 di test. Verificava il recupero durevole, ma non un'interruzione durante un trasferimento S3 autentico. Per la #21 è necessario osservare bytes del PUT realmente in volo prima di terminare il processo.

## Protocollo di test
1. Dopo le normali prove, avviare l'API di sviluppo con `docker/compose.chaos.yml`. L'overlay sostituisce soltanto l'endpoint S3 dell'applicazione e avvia il proxy `tests/chaos/s3_gate.py` sulla rete interna. Il proxy NON fa parte del deploy prodotto.
2. Ottenere un access token Keycloak firmato con il client `peritial-test-write`, abilitato al direct grant soltanto nel realm demo. Il client produttivo PWA mantiene Code+PKCE. Inviare un file PDF sintetico via multipart REST con idempotency key.
3. Il vero `MinioObjectStore` e AWS SDK iniziano un HTTP PUT. Il proxy inoltra header e fino a 4096 byte di corpo al vero SeaweedFS, poi resta intenzionalmente bloccato. Il file di segnalazione viene creato **solo dopo** che il prefisso del corpo è stato inoltrato, non alla semplice ricezione della richiesta. La CI richiede anche che il client REST sia ancora in corso.
4. Verificare la presenza dell'intento durevole mentre il PUT è bloccato, quindi inviare `SIGKILL` all'API. Solo dopo l'arresto forzare in test la scadenza di lease e grace period nel PostgreSQL reale, così non è necessario aspettare un'ora.
5. Riavviare l'API originale senza proxy. Il worker usa il ruolo dedicato, controlla la referenza e rimuove eventuali oggetti non referenziati; libera l'idempotency key.
6. `UploadMidPutContractTests` verifica PostgreSQL: zero intenzioni, chiavi pending, documenti e audit di successo per l'ID del PUT ucciso; con SDK S3 autenticato verifica che il key risponda HTTP 404. La job CI ha fallito se manca il segnale, il client è terminato prematuramente o il recovery non è avvenuto.

## Risultato
- Commit `61583a29`, pipeline https://github.com/KeyserDSoze/Peritial/actions/runs/43735731921: backend, frontend, Terraform e Compose tutti verdi. Il log Compose contiene il superamento di `UploadMidPutContractTests` e il marcatore `S3_MID_PUT_KILL_RECOVERED`.
- Il test osserva dati del corpo realmente inoltrati a SeaweedFS, ma non richiede che l'oggetto S3 sia visibile durante un PUT parziale: gli storage S3 possono esporre l'oggetto soltanto a completamento dell'operazione. Perciò è coerente osservare nessun blob persistito e un DELETE idempotente.

## Limiti residui
- Non prova failover multi-replica, interruzione rete dopo commit S3 ma prima della risposta, servizi Azure Blob reali, outage PostgreSQL prolungati o firme di rilascio. Si richiedono fault injection e monitoring specifici per tali scenari (#21/#17/#19).
- Non garantisce che una pipeline in produzione riesca ad annullare un S3 PUT remoto se l'SDK ignora il token: la protezione applicativa è il fencing del lease e del commit, con recovery best effort. La quarantena persistente rimane nella #5.
