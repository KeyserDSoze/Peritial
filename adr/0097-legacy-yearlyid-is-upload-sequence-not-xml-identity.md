# ADR 0097 — Caratterizzazione statica di YearlyId: progressivo di caricamento, non identità XML

- Data: 23 novembre 2026
- Stato: caratterizzazione **statica** verificata sul commit storico; dati reali e decisione di identità ancora da validare
- Issue: #24 (P0), #10, #7, #25
- Ramo `death`: sola lettura, **mai modificato**
- Evidenza vincolata al commit storico `35b043e6f6392bde3e45faa9d326a842914771d5`

## Risultati verificabili dal codice storico

1. [`TableAccident`](https://github.com/KeyserDSoze/Peritial/blob/35b043e6f6392bde3e45faa9d326a842914771d5/Expertiser.Database/Models/TableAccident.cs) ha `Id` numerico come primary key, `ExternalId` stringa obbligatoria, `AccidentDate` e `DenunciationDate` come SQL Server `datetime`, `UploadDate` e `YearlyId` intero. `YearlyId` non è la colonna dell'identificativo Sinistro trasmesso dalla fonte XML.
2. [`QueryManager.InsertAccidentAsync`](https://github.com/KeyserDSoze/Peritial/blob/35b043e6f6392bde3e45faa9d326a842914771d5/Expertiser.Manager/QueryManager.cs) assegna `YearlyId=1` o `previousAccident.YearlyId+1`, leggendo `TableAccident` con `UploadDate >= 1 gennaio dell'anno di UploadDate` e ordinamento decrescente per `YearlyId`. Non usa `AccidentDate.Year`, `ExternalId`, compagnia o polizza per calcolarlo. **Attenzione:** il filtro del codice non possiede un limite superiore al 1 gennaio dell'anno successivo; non equivale formalmente a una partizione esatta per anno nei dati fuori ordine.
3. Lo stesso metodo usa un [`TrafficLight` statico](https://github.com/KeyserDSoze/Peritial/blob/35b043e6f6392bde3e45faa9d326a842914771d5/Expertiser.Manager/QueryManager.cs) nel processo applicativo. Il lock in memoria non dà garanzia distribuita fra istanze, processi, job o importatori. Dal codice visto **non si può dedurre** un vincolo SQL di unicità dell'accoppiata anno/progressivo.
4. [`ImportManager.FinalizeAsync`](https://github.com/KeyserDSoze/Peritial/blob/35b043e6f6392bde3e45faa9d326a842914771d5/Expertiser.Manager/Importer/ImportManager.cs) cerca il Sinistro con `FirstOrDefaultAsync(x => x.ExternalId == importValue.Accident.ExternalId)` **senza** compagnia/polizza/data. Se non trovato lo crea; se la data in ingresso è maggiore di quella del primo match, crea un secondo `TableAccident` con lo stesso `ExternalId`. Non è un algoritmo canonico di deduplicazione e può produrre duplicati storici deliberati o accidentali; non bisogna trasferirlo a `main` come invariante.
5. [`TableExpertise.ConcreteId`](https://github.com/KeyserDSoze/Peritial/blob/35b043e6f6392bde3e45faa9d326a842914771d5/Expertiser.Database/Partials/TableExpertise.cs) concatena `YearlyId`, alias compagnia/agenzia, anno di `UploadDate`, membro e contributor. È un identificatore **di presentazione costruito lato codice**, non una prova di chiave univoca del file XML né una chiave per collegare pratiche in PostgreSQL.
6. Entrambi gli importatori [Classic](https://github.com/KeyserDSoze/Peritial/blob/35b043e6f6392bde3e45faa9d326a842914771d5/Expertiser.Manager/Importer/Integrations/ClassicXml.cs) e [Accertatore](https://github.com/KeyserDSoze/Peritial/blob/35b043e6f6392bde3e45faa9d326a842914771d5/Expertiser.Manager/Importer/Integrations/AccertatoreXml.cs) mappano `NumeroSinistro` a `ExternalId`. Usano anche risoluzioni di compagnia basate su stringhe parziali/fuzzy: non replicarle come identity matching senza evidenza e regole approvate.

## Decisione per il nuovo porting

- **Non usare** `YearlyId`, `YearlyId + anno`, `ConcreteId`, `NumeroSinistro` da solo o `AccidentDate` come chiave globale di deduplica per il nuovo import.
- Conservare il guard SQL V012 solo sui riferimenti **già dichiarati importati**, senza introdurre UNIQUE indiscriminata su `sinistri.external_reference`. Il commit V011 rimane interno e bloccato.
- Non introdurre ancora `YearlyId` nel modello PostgreSQL né convertirlo in un anno di evento. Prima serve un mapping di provenienza e una policy verificata per identificatori interni, fonte esterna, compagnia, polizza e origine.
- Conservare la precisione originale dei timestamp nel futuro modello secondo #25; non usare conversioni DateOnly come scorciatoia di identità.

## Dati e decisioni ancora mancanti

Questa lettura statica **non equivale** a caratterizzazione di dati storici reali. Occorrono campioni anonimizzati e approvazioni owner per:
- duplicati di `ExternalId` con stessa/differente compagnia, polizza, annualità di upload ed evento; versioni/timestamps diversi;
- collisioni reali di `YearlyId` per anno, date di caricamento fuori ordine, più istanze e migrazioni SQL Server;
- effettiva unicità dei vincoli SQL nel database distribuito ai clienti, non dedotta da model o script potenzialmente storici;
- stabilità degli identificatori esposti in PDF, report e comunicazioni; eventuali correzioni manuali;
- scelta e test di una chiave composita senza fuzzy matching o selezione arbitraria di UUID, legal hold e retention.

La checklist P0 della #24 rimane aperta: nessuna autorizzazione alla pubblicazione dell'import XML. `main` è l'unico ramo di sviluppo; `death` rimane intatto.
