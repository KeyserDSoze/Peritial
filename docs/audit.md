# Audit di sicurezza REST/MCP — primo vertical slice

## Registrazioni durevoli
Il servizio Business registra in PostgreSQL nella tabella security_audit esclusivamente i campi tenant_id (da JWT firmato), subject (sub firmato), action, outcome, resource_id (UUID opzionale), occurred_at_utc e id.
Le azioni attuali sono perizia.create, perizia.assign, perizia.advance, document.ingest e authorization.forbidden. Gli esiti sono succeeded o denied. Non salvare mai riferimento perizia, nominativo assegnatario, file name, contenuti, base64, token Bearer, argomenti MCP, body HTTP o credenziali.

## Flussi REST/MCP e protezioni DB
I tool MCP e le API REST condividono gli stessi servizi Business; i tool negati per scope insufficiente generano eventi denied. Il callback JWT OnForbidden registra i 403 con identità già validata. I 401 senza identità verificata non sono associabili in sicurezza a un tenant e restano nei log operativi.
Le policy PostgreSQL ENABLE/FORCE ROW LEVEL SECURITY proteggono security_audit. Il ruolo applicativo peritial_app può soltanto fare INSERT, non SELECT, UPDATE o DELETE. Gli investigatori autorizzati usano un ruolo amministrativo separato e tracciato; nessun endpoint REST/MCP espone il registro.
Test xUnit verificano gli eventi Business, i privilegi e il fail-closed RLS su PostgreSQL reale. La CI Compose verifica la persistenza dei successi REST/MCP e ingestion e dei rifiuti mediante query effettuate esclusivamente dall'account privilegiato del database di test.

## Limitazioni di sicurezza prima della produzione
Le mutazioni dei dati e gli insert di audit sono al momento in transazioni separate: crash tra i due oppure errore scrittura audit dopo un commit possono lasciare dati privi di audit o produrre un HTTP 500 a mutazione già eseguita. Prima della produzione serve un transactional outbox o unit of work atomica, con retry/idempotenza e prove fault-injection (#6/#19).
Mancano inoltre retention con immutabilità esterna, audit di ogni futuro use case, tracing/alert e test con Entra e client MCP esterni (#4/#17/#19).
