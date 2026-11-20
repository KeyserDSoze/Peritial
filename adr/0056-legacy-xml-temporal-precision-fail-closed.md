# ADR 0056 — XML legacy: non perdere ore e metadati sorgente

- Data: 20 novembre 2026
- Stato: protezione interna e preflight implementati su `main`;
  full-stack CI da confermare
- Issue: #25, #10, #24, #7
- `death`: consultato solo in lettura, non modificato

## Contesto e rischio di perdita silenziosa

Nel porting da `death`, i modelli storici
`TableAccident.AccidentDate` e
`TableAccident.DenunciationDate` sono
`DateTime` con tipo SQL Server `datetime`.
Gli importer Classic e Accertatore accettano
`DataOraSinistro` e `DataIncarico` come
`yyyy-MM-dd HH:mm:ss` o `yyyy-MM-dd`.
Il dominio V003 Sinistro espone però solo
`DateOnly OccurredOn/ReportedOn`.

Il decoder XML già convertiva timestamp validi
a `DateOnly` per il preflight, comportamento
accettabile per mostrare una data sommaria ma
**non** per una persistenza irreversibile.
La V011 era correttamente chiusa come servizio
interno, ma `PreviewCaseOnly` permetteva
timestamp con ora, che il repository avrebbe
scartato. Anche `XmlReaderSettings` ignorava
commenti e processing instructions: eventuali
metadati sorgente sarebbero scomparsi persino
da un XML *case-only* apparentemente minimale.

## Decisione — preservare il segnale, bloccare la scrittura

Il parser mantiene la preview dei formati già
supportati senza modificare le API di trasporto:
`OccurredOn` e `ReportedOn` restano date
di calendario. Aggiungiamo al calcolo del
booleano `HasUnmappedBusinessData` due
categorie di informazione che il dominio non
può conservare:

1. Se `DataOraSinistro` o `DataIncarico`
   contengono un valore valido con ora
   (`yyyy-MM-dd HH:mm:ss`), il flag è `true`,
   **anche per `00:00:00`**, perché nel DTO
   è perso il tipo/formato della sorgente.
2. Se l'XML include commenti o processing
   instructions, anche fuori dal root, il
   lettore li conserva nell'XDocument per
   classificare la presenza; non ne emette
   mai contenuto, posizioni, quantità o
   markup nelle risposte, errori e log.

DTD/XXE restano proibite, `XmlResolver=null`,
budget 1 MiB e whitelist di root invariati.
La XML declaration resta ammessa: non è una
processing instruction del documento LINQ
e serve a indicare il normale encoding.

Il planner dell'ADR 0055 usa come prima
`new` senza match, `ambiguous` con più
match, `conflict` con date discordanti;
con un unico candidato a date uguali e
`HasUnmappedBusinessData=true` risponde
`review-required`, senza UUID. Un match
minimal date-only rimane advisory
`existing`, **non una licenza d'import**.

Il servizio `LegacyXmlCaseImportService`
(non DI/non REST/non MCP) passa sempre da
`PreviewCaseOnly`, che rifiuta timestamp,
commenti, PI e gli altri campi non mappati
**prima** di toccare `ILegacyXmlCaseImportStore`.
Non viene creato alcun Sinistro, ledger né
audit `succeeded` per queste richieste.
La V011 e la V012 non cambiano, e non servono
nuovi grant o nuove migrazioni per il gate.

## Perché non salvare automaticamente un timestamp UTC

Le sorgenti potrebbero rappresentare tempi
locali senza offset. `DateTime` storico
non prova una timezone e `YearlyId` non
è ancora caratterizzato. Aggiungere
`DateTimeOffset`, convertirlo d'ufficio
a UTC o trarre l'anno solare dal giorno
creerebbe dati inventati o non reversibili.
La #25 traccia la caratterizzazione su
campioni anonimizzati, il nuovo schema
additivo, i test DST/precisione, REST/MCP,
RLS e backup/restore.

## Evidenze e limiti

TDD Business verifica date/ore ordinarie
e a mezzanotte, `DataIncarico` con ora,
assenza di UUID/dettagli non mappati,
rifiuto del commit prima del repo,
commenti/PI e trattamento di XML date-only.
Test Playwright Keycloak verificano REST/MCP
con preview/plan e JWT `peritial.write`.
Non si importa ancora alcuna compagnia,
polizza, anagrafica, Perizia o allegato.
L'issue #25 è **aperta** fino alla vera
persistenza senza perdita, e #10/#24 non
sono completate.

## Estensione con ADR 0057 — perdita lessicale dei campi base

La distinzione fra dato letto per preview e dato
fedelemente importabile riguarda anche spazi
normalizzati e presenza del nodo opzionale vuoto:
`DataIncarico` vuoto non è automaticamente
equivalente ad assente. Il segnale booleano
viene riutilizzato senza aggiungere contenuti
dell'XML al DTO; il commit V011 li rifiuta.
Vedi [ADR 0057](0057-xml-source-scalar-normalization-review.md).
