# ADR 0034 — V004: collegamento Perizie–Sinistri con FK tenant e audit atomico

**Stato:** Accepted per la prima relazione; issue #7, #9, #12, #21 ancora aperte.  
**Data:** 2026-11-17

## Problema

La V003 memorizza i sinistri con `(tenant_id,id)` e FORCE RLS; le perizie
V001 rimangono entità con PK composta `(tenant_id,id)`, senza FK verso un
sinistro. Collegare i due domini soltanto a livello REST/MCP rischierebbe di
creare associazioni cross-tenant, perdere link in race concorrenti o registrare
un audit di successo senza aver realmente salvato la relazione.

## Decisione

1. Applicare una nuova migrazione **additiva, forward-only V004**:
   `ALTER TABLE perizie ADD COLUMN sinistro_id uuid NULL`, vincolo
   `FOREIGN KEY (tenant_id,sinistro_id) REFERENCES sinistri(tenant_id,id)`
   con `ON UPDATE/DELETE RESTRICT`, indice parziale
   `(tenant_id,sinistro_id,id)` dove il link è presente.
   Un sinistro può avere più perizie; una perizia può avere **al massimo
   un sinistro**. Le perizie già esistenti rimangono **non collegate**.
   Le migrazioni V001–V003 sono immutabili, con checksum conservati.
   Nessuna nuova tabella RLS: sia `perizie` sia `sinistri`
   conservano le proprie policy **ENABLE/FORCE RLS**.
2. Introdurre un port `IPeriziaSinistroLinkRepository` e un adattatore
   `PgPeriziaSinistroLinkRepository`. Entrambe le risorse vengono lette
   con tenant validato tramite `TenantDbScope`. Un riferimento assente
   oppure appartenente ad altro tenant produce indistintamente **404 REST**
   (errore MCP) prima della scrittura; la FK composta garantisce anche a
   livello database il divieto di collegare tenant diversi.
3. L'operazione di primo link usa
   `UPDATE perizie SET sinistro_id=@sinistro WHERE tenant_id=@tenant
     AND id=@perizia AND sinistro_id IS NULL`: un compare-and-swap
   **atomico**, che impedisce a due richieste concorrenti di
   associare o riassociare la stessa perizia. Se già associata:
   **409 REST**, anche se il sinistro richiesto è lo stesso; niente
   ricollegamento, rimozione o cascata impliciti.
4. Nella medesima transazione PostgreSQL del link riuscito viene inserito
   l'audit immutabile `perizia.link-sinistro / succeeded`, con
   `ResourceId=periziaId` e tenant/subject firmati; un audit fallito
   determina **rollback completo**. La Business service
   `PeriziaSinistroLinkService` autorizza la lettura
   con `peritial.read` o `peritial.write` e il link con
   `peritial.write`. Denial lato Business genera un evento
   `perizia.link-sinistro / denied` senza identificativi/payload.
   Il 403 bloccato dalla policy REST ASP.NET è auditato
   dalla policy generale di autorizzazione, non dal Business.
5. Nuovi contratti:
   - REST `POST /api/perizie/{id}/sinistro` body `{"sinistroId":"UUID"}`,
     201 `{"periziaId":"UUID","sinistroId":"UUID"}`;
     `GET /api/sinistri/{id}/perizie` restituisce GUID perizie,
     **non** le entità complete.
   - MCP `link_perizia_sinistro(periziaId,sinistroId)` e
     `list_perizie_for_sinistro(sinistroId)`, stesso servizio Business.
   Nessun `tenantId` viene richiesto o accettato per determinare
   l'identità. Le risposte non contengono tenant o audit metadata.
6. Compose demo parte ancora dallo schema storico **V001 non journaled**;
   solo la CI effimera applica V003 poi V004 come fixture SQL per testare
   REST/MCP. La migrazione dei database reali è **esplicita**, con il
   migrator privilegiato, backup e lock/checksum. Nessun startup-DDL/API,
   nessun trasferimento dati dal ramo storico `death`.

## Verifica

- TDD RED su PostgreSQL reale quando mancava l'adattatore link, quindi GREEN.
- Test reali con due tenant e FK composita (incluso UPDATE amministrativo
  cross-tenant rifiutato), link duplicato/CAS 409 e audit atomico con
  simulazione di duplicato audit per verificare il rollback.
- Test Business per scope firmati, denial audit, DTO allowlist e
  mancata enumerazione dell'altro tenant.
- E2E Playwright Keycloak con due JWT firmati su REST e MCP:
  creazione di sinistri/perizie, link A REST e B MCP, letture tenant-scoped,
  404 per risorse mancanti/esterne, 409 replay, 401/403.
- CLI migrator e test baseline legacy/pre-lease/restore PG aggiornati
  al journal **V001–V004**. Il restore deve mantenere anche
  `perizie.sinistro_id` di tenant A; i dati tenant B rimangono non collegati.

## Limiti

Questa non è una soluzione completa per il porting delle relazioni storiche:
**non** include cambio sinistro, unlink, perizia↔polizza/anagrafica,
import di ID legacy, E2E Entra produttivo o UI PWA. L'elenco relazioni
restituisce GUID, non un documento composto con informazioni personali.
Le issue #7, #9, #12, #21 e #6 restano aperte.
