# ADR 0098 — XML ricco senza candidato: review-required invece di new

- Data: 23 novembre 2026
- Stato: implementato su `main`; CI finale da attestare
- Issue: #24 (P0), #10, #25, #7
- `death`: ramo storico, non modificato

## Problema

`LegacyXmlImportPlanService` esegue una ricerca esatta con massimo
due risultati sotto tenant RLS. Con **zero** risultati restituiva
sempre `status=new`, anche se la preview aveva
`hasUnmappedBusinessData=true`. Il risultato descriveva l'assenza
di un riferimento, non una possibilità sicura di creare una pratica.
I client REST/MCP potevano però interpretarlo come approvazione,
mentre compagnie, polizze, anagrafiche, timestamp o allegati
sarebbero stati scartati (il commit interno già li rifiuta).

## Decisione

| Candidati per riferimento esatto | Fonte XML | Esito |
| --- | --- | --- |
| 0 | minimale/canonica | `new` (advisory, non permesso d'import) |
| 0 | dati non mappabili | **`review-required`**, UUID null |
| 1, date compatibili | minimale/canonica | `existing`, UUID advisory |
| 1, date compatibili | dati non mappabili | `review-required`, UUID null |
| 1, date discordanti | qualunque | `conflict`, UUID null |
| 2 o più | qualunque | `ambiguous`, UUID null |

Cambia soltanto il servizio Business condiviso tra REST
`POST /api/imports/xml/plan` e MCP `plan_legacy_xml(xml)`.
Non servono nuovi endpoint, permessi, schemi wire o migrazioni.
La ricerca resta esatta, tenant-scoped e LIMIT 2, senza fuzzy
matching, mutation o audit succeeded. Il booleano proviene
dal parser di preflight sicuro; nessun dato business privato
o UUID di candidati viene restituito quando è necessaria
revisione.

## Prove e contratti

- Test Business: polizza, allegati, commento, attributo e data
  con componente oraria, **zero candidati** → `review-required`
  senza UUID/PII. XML minimale senza candidato rimane `new`.
- Test Keycloak E2E: XML ricco senza match →
  `review-required` in REST e MCP, senza persona/UUID;
  stesso XML minimale → `new`. Dopo creazione ordinaria
  sulla stessa reference, rich → `review-required`,
  minimal → `existing`.
- Test wire-schema MCP per entrambi i formati Classic e
  Accertatore aggiornati; `HasUnmappedBusinessData` resta
  booleano, nessun nuovo campo o status pubblico.

## Gate residui

Un planner non certifica identità, unicità futura né
idempotenza in presenza di race. La chiave composita
storica, la concorrenza multi-writer, i timestamp originali,
i documenti e gli allegati con antivirus restano bloccanti
(#24, #25, #10 e #5). Il commit XML V011 rimane **interno**
e non registrato in DI; nessuna modifica a `death` o
alle migrazioni preesistenti.
