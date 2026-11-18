# ADR 0041 — Sinistri V005: DELETE con CAS, audit atomico e FK RESTRICT

- Data: 18 novembre 2026
- Stato: implementato; piena CI finale da attestare sul commit HEAD
- Ambito: #7, #6, #12, #21; nessuna modifica a `death`.

## Contesto e vincoli

La tabella Sinistri (V003) accordava al ruolo app SELECT/INSERT/UPDATE
ma non DELETE. La V004 aggiunge a `perizie` il riferimento opzionale
`(tenant_id,sinistro_id)` con FK a `sinistri(tenant_id,id)`,
`ON DELETE RESTRICT`; i legami storici non vanno mai eliminati a cascata.
Mancavano operazione esplicita con versione, audit atomico e risposta
appropriata a errori FK; una semplice query DELETE sarebbe pericolosa.

## Decisione

1. Migrazione forward-only `V005__sinistri_audited_delete.sql`:
   `GRANT DELETE ON sinistri TO peritial_app;`. Nessuna modifica
   retroattiva alle migrazioni precedenti, nessuna cancellazione DDL,
   RLS resta FORCE, ruolo recovery invariato. Richiede CLI DDL e
   backup/maintenance di #6 su database versionati.
2. Il solo contratto applicativo: `DELETE /api/sinistri/{id}?expectedVersion=N`
   con scope `peritial.write` oppure MCP `delete_sinistro(id,expectedVersion)`
   con lo stesso servizio. Nessun parametro tenant supplied by user;
   `expectedVersion` positivo è obbligatorio. 204 REST / true MCP
   solo dopo il commit. Read-only e anonimi sono rifiutati.
3. `SinistroService.DeleteAsync` esegue autenticazione/scope,
   validazione versione, lettura tenant-scoped, early CAS e delega
   `DeleteAuditedAsync`. Repository esegue `DELETE ... WHERE
   tenant_id=@tenant AND id=@id AND version=@version` sotto
   `TenantDbScope` + FORCE RLS. Query parametrizzata; mancanti/
   foreign indistinguibili (404), versione diversa (409).
4. La FK V004 `ON DELETE RESTRICT` è il gate finale anche contro
   link concorrenti: violazione **specifica** del constraint name
   `fk_perizie_sinistro_same_tenant` è mappata a
   `SinistroLinkedPerizieException` (409). Nessuna cascata.
5. Stessa transazione PostgreSQL per DELETE e audit di successo
   `sinistro.delete`. Audit duplicato/fallito causa rollback
   della cancellazione. L'audit usa solo tenant, subject firmato,
   azione, outcome, resource UUID e timestamp; non memorizza body,
   riferimenti esterni o token.
6. La PWA **non espone ancora il pulsante irreversibile**. Prima
   servono policy retention/legal hold e regole di autorizzazione
   dedicate, conferma accessibile e procedure di ripristino. L'API
   resta una funzione amministrabile solo da un client con scope write,
   non un'attestazione di conformità normativa.

## Regressioni e limiti

Test RED prima del codice e GREEN successivo in xUnit Business,
PostgreSQL 17 reale con ruolo `peritial_app` non privilegiato, FK
RESTRICT e rollback audit, migration checksum/journal V005 e
adozione esplicita legacy; Playwright Keycloak desktop/mobile REST
e MCP con 204/400/401/404/409. Fixture CI V003+V004+V005
**solo** sul database Compose effimero; lo schema demo V001 e
`death` non vengono aggiornati.

Resta aperto: policy legal hold/retention, workflow di cancellazione
con approvazioni, restore auditato in caso di errore operativo,
soft delete/archiviazione se il dominio lo richiederà, perizie
reparent/unlink, full-text, anagrafiche e deploy reali (#7/#6/#21).

## Evoluzione successiva — ADR 0042

La V005 qui definita non viene modificata. Il successivo
[ADR 0042](0042-sinistri-delete-development-gate.md) impedisce
di fatto la cancellazione REST/MCP in ogni ambiente produttivo
finché non saranno codificati retention, legal hold, approvazioni
e scope dedicato. `peritial.write` da solo non autorizza più
`sinistro.delete` e persino il client dev con scope distruttivo
richiede abilitazione separata del gate Development.
