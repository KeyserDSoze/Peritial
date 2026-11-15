> Aggiornamento: per non recuperare upload ancora attivi oltre l'ora di grazia, ora si usa anche una lease rinnovabile, vedi ADR 0013. Il worker interviene solo quando intenzione vecchia **e** lease scaduta.

# ADR 0011 — Intenzione di upload e reconciler con lock anti-race

Stato: adottata nella stack Compose e nei test PostgreSQL; gate Azure/on-prem produzione ancora aperti (2026-11-15).

## Problema
La compensazione sincrona di ADR 0009 non recupera un processo ucciso dopo PUT. Inoltre un errore durante COMMIT può significare soltanto che il client ha perso la risposta: cancellare immediatamente il blob può rompere un documento già committato. I sistemi S3/Azure Blob e PostgreSQL non hanno una transazione condivisa.

## Protocollo
1. Dopo firma/MIME/ClamAV, l'API genera document_id e storage_key nel namespace del tenant; `PgUploadIntentLedger.RecordAsync` committa la prenotazione prima di PUT. Un errore di prenotazione blocca PUT.
2. Se PUT fallisce, la compensazione sincrona con token indipendente tenta DELETE e `ForgetAsync`. Se DELETE fallisce o il processo si interrompe, l'intento resta nel database per il worker.
3. Dopo PUT riuscito, il servizio chiama `PgDocumentRepository.AddAuditedAsync`. In una sola transazione PostgreSQL tenant-scoped il repository esegue DELETE della prenotazione **prima** di INSERT documents e INSERT audit. Se l'intento è sparito, l'intera operazione fallisce senza documento. Nessun DeleteObject si esegue immediatamente in caso di errore dal repository, poiché l'esito di COMMIT è potenzialmente incerto.
4. Un worker periodico, con connessione `peritial_recovery` separata, esamina soltanto gli intenti più vecchi di un'ora. `SELECT ... LIMIT 1 FOR UPDATE SKIP LOCKED` mantiene il row lock, verifica l'esistenza di un documento con la stessa coppia tenant_id/storage_key, elimina l'oggetto solo se non referenziato e consuma l'intento **nella stessa transazione**. Se DELETE oggetto fallisce, la transazione PostgreSQL viene annullata e l'intento rimane disponibile al prossimo giro. Se il worker muore dopo DELETE ma prima del commit, il successivo DELETE idempotente è innocuo.
5. Se scrittura documento e worker competono, la rimozione dell'intento serializza i due percorsi. Chi perde la prenotazione non può creare metadata/audit che puntino a un blob già pulito.

## Isolamento e privilegi
- `upload_intents` contiene soltanto UUID tenant/documento, storage key UUID e timestamp, mai contenuto, filename o token. `peritial_app` usa FORCE RLS tenant-scoped con INSERT/SELECT/DELETE, senza visibilità cross-tenant.
- `peritial_recovery` è un ruolo distinto `NOSUPERUSER` `NOBYPASSRLS`: SELECT/UPDATE/DELETE sugli intenti per locking/cleanup; SELECT soltanto delle colonne tenant_id e storage_key su documents. Non ha accesso a security_audit o filename. Connessione protetta da secret manager e rete privata, non condivisa con API pubbliche.
- La demo Compose provisiona password statiche **solo per sviluppo**; produzione richiede migrazione versionata e segreti ruotabili, incluse copie di sicurezza.

## Prove e limiti
- Test xUnit su PostgreSQL verificano intent prima di PUT, retry dopo crash simulato, rollback su DeleteObject fallita, protezione documento referenziato con ACK incerto, rifiuto metadata senza intento, separazione privilegi recovery e grace period. CI Docker Compose verifica REST/MCP e numero di intenti zero dopo i successi.
- **Non è ancora idempotenza HTTP/MCP**: una richiesta ritentata può creare un documento nuovo. La finestra di un'ora presuppone che PUT+commit normale termini molto prima; serve un protocollo di lease/heartbeat per upload eccezionalmente lenti. Non sono state effettuate prove kill/restart dell'API e del worker con provider reali, né deployment/monitoraggio production-ready. Questi restano criteri aperti delle #21/#5/#6/#19.
