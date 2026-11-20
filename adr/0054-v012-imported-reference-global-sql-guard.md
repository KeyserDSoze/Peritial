# ADR 0054 — V012: trigger SQL per riferimenti Sinistri importati (#24)

- Data: 20 novembre 2026
- Stato: implementato su `main`, gate di verifica CI ancora da attestare
- Correlate: #24, #10, #7, #6, #21, ADR 0052 e 0053
- `death` è un riferimento storico non modificato

## Problema e confine della decisione

In V003 `sinistri.external_reference` non è unique:
le chiavi possono dipendere da assicuratore e anno,
e il legacy può includere duplicati legittimi.
V011 serializzava solo le richieste del proprio
`PgLegacyXmlCaseImportStore` attraverso un hash
SHA-256 a 64 bit calcolato in .NET. Gli altri writer
REST/MCP e persino SQL diretto con `peritial_app`
potevano concorrere senza lo stesso mutex,
duplicando o modificando riferimenti importati.
Una UNIQUE globale avrebbe potuto violare dati
storici reali non ancora caratterizzati.

## Decisione V012

Una nuova migrazione PostgreSQL installa la funzione
`guard_imported_sinistro_reference()` con
privilegi **dell'invocante**, non SECURITY DEFINER.
Il trigger BEFORE INSERT/UPDATE protegge **qualsiasi
writer di `sinistri`**, a prescindere dall'endpoint:
1. Acquisisce un advisory transaction lock calcolato
   con `hashtextextended(tenant_id::text || chr(10) ||
   external_reference, 0)`. L'adapter XML V011 usa
   la medesima espressione in SQL, senza replicare
   l'hash in .NET. Il lock è temporaneo; eventuali
   collisioni di hash serializzano operazioni
   indipendenti senza corrompere dati.
2. Un UPDATE su un Sinistro che possiede una prova
   nel ledger `legacy_xml_import_keys` fallisce:
   quel record è immutabile fino alla definizione
   di una correzione con provenienza e revisione.
3. Un INSERT/UPDATE su un riferimento già assegnato
   a un Sinistro importato **differente** è negato
   con SQLSTATE 23505, constraint semantico
   `ck_imported_sinistro_reference_guard`, che il
   repository applicativo converte a
   `LegacyXmlReferenceConflictException`
   (REST 409). Nessun UUID del caso concorrente
   è divulgato al richiedente.
4. Un INSERT che crea per la prima volta un import
   V011 è possibile: il ledger con FK differibile
   viene inserito prima del Sinistro e il controllo
   non trova ancora un Sinistro importato diverso.
   Audit+ledger+Sinistro devono sempre committare
   insieme, come stabilito dall'ADR 0053.
5. I duplicati ordinari **senza prova d'import**
   restano ammessi. Questo non certifica che il
   riferimento sia una vera identità univoca per
   compagnia/anno; pertanto la #24 resta aperta.

Il vincolo vive nel database: anche una INSERT
SQL diretta con il ruolo `peritial_app` attiva il
trigger. RLS `ENABLE`+`FORCE` resta in vigore,
la funzione usa i privilegi dell'invocante e
accede soltanto ai dati del tenant di sessione.
Un account DDL superuser capace di rimuovere
oggetti resta fuori dal trust boundary applicativo.

## Migrazione, rollback e sicurezza operativa

V012 non modifica le V001–V011 né i dati esistenti.
Nel medesimo commit DDL prende un lock di tabella e
verifica collisioni **già presenti** fra riferimenti
di Sinistri importati e altre righe del tenant.
Se il controllo trova un conflitto, abortisce
l'upgrade, senza inserire la versione nel journal
né riscrivere/etichettare retroattivamente dati
ambigui. Il ripristino deve seguire il runbook di
backup/restore; non rimuovere il trigger in
produzione per continuare un upgrade fallito.

La fixture Compose esegue V012 con
`psql --single-transaction -f /dev/stdin`;
`LOCK TABLE` non funziona con STDIN in autocommit.
Il migrator .NET usa già un'unica transazione
versionata con SHA-256, advisory DDL lock e journal
atomico. In uno schema popolato sono obbligatori
backup verificato, sospensione dei writer e
finestra di manutenzione. L'uso del trigger su
ogni INSERT/UPDATE ha un costo addizionale e
richiede misure di contesa su staging.

## Test, residui e non-obiettivi

Test PostgreSQL reale: repository ordinario e
import interno in due pool autentici, RLS tenant
distinti con stesso riferimento, inserimento diretto
app-role, UPDATE su record importato e retargeting,
replay idempotente e rollbacks senza audit successo.
Una prova di migrazione in schema isolato semina
una collisione pre-V012 e impone rollback senza
nuovo journal. I percorsi legacy che non hanno
provenienza XML mantengono i duplicati esistenti.

**NON** abilita REST/MCP per il commit d'import
XML né un nuovo vincolo UNIQUE su tutti i Sinistri.
Rimangono da studiare i tracciati insurer/year e le
anagrafiche; approvare la strategia di source ID,
gestire conflitti preesistenti, correzioni
non distruttive di Sinistri importati, allegati
con scansione/recovery, throughput/lock wait/deadlock
sotto carico, staging Azure/on-prem e retry
di mutazioni ordinarie. La #24 rimane aperta.
