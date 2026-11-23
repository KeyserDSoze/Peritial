# ADR 0099 — CR/CRLF e caratteri di controllo nei riferimenti XML

- Data: 23 novembre 2026
- Stato: implementato su `main`; CI finale da attestare
- Issue: #24 (P0), #25, #10, #7
- Branch `death`: solo archivio storico, mai modificato

## Problema

XML 1.0 esegue la normalizzazione dei fine-riga nei
testi durante il parsing: CR e CRLF sorgenti vengono
esposti al codice come LF. L'implementazione
`LegacyXmlPreviewService` controllava soltanto
`e.Value != e.Value.Trim()`, entità/CDATA, attributi,
date con ora e XML ricchi. Un `NumeroSinistro`
contenente CR, CRLF, TAB o LF **internamente**
non cambiava per `Trim()`, non produceva il flag
`HasUnmappedBusinessData` e poteva ottenere
`existing` con l'UUID di una pratica sul
riferimento decodificato (non sulla forma sorgente).
Il commit case-only interno poteva persistere un
riferimento con line ending già riscritto.

## Decisione

1. Su ciascuno dei tre scalari XML approvati
   (`NumeroSinistro`, `DataOraSinistro`,
   `DataIncarico`), se il valore decodificato
   contiene un carattere `char.IsControl`,
   impostare `HasUnmappedBusinessData=true`.
   Include i CR/CRLF normalizzati in LF e i
   TAB/LF letterali; non inferire che un
   rifermento già presente sia identità originale.
2. La preview resta consultiva, senza raw bytes o
   markup aggiuntivi; il planner `LegacyXmlImportPlanService`
   già tratta il flag come `review-required`
   per un candidato compatibile e, da ADR 0098,
   anche per zero candidati. Nessun UUID
   candidato viene restituito nei due casi.
3. `PreviewCaseOnly` rifiuta l'input e
   `LegacyXmlCaseImportService` non chiama lo store;
   il controllo dello store introdotto con ADR 0096
   resta una seconda linea di difesa.
4. Il controllo è limitato ai contenuti scalari
   previsti. L'indentazione XML *fra i tag* non
   impone review. Restano invariati JSON/REST/MCP,
   RLS, audit, PostgreSQL V011/V012, scope JWT e
   codice storico.

## Prove

- Test-first RED:
  [5d909ad](https://github.com/KeyserDSoze/Peritial/commit/5d909ad065152ad91aed0fd445da6849f06d3cbc),
  [run #45166215453](https://github.com/KeyserDSoze/Peritial/actions/runs/45166215453).
  **12 test Business nuovi falliti come previsto** (4 parser,
  4 planner con candidato esatto, 4 commit case-only).
  150 Business esistenti e 201 Infrastructure superati.
- Fix [b8de578](https://github.com/KeyserDSoze/Peritial/commit/b8de578f6233e08c2c193fae5aac3b3ff8fcdef5)
  sul parser unico, prima della lettura dei tre scalari,
  riutilizzando il comportamento fail-closed del piano e
  del commit già implementato.
- E2E
  [c0ba83c](https://github.com/KeyserDSoze/Peritial/commit/c0ba83c4751cce545833f706255489ac0639df41):
  JWT Keycloak, Sinistro realmente persistito con LF nel
  riferimento, sorgente XML contenente CR, REST e MCP devono
  dare `review-required`, `existingSinistroId=null`;
  il Sinistro resta immutato.

## Rischi residui

Questa guardia non sostituisce il modello completo di
identity resolution (compagnia, polizza, yearly upload ID,
fonte originale) né la preservazione delle date e ore
del legacy. Un valore di preview decodificato non è
una chiave di import. Sono necessarie prove su dati
anonimizzati, con tenant distinti, policy di retention
e concurrency multi-replica. L'import XML completo resta
interno senza route REST/MCP e senza DI; nessuna
cancellazione o UNIQUE generalizzata sui Sinistri.
