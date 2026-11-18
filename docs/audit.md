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

Dopo il PUT i metadata e l'audit vengono confermati nella medesima transazione che consuma `upload_intents`. Se il commit fallisce o perde l'ACK, il sistema **non elimina subito il blob**: il worker PostgreSQL dedicato verifica la referenza prima di pulire i record scaduti. Un lock sul record d'intento impedisce che il worker elimini un blob e un commit successivo lo referenzi. Il worker usa ruolo ristretto `peritial_recovery`, con accesso solo alle chiavi pendenti e a tenant/storage_key documentali; non può leggere nomi, contenuti o audit. Un errore di cancellazione conserva il record per retry. Rischi: richieste non idempotenti, upload troppo lenti rispetto al grace period, gestione operativa del worker e migrazioni di produzione (ADR 0011, #21/#6).

Il completamento delle richieste idempotenti viene registrato su `upload_request_keys` nello stesso commit di `documents` e `security_audit`. Un replay completato non crea un secondo audit. Il ruolo recovery può leggere solo identificativi documento/tenant e stato, non i digest della richiesta, del soggetto o della chiave. Le chiavi completate richiedono retention controllata (#19).

## V005 Sinistri: commit audit/DELETE atomico (#7/#21)

`sinistro.delete` produce un audit append-only con tenant e subject
firmati, azione, outcome e UUID della risorsa, **mai** il contenuto,
numero di sinistro o body HTTP/MCP. L'operazione DELETE condizionata
alla `version` e l'inserimento dell'audit sono nella **stessa
transazione PostgreSQL**; un audit fallito annulla la cancellazione.
Un errore FK `fk_perizie_sinistro_same_tenant` impedisce la mutazione
e non emette un falso successo. Un diniego di scope ha l'outcome
`denied` senza resource ID. [ADR 0041](../adr/0041-sinistri-v005-audited-cas-delete.md).

## ADR 0042 — audit per gate hard-delete Sinistri (#22)

Le chiamate DELETE rifiutate da `peritial.write` mancante,
`peritial.sinistri.delete` mancante o
`SinistroDeletionGate` spento appendono
`sinistro.delete/denied` con `resource_id = NULL`, senza
consultare la riga, il riferimento esterno o i link alla Perizia.
L'audit di successo resta **transazionale** con il DELETE
e disponibile soltanto quando scope e gate Development sono
entrambi soddisfatti. L'assenza di una policy retention/legal hold
mantiene il gate di produzione disabilitato.
[ADR 0042](../adr/0042-sinistri-delete-development-gate.md).

## Perizia.unlink-sinistro (#7/#9/#21, ADR 0043)

Lo scollegamento esplicito registra `perizia.unlink-sinistro`
come evento append-only `succeeded` con resource UUID della
Perizia, tenant e soggetto firmati, **nel medesimo commit**
dell'UPDATE CAS su `perizie.sinistro_id`. Conflitti e ID
assenti/foreign non generano false righe di successo.
Denial OAuth `peritial.write` è auditato con
`outcome=denied` e `resource_id=NULL` prima di accedere
ai dati. Si conserva la storia del link originario nell'audit
`perizia.link-sinistro`; la rimozione dell'associazione non
cancella audit, Sinistri, Perizie o documenti.
[ADR 0043](../adr/0043-perizia-sinistro-explicit-unlink.md).

## Perizia.reparent-sinistro — audit transazionale CAS (#21/#9, ADR 0044)

La singola mutazione da `expectedSinistroId` a
`newSinistroId` registra `perizia.reparent-sinistro/succeeded`
nel medesimo `TenantDbScope`. Se l'audit fallisce,
l'UPDATE viene rollbackato; un CAS/tenant/stato terminale
non soddisfatto non genera falsi successi. Il diniego scope
viene auditato separatamente con `resource_id=NULL`.
La riga audit è allowlisted (Perizia UUID, tenant/soggetto,
azione, outcome, timestamp): **non contiene ancora i due UUID
source/target né una revisione monotona della relazione**.
Questi dati richiedono policy di disclosure e modello eventi
dedicato, prima di definirla una traccia probatoria completa.
[ADR 0044](../adr/0044-perizia-sinistro-atomic-reparent.md).
