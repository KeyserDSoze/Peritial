# ADR 0012 — Upload idempotenti condivisi tra REST e MCP

Stato: adottata nel vertical slice con PostgreSQL e CI Compose (15 novembre 2026).

## Motivazione
Un retry dopo timeout, risposta COMMIT persa o disconnessione del tool MCP poteva creare un secondo documento e un secondo audit nonostante il worker di recovery di ADR 0011. Serve una chiave stabile e una relazione durevole tra richiesta, identità e risultato.

## Decisione
1. I client possono usare `Idempotency-Key` (REST) oppure `idempotencyKey` (tool `ingest_file` MCP). Sono ammessi 16–128 caratteri ASCII alfanumerici, trattino, punto, due punti e underscore. Il parametro è opzionale per compatibilità con i vecchi client.
2. SHA-256 di subject JWT firmato e chiave client definiscono una prenotazione per tenant/soggetto. La fingerprint SHA-256 canonica include nome sanitizzato, MIME normalizzato, identificativo perizia (se presente) e byte verificati; vengono memorizzati solo digest, non dati personali, token, filename, contenuto o chiavi in chiaro.
3. `upload_request_keys` usa FORCE RLS e unique key `(tenant_id,subject_digest,key_digest)`. `INSERT ... ON CONFLICT DO NOTHING` è l'arbitro fra istanze: una sola richiesta riceve `Acquired` e può iniziare PUT; le altre vedono `InProgress`. Impronta differente produce conflitto. Se `Completed` legge il documento già autorizzato e restituisce lo stesso DTO senza nuovo PUT o audit.
4. Il repository documenti aggiorna lo stato a `completed` **nella stessa transazione** che consuma `upload_intents`, inserisce documenti e audit. Una conferma di commit smarrita non perde l'ID del documento. Se PUT fallisce e cleanup riesce, si rilascia la key prima di dimenticare l'intento; se il commit è incerto, non si cancella il blob.
5. Il reconciler sotto lock rilascia le key pending solo dopo aver eliminato un orphan non referenziato; se trova metadata esistenti preserva l'oggetto e ripara l'eventuale key pending marcandola completed. Il ruolo recovery ha visibilità minima su tenant/document ID/stato, mai sui digest.

## Verifiche
- xUnit PostgreSQL verifica simultaneità, separazione tenant e soggetto, mismatch, completamento unico, recupero e privilegi minimi. Business verifica digest dei byte e dei metadati. Playwright full-stack invia upload via REST, retry REST e MCP con stessa chiave, e verifica stesso documento e HTTP 409 su contenuto modificato. La CI confronta conteggio metadata con audit.

## Limiti non rimossi
- La chiave è opzionale; senza di essa i retry restano non idempotenti. Le chiavi completed persistono fino a una policy di retention esplicita; cancellarle rende di nuovo possibili duplicati. Richieste pending simultanee ricevono 409 e vanno ritentate dal client, non c'è long polling.
- Il protocollo di lease per upload lentissimi e i test reali di kill/restart/failover, migrazioni versionate, backup e provisioning Azure/on-prem rimangono gate #21/#6/#17/#18. Questo vertical slice copre i documenti, non ancora tutti i tool mutativi.
