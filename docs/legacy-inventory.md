# Inventario funzionale dal branch death

Esame statico dei progetti Expertiser.* sul commit death `35b043e6f6392bde3e45faa9d326a842914771d5`. Da riesaminare con test di caratterizzazione e casi reali prima della parità funzionale.

| Epic | Funzioni osservate | Stato nuovo |
|---|---|---|
| Sinistri | CRUD, ricerca, autocomplete, import | aggregate + PG V003, REST/MCP list/search/get/create/update; V004 collega perizie al sinistro con FK same-tenant, CAS e audit, esposto REST/MCP. PWA: selettore per associare una perizia con lettura persistita; ancora senza DELETE, ricerca avanzata/cursor, reparent/unlink, CRUD completo Sinistri nella UI, anagrafiche o import (#7/#9/#13) |
| Perizie | CRUD, assegnazione, filtro utente, workflow | primo vertical slice in corso |
| Stati | Registered(-1), Assigned(0), ToBeRevised(1), Revised(2), ToSend(3), Sent(4), Billed(5), Duplicated(6), Cancelled(7) | regole nuove da validare |
| Import | XML Classic/Accertatore, auto-detection, anagrafiche e allegati | da implementare |
| Documenti | folder AccidentDocs/Photos/RawExpertise/CorrectedExpertise/CompletedExpertise/ReservedNote, upload, delete, zip, download | ingestion primo vertical slice |
| Report | PDF/Word fotografici, generazione da template | da implementare |
| Anagrafiche | clienti, assicurazioni, compagnie, agenzie, ispettorati, liquidatori, località, utenti | da implementare |
| Comunicazioni | messaggi e notifiche email | da implementare |
| Contabilità | fatture/parcelle, importi, estratti | da implementare |
| Analytics | KPI, CSV assegnazioni/chiusura/liquidato | da implementare |
| Sicurezza | Admin, Contributor, Reader | modello nuovo basato su OAuth scope/role e tenant |

Nota: il legacy contiene vari `NotImplementedException`; la loro presenza non costituisce un requisito di compatibilità.
