# ADR 0084 — Timeout anche sull'arbitraggio idempotency key XML

- Data: 22 novembre 2026
- Stato: implementato su `main`; CI del commit da attestare
- Issue: #24 (P0), #10, #21, #6
- `death`: branch storico non modificato

## Problema

V011 registra una prova di idempotenza con
`PRIMARY KEY (tenant_id,subject_digest,key_digest)`;
la FK a `sinistri` è differita. V012 serializza le
scritture Sinistri con il mutex per tenant/reference.
ADR 0082/0083 limitano a 1500 ms l'acquisizione
di quest'ultimo durante il commit XML interno.

Due importatori con **stessa chiave** e riferimenti
**diversi** non competono per il mutex del riferimento,
ma il secondo può attendere la decisione della prima
transazione nell'indice UNIQUE del ledger. Il codice
precedente ripristinava `lock_timeout` a 0 prima
dell'`INSERT ... ON CONFLICT`, quindi in assenza
di timeout DBA la seconda attesa era illimitata.

## Decisione

Applicare un timeout **solo alla prenotazione**
`INSERT INTO legacy_xml_import_keys ... ON CONFLICT`,
non all'intera operazione:

1. Dopo il mutex, leggere già `pg_settings.lock_timeout`
   e mantenere l'eventuale valore operatore minore
   o uguale a 1500 ms.
2. Poco prima dell'INSERT nel ledger, attivare
   nuovamente il massimo di 1500 ms se l'operatore
   non ha fissato un valore più restrittivo.
3. L'arbitraggio SQL della UNIQUE risolve success,
   replay o attesa di un commit altrui. Se la
   prenotazione scade con `55P03` o `57014` e
   il caller non ha cancellato la richiesta,
   propagare `LegacyXmlImportBusyException`
   (non `LegacyXmlIdempotencyConflictException`
   né presunta duplicazione del Sinistro).
4. Una volta completato l'INSERT, ripristinare
   il valore originario di `lock_timeout` prima
   di leggere il ledger o scrivere Sinistro/audit.
   Un errore abortisce tutta la transazione e
   ripristina implicitamente i setting con rollback.
5. Nessun retry automatico o endpoint pubblico
   aggiunto. I writer REST/MCP non ricevono il
   timeout applicativo dell'import.

## Verifica

Test PostgreSQL 17 reali con due pool del ruolo
applicativo e transazione RLS esplicita:
prima sessione inserisce una prenotazione ledger
uncommitted per un idempotency key, senza acquisire
il mutex del riferimento e senza inserire Sinistro.
La seconda importa lo stesso key con un
riferimento diverso: supera il mutex, ma il
vincolo UNIQUE è trattenuto. Deve terminare
con Busy in una finestra limitata, senza
ledger/Sinistro/audit success committed.

Ripetere anche con `statement_timeout=350ms`
imposto nella seconda connessione; la policy
del DBA è rispettata. Un tenant differente
con la stessa chiave può importare. Dopo rollback
della prima transazione, la seconda può riprovare
con gli stessi attore/chiave/XML, creare una
pratica ed effettuare replay idempotente senza
un ulteriore audit.

## Limiti

Resta da testare il comportamento con bulk
multi-reference, deadlock, timeout e carico di
produzione reale, conflitti storici, identità
canonica e import completo con timestamp e
allegati. Questa è soltanto una misura
**fail-closed di disponibilità** dell'import
interno, non autorizzazione a esporlo.
Nessuna modifica alle migrazioni e alle
regole di business del legacy.
