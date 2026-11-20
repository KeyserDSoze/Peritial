# ADR 0053 — V011: idempotenza atomica XML solo Sinistro, non esposta

- Data: 20 novembre 2026
- Stato: implementata come infrastruttura **interna su main**;
  verifica CI da completare
- Issue: #10, #6, #21
- `death`: intatto, solo archivio storico

## Contesto e limiti del modello storico

L'ADR 0052 espone un piano in sola lettura che individua
un Sinistro univoco o un riferimento ambiguo all'interno
di un tenant. La V003 **non impone unicità**
di `external_reference`: il significato del numero
dipende potenzialmente da compagnia e annualità, dati
ancora da migrare. Un piano `new` non è un lock né
un consenso al commit: fra lettura e inserimento,
un altro client può creare lo stesso riferimento.

Non è sicuro riusare la vecchia pipeline
`ImportManager`: conteneva scritture multiple e
allegati non protetti da un'unica transazione.

## Decisione V011 (solo port e adapter interno)

Aggiungiamo `LegacyXmlCaseImportService`,
`ILegacyXmlCaseImportStore` e
`PgLegacyXmlCaseImportStore`, ma **nessuna**
registrazione DI, route REST o tool MCP.
Il parser con `PreviewCaseOnly` rifiuta
tutti i dati oltre a `NumeroSinistro`,
`DataOraSinistro`, `DataIncarico`.
L'input ha limite di 1 MiB, DTD/XXE proibiti,
OAuth `peritial.write` obbligatorio;
qualsiasi anagrafica, compagnia, danneggiato,
polizza, allegato o attributo porta a rifiuto
prima del DB. Non esiste una conversione
parziale silenziosa di tracciati ricchi.

Il client indica una chiave idempotente ASCII
di 16–128 caratteri. Business calcola SHA-256
degli **esatti byte XML**; PostgreSQL vede
solo digest attore, chiave e request,
id Sinistro e timestamp. Il ledger
`legacy_xml_import_keys` presenta PK
`(tenant_id,subject_digest,key_digest)`,
vincolo unico su `(tenant_id,sinistro_id)` e
FK verso Sinistro
`DEFERRABLE INITIALLY DEFERRED`.
Il ruolo app ha SELECT/INSERT sotto
FORCE RLS e nessun UPDATE/DELETE; nessun
privilegio per PUBLIC/recovery. La FK
protegge la prova storica anche in presenza
del gate di delete Dev.

L'adapter apre una singola transazione RLS
tenant e prende un advisory lock
`pg_advisory_xact_lock` calcolato con hash
tenant+riferimento. Primo inserisce la prova
in DB con unique arbitration e
`ON CONFLICT DO NOTHING`.
Una chiave già confermata con stesso digest
è una replay read-only, altrimenti conflitto.
Con una chiave nuova verifica una
corrispondenza esatta nel tenant e nega
l'import se esiste anche una sola riga.
Se assente, inserisce `sinistri` e
`security_audit` action
`sinistro.import-xml`, outcome
`succeeded`, stesso resource ID, prima
di un singolo COMMIT. Un errore di audit
annulla ledger+Sinistro. Non usa un
secondo sistema di transazioni o un audit
`succeeded` anticipato.

## Concorrenza e blocco dell'esposizione pubblica

Il lock serializza gli **importatori V011**,
anche con chiavi diverse. Gli attuali
endpoint `POST /api/sinistri` e MCP
`create_sinistro` non condividono questo
lock, e il database non possiede una
unique constraint sul riferimento.
Pertanto un writer normale concorrente
può ancora creare un duplicato:
**l'import V011 rimane volutamente
non disponibile dall'API**.
Il successivo gate deve proteggere
*ogni* writer e caratterizzare
l'identità composita per anno/compagnia
prima di un uso pubblico. Non introdurre
una `UNIQUE (tenant_id,external_reference)`
senza una migrazione approvata e verifica
dei dati storici.

## Test, recovery e lavoro aperto

Test Business per allowlist, DTD,
budget, chiavi invalide, scope e
digest esatto; PostgreSQL isolato
per replay, key reuse, duplicati,
cross-tenant, confronto concorrenziale,
FORCE RLS, privilegi minimi e rollback
forzando un errore nell'audit.
Migrator versionato V011 e verifiche
nelle fixture CI, adozione legacy
e restore. Nessun dato passato viene
retroattivamente etichettato come
import XML; nessun cambio V001–V010.

Restano aperti import vero, riconciliazione
compagnia/anno, anagrafiche, polizze,
Perizie, report, allegati ClamAV/WORM,
retention approvata, staging reale e
concorrenza globale di tutti i writer.

## Aggiornamento con V012 (ADR 0054)

V012 rende condiviso il lock SQL anche con i normali
`INSERT/UPDATE` di Sinistri e nega mutazioni sui
Sinistri importati o la duplicazione del relativo
riferimento da altri writer. La V011 resta
interna: non abilita import completo, correzioni
di origine, anagrafiche o gestione allegati.
La discussione originale sui lock solo fra importatori
descrive lo stato **prima** della V012.
