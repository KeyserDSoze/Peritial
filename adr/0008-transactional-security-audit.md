# ADR 0008 — Audit atomico alle scritture perizia e ai metadati documentali

Stato: adottata per i dati PostgreSQL, non ancora per object storage (2026-11-15).

## Problema
In origine perizie e documenti venivano inseriti/aggiornati nel repository e solo dopo veniva chiamato PgSecurityAuditTrail.AppendAsync, aprendo una transazione distinta. Un errore audit o un crash intermedio poteva lasciare una mutazione riuscita senza evento. REST e MCP usano gli stessi servizi Business, perciò il problema riguardava entrambi.

## Decisione
- I contratti IPeriziaRepository espongono AddAuditedAsync e UpdateAuditedAsync. IDocumentRepository espone AddAuditedAsync. Le mutazioni di Business chiamano **solo** tali metodi, passando SecurityAuditEvent con tenant, subject, azione ed esito derivati dall'identità JWT.
- PgPeriziaRepository/PgDocumentRepository aprono una sola TenantDbScope: impostano il tenant PostgreSQL con set_config(..., true) nella transazione, eseguono la query business, poi PgSecurityAuditTrail.AppendWithinScopeAsync, infine COMMIT. Un errore a qualsiasi punto impedisce il commit di entrambe le righe.
- Validare tenant, identificativo risorsa, tipo di azione ed esito prima di scrivere. Conservare le policy FORCE RLS e il ruolo DB INSERT-only sull'audit.
- I rifiuti di accesso sono eventi persistiti autonomamente: non appartengono a un commit business che non si è verificato.
- Mantenere i precedenti metodi mutatori dei repository soltanto come seam legacy/test. Ogni nuovo caso d'uso di produzione deve usare l'operazione con audit atomico.

## Evidenze TDD
- PeriziaAtomicAuditTests verifica successi, rollback in caso di evento audit con ID duplicato, stato invariato dopo fallimento UPDATE, retry applicativo con evento nuovo e incompatibilità cross-tenant.
- DocumentAtomicAuditTests verifica metadati+audit nella stessa transazione, rollback del metadata su errore audit e rifiuto cross-tenant. Test su ruolo PostgreSQL con FORCE RLS in CI.
- Compose/Playwright mantiene verifica OAuth PKCE e MCP/REST con conteggi reali audit.

## Decisioni rinviate, blocchi produzione
- L'object storage S3/Azure Blob non partecipa alla transazione PostgreSQL. FileIngestionService salva prima l'oggetto e può creare un orfano se il commit DB fallisce. Servono staging, quarantena antivirus, compensazione/outbox e recovery; issue #5 e #21.
- Gli ID audit nuovi non rendono automaticamente idempotenti le richieste ritrasmesse. Servono idempotency key/correlation ID stabile, deduplicazione e test su retry HTTP/MCP.
- Manca un piano versionato di migrazione e recovery/retention del registro audit (#6/#19). Non dichiarare la issue #21 completa prima di questi criteri.
