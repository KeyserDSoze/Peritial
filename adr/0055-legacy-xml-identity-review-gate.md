# ADR 0055 — Gate di revisione dell'identità per XML legacy ricchi

- Data: 20 novembre 2026
- Stato: implementato su `main`, test backend/CI full-stack in verifica
- Issue: #24 (identità multi-writer), #10 (import), #7 (Sinistri)
- `death` è consultato in sola lettura e non riceve modifiche

## Evidenza nel legacy: riferimento non equivale a identità

Consultazione **read-only** di:
`death:Expertiser.Database/Models/TableAccident.cs`,
`death:Expertiser.Manager/Importer/ImportManager.cs`,
`death:Expertiser.Manager/Importer/Integrations/ClassicXml.cs`
e `death:Expertiser.Manager/Importer/Integrations/AccertatoreXml.cs`.

`TableAccident` conserva `ExternalId` max 100,
`InsuranceId` FK e `YearlyId`, oltre a
`AccidentDate` e `DenunciationDate` con
precisione DateTime. Il significato e la
generazione effettiva di `YearlyId` non sono
stati ancora caratterizzati: NON assumere
che sia uguale all'anno solare.

Il vecchio `ImportManager` cerca con
`FirstOrDefaultAsync(x => x.ExternalId ==
importValue.Accident.ExternalId)` senza verifica
di assicuratore o polizza. Se il sinistro trovato
ha data più vecchia può persino creare una nuova
riga invece di riconciliare la precedente.
È un comportamento storico, **non una regola
da replicare**. Gli importer leggono
`Compagnia` e `Polizza` dai rispettivi tracciati;
Classic ottiene la compagnia dall'assicurato,
Accertatore dal nodo dedicato o dal contraente.
Le corrispondenze di nome compagnia nel vecchio
codice possono usare ricerca parziale/fuzzy:
non sono una chiave stabile.

L'attuale dominio V003 contiene soltanto
`tenant_id`, `external_reference`,
`occurred_on` e `reported_on`, senza
identità assicuratore, polizza, yearly ID,
timestamp originale o chiave d'origine verificata.
Un singolo numero e due date coincidenti
non bastano per associare in modo sicuro
un tracciato XML ricco a un Sinistro esistente.

## Decisione: presenza dei dati senza divulgarne il valore

`LegacyXmlPreviewDto` aggiunge il booleano
`HasUnmappedBusinessData`, serializzato come
`hasUnmappedBusinessData` nelle API.
È `true` se il root ha attributi, elementi
figli diversi dai soli `NumeroSinistro`,
`DataOraSinistro`, `DataIncarico`, elementi
annidati o testo diretto inatteso. Sono
inclusi quindi compagnie, polizze, anagrafiche,
danneggiati, allegati, anche se i loro
contenuti non sono restituiti. Il parser
continua a rifiutare XXE/DTD, date malformate,
namespace inattesi e input superiori a 1 MiB.
`PreviewCaseOnly` per l'import interno
continua a rifiutare i dati aggiuntivi invece
di scartarli silenziosamente.

Nel planner tenant-scoped dell'ADR 0052:
- 0 candidati esatti: `new`, nessun UUID.
  L'eventuale `HasUnmappedBusinessData=true`
  vieta comunque di trattare questo stato come
  consenso all'import.
- 2+ candidati: `ambiguous`, nessun UUID.
- Unico candidato con date discordanti:
  `conflict`, nessun UUID.
- Unico candidato con date compatibili, XML
  **ricco**: `review-required`, nessun UUID.
  L'identità estesa non è stata confrontata.
- Unico candidato con date compatibili, XML
  **minimale**: `existing`, UUID del candidato
  come suggerimento sulla sola corrispondenza
  numero/date, mai licenza di import.

Non vengono mai restituiti nomi, compagnie,
polizze, XML originale o altri dati personali
nei DTO. La decisione è comune a REST
`POST /api/imports/xml/plan` e MCP
`plan_legacy_xml(xml)`, con
`peritial.write`, RLS, `Cache-Control:no-store`.
Nessuna nuova tabella, migrazione, permesso,
write o audit `succeeded`.

## Test e prossima tranche

Test Business RED/GREEN verificano l'assenza
di UUID e dati privati quando ricchi tracciati
hanno esattamente numero e date di un
Sinistro; il semplice XML mantiene il
comportamento advisory `existing`. Playwright
con JWT Keycloak controlla parità REST/MCP e
che l'import non alteri né aggiunga Sinistri.

Resta aperta la #24: definire una fonte di
identità stabile (assicuratore e polizza
normalizzati, numero, anni e possibili duplicati),
non dedurre `YearlyId` dall'anno senza
evidenza, non riusare la ricerca fuzzy legacy.
Completare mappatura e consentire correzioni
controllate dei dati importati, cancellazione
con retention, allegati ClamAV e approvazione
prima di rendere pubblico il commit V011.
