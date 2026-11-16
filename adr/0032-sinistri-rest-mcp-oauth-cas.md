# ADR 0032 — Primo use case Sinistri condiviso REST/MCP con versioni obbligatorie

**Stato:** Accepted, incremento parziale #7/#12/#21.  
**Data:** 2026-11-16

## Contesto

Con V003, `Sinistro` e `PgSinistroRepository` dispongono già di
`FORCE RLS`, scritture con audit nella medesima transazione e `version`
per compare-and-swap (ADR 0031). Non erano però disponibili i contratti
utente e una fonte unica di autorizzazione per REST e MCP. Un endpoint
che accettasse tenant dalla richiesta o rilegga la riga prima di aggiornare
ignorando la versione del client permetterebbe escalation e lost update.

## Decisione

- Introdurre il contratto `ISinistroRepository` e il servizio condiviso
  `SinistroService` nel Business layer. Ogni GET richiede
  `peritial.read` **o** `peritial.write`; mutazioni `peritial.write`.
  Identità tenant e subject provengono da `ITenantContext` alimentato
  esclusivamente dai claim JWT firmati: **nessun parametro tenant**
  in API o MCP.
- API REST: `GET /api/sinistri?limit=50`, `GET /api/sinistri/{id}`,
  `POST /api/sinistri` (201) e `PUT /api/sinistri/{id}`. Creazione:
  `externalReference: string`, `occurredOn: DateOnly` ISO
  `YYYY-MM-DD`, `reportedOn?: DateOnly|null`. Aggiornamento:
  gli stessi campi più `expectedVersion: long` **obbligatoria**.
  Una versione obsoleta restituisce **HTTP 409**, ID mancante o
  di altro tenant **404**. Scope negato: **403**, token assente: **401**.
- MCP: `list_sinistri`, `get_sinistro`, `create_sinistro`,
  `update_sinistro` chiamano i **medesimi metodi** di Business.
  Il tool update richiede `expectedVersion` come il REST.
  L'errore MCP è rappresentato nel risultato del protocollo, non
  necessariamente con lo stesso codice HTTP 409.
- Le risposte DTO contengono **solo**
  `id`, `externalReference`, `occurredOn`, `reportedOn`,
  `createdAtUtc`, `version`: niente `tenantId`, subject,
  audit, configurazioni PostgreSQL o riferimenti al legacy.
- `SinistroService.UpdateAsync` confronta la versione fornita dal
  chiamante con quella letta, ma il controllo definitivo è il CAS
  PostgreSQL per garantire la sicurezza in concorrenza. La versione
  cresce soltanto dopo COMMIT. Gli eventi `sinistro.create` e
  `sinistro.update` riusciti sono atomici con i dati; i tentativi
  senza scope generano audit `denied` senza ID o payload.
- Il bootstrap Compose demo resta deliberatamente lo schema
  **V001 non journaled** e **non** esegue V003 all'avvio. Solo la CI
  **effimera** applica il contenuto additivo V003 tramite `psql`
  come fixture prima dei test OAuth+Playwright: non è un
  workflow di upgrade, né crea un falso journal. Ambienti reali
  devono applicare la migrazione versionata attraverso il migrator
  esplicito con account DDL e backup come da
  `docs/database-migrations.md`.

## Verifica e limiti

TDD RED con servizio/porta mancanti, test xUnit Business su
scope read/write, audit denied e riusciti, isolamento tenant,
cap 1–100 e expectedVersion. Test Playwright con Keycloak
reale su REST e MCP verificano creazione, lettura, update,
stale 409, anonimo 401 e assenza di tenant ID nei DTO.
Infrastructures V003 e CAS già provate su PostgreSQL reale.
Le risposte API/MCP sono `Cache-Control: no-store`.

Non è CRUD completo: **DELETE, ricerca/autocomplete, filtri,
collegamento sinistri↔perizie/polizze/anagrafiche, UI PWA e
import datetime legacy** restano non implementati. Sono ancora
necessari deployment/migrazione espliciti e casi E2E di
autorizzazioni negative con token di tenant diversi.
Le issue #7, #12 e #21 restano aperte.
