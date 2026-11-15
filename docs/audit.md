# Audit di sicurezza REST/MCP — primo vertical slice

## Registrazioni durevoli
Il servizio Business registra in PostgreSQL nella tabella security_audit esclusivamente i campi tenant_id (da JWT firmato), subject (sub firmato), action, outcome, resource_id (UUID opzionale), occurred_at_utc e id.
Le azioni attuali sono perizia.create, perizia.assign, perizia.advance, document.ingest e authorization.forbidden. Gli esiti sono succeeded o denied. Non salvare mai riferimento perizia, nominativo assegnatario, file name, contenuti, base64, token Bearer, argomenti MCP, body HTTP o credenziali.

## Flussi REST/MCP e protezioni DB
I tool MCP e le API REST condividono gli stessi servizi Business; i tool negati per scope insufficiente generano eventi denied. Il callback JWT OnForbidden registra i 403 con identità già validata. I 401 senza identità verificata non sono associabili in sicurezza a un tenant e restano nei log operativi.
Le policy PostgreSQL ENABLE/FORCE ROW LEVEL SECURITY proteggono security_audit. Il ruolo applicativo peritial_app può soltanto fare INSERT, non SELECT, UPDATE o DELETE. Gli investigatori autorizzati usano un ruolo amministrativo separato e tracciato; nessun endpoint REST/MCP espone il registro.
Test xUnit verificano gli eventi Business, i privilegi e il fail-closed RLS su PostgreSQL reale. La CI Compose verifica la persistenza dei successi REST/MCP e ingestion e dei rifiuti mediante query effettuate esclusivamente dall'account privilegiato del database di test.

## Limitazioni di sicurezza prima della produzione
Le operazioni perizia.create, perizia.assign, perizia.advance e la persistenza dei metadata documentali registrano il relativo audit riuscito **nella stessa transazione PostgreSQL** con tenant scoped RLS. Se l'insert audit fallisce, il cambiamento del dato viene rollbackato; i test con audit ID duplicato dimostrano rollback sia di CREATE sia di UPDATE e metadata documentali. I dinieghi sono registrati in transazioni indipendenti, poiché non esiste una mutazione da committare.

**Il file fisico non è atomico con PostgreSQL.** La FileIngestionService scrive prima l'oggetto S3/Azure Blob: un errore del successivo commit PostgreSQL, una disconnessione o un crash possono lasciare un oggetto orfano. Non presentare il percorso come totalmente transazionale o idempotente: prima della produzione occorrono staging/quarantena, outbox/compensazione, retry idempotenti e fault injection end-to-end (#5/#21/#6). Inoltre i metodi legacy AddAsync/UpdateAsync esistono per le fixture di test e non devono essere usati per nuove mutazioni applicative senza audit.
Mancano inoltre retention con immutabilità esterna, audit di ogni futuro use case, tracing/alert e test con Entra e client MCP esterni (#4/#17/#19).

## Compensazione degli oggetti dopo errore (#5/#21)

Il servizio tenta la cancellazione della chiave S3/Azure Blob nuova se PutAsync o il commit metadata/audit lanciano un'eccezione. La cancellazione è idempotente e usa un timeout di 15 secondi distinto dal token richiesta. Un errore del cleanup viene restituito insieme a quello originale per attivare riconciliazione; non esiste ancora un worker che recuperi i file orfani lasciati da crash improvvisi. Gli acknowledgement di commit PostgreSQL persi richiedono riconciliazione prima di cancellare una possibile risorsa già referenziata. Non considerare questo flusso crash-safe o idempotente senza un registro transazionale di intenti e un worker di recovery (ADR 0009 e issue #21).
