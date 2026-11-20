# ADR 0052 — Piano di riconciliazione XML legacy senza mutazioni

- Data: 20 novembre 2026
- Stato: implementato su `main`; CI HEAD da attestare
- Issue: #10, #7, #12
- `death`: nessun commit, solo riferimento storico

## Problema

L'ADR 0051 ha introdotto la preview Classic/Accertatore
ma non l'import effettivo. Lo schema V003 di `sinistri`
ha un indice `(tenant_id,external_reference)` **non unique**
perché le precedenti semantiche storiche (es. assicuratore,
annualità) non sono ancora caratterizzate. Trattare un
`NumeroSinistro` come chiave globale o tenant-unica e
fare `FindFirst`/upsert in automatico selezionerebbe
potenzialmente il record sbagliato o sovrascriverebbe
un Sinistro legittimo. Il solo controllo preview non
protegge dalle inserzioni concorrenti.

## Decisione

Nessuna nuova migrazione, indice, permesso o scrittura.
Aggiungiamo `LegacyXmlImportPlanService` e un adapter
PostgreSQL `PgLegacyImportReferenceLookup`. La richiesta
passa per il parser dell'ADR 0051, autenticazione JWT
con `peritial.write`, input di max 1 MiB e
`TenantDbScope` con FORCE RLS.

La SELECT contiene solamente
`id,occurred_on,reported_on`, filtra per
`tenant_id=@tenant AND external_reference=@reference`,
usa match **esatto** (non ILIKE, nessuna interpolazione
SQL) e `LIMIT 2`. Sono sufficienti due record per
distinguere 0, 1 e 2+ candidati; non si espongono
altri dati anagrafici o potenzialmente privati.
La risposta `LegacyXmlImportPlanDto` include il
DTO preview dell'ADR 0051 e lo stato:

- `new`: zero candidati, `existingSinistroId=null`.
- `existing`: esattamente un candidato, data
  sinistro identica e data incarico confrontata
  solo se fornita: restituisce l'UUID del candidato.
- `conflict`: candidato singolo con data non
  compatibile, nessun UUID in risposta.
- `ambiguous`: più candidati nel tenant, nessun
  UUID selezionato o esposto.

REST `POST /api/imports/xml/plan` e MCP
`plan_legacy_xml(xml)` delegano alla stessa
logica, senza side effect. Risposte
tenant-scoped REST/MCP `Cache-Control: no-store`.
Il planner non produce un audit di `succeeded`
perché non ha committato alcuna mutazione.

## Limiti e prossimo gate

Il planner è una lettura, **non un sistema di
idempotenza**: una nuova riga può apparire dopo il
piano `new`, e il match `existing` può cambiare.
Non può essere usato come autorizzazione a scrivere;
il commit import richiederà una transazione tenant
con controllo concorrenza sul riferimento,
chiave/idempotenza per attore e sorgente, audit
atomico, gestione anagrafiche/polizze e controllo
dei file allegati con ClamAV + recovery. L'unicità
richiede una decisione esplicita su anno/compagnia,
non un indice introdotto prematuramente.

Test TDD RED/Green Business e PostgreSQL coprono
ambiguità, data incompatibile, case-sensitive
exact-match, tenant RLS, pool, input malformato,
scope negato e limiti; il job full stack prova
stati REST/MCP con token OAuth reali. Le issue
#10, #7 e #12 restano aperte per i propri scope
non ancora completati.
