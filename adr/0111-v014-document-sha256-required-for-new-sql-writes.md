# ADR 0111 — V014: SHA-256 obbligatorio per ogni nuovo documento SQL

- Data: 23 novembre 2026
- Stato: implementato su `main`, validazione full-stack sull'ultimo HEAD in corso
- Issue: #27 (P0), #6 (migrator/restore), #21 (atomicità/audit)
- `death` resta un branch storico non modificato

## Contesto

Con [ADR 0110](0110-document-sha256-admission-and-verified-download.md)
`PgDocumentRepository.AddAuditedAsync` rifiuta ogni metadata record
senza `ContentSha256` lungo 32 byte. La migrazione V013, però,
permette la colonna `NULL` per i record storici, quindi un writer
SQL diretto con i permessi INSERT di `peritial_app` potrebbe ancora
creare un nuovo documento senza prova di ammissione. La lettura
download è fail-closed, ma il database potrebbe contenere nuovi
record inutilizzabili.

## Decisione

Aggiungere una nuova migrazione **forward-only V014**:
`V014__documents_require_sha256_new_writes.sql`.

```sql
ALTER TABLE documents
    ADD CONSTRAINT ck_documents_sha256_required_new
    CHECK (content_sha256 IS NOT NULL) NOT VALID;
```

`NOT VALID` è intenzionale e non equivale a una eccezione per le
nuove scritture: PostgreSQL verifica il CHECK per ogni futuro
`INSERT` e `UPDATE`, ma **non scansiona né modifica** le righe
V001–V013 preesistenti con `content_sha256=NULL`. Il vincolo
V013 di dimensione 32 byte continua a impedire hash malformati,
mentre il trigger V013 impedisce la modifica di digest già inseriti.

Questa sequenza di vincoli preserva il journal checksum di V001–V013
senza editare migrazioni storiche. `SchemaMigrator` registra V014,
lo applica in un unico commit sotto lock DDL; le fixture xUnit
applicano V013 + V014 solo nello schema di test, mentre la fixture
storica di adozione V001 resta esattamente V001.

Non concedere UPDATE/DELETE aggiuntivi a `peritial_app`, non
modificare FORCE RLS, non introdurre valori predefiniti o checksum
fittizi nel database e non applicare una validazione globale
(`VALIDATE CONSTRAINT`) prima di aver gestito i documenti legacy.

## Semantica e rollout

- Nuovi upload REST/MCP: digest SHA-256 dei byte già ammessi da
  ClamAV prima del PUT, scritti nella stessa transazione di audit,
  metadata e idempotency; ora anche i writer SQL alternativi
  non possono omettere il digest.
- Vecchi documenti: rimangono fisicamente disponibili nel database,
  con digest `NULL`, ma il loro download resta **bloccato** fino
  a un backfill con revisione e nuova scansione approvata. V014
  blocca anche UPDATE di una vecchia riga che resta `NULL`; una
  procedura futura dovrà definire esplicitamente una via sicura di
  bonifica per queste righe e l'immutabilità V013.
- Restore/upgrade: amministrare backup verificato, fermare i writer
  nel deployment, utilizzare il migrator privilegiato fuori dall'API.
  `NOT VALID` è fondamentale anche per ripristinare dump contenenti
  NULL storici. La CI Compose applica V014 esplicitamente solo nel
  PostgreSQL usa-e-getta e non altera database reali.

## Verifiche

- Test TDD `V014_new_sql_inserts_require_digest_but_preexisting_legacy_null_remains_readable`:
  journal idempotente, rewinding *solo del database isolato di test*
  a V013 con documento `NULL`, upgrade di una versione e prova
  del vincolo `convalidated=false`; successivi raw INSERT senza
  digest negati con SQLSTATE `23514`, hash a 32 byte ammessi,
  documento storico invariato.
- Test `V014_denies_legacy_sql_insert_without_sha_and_preserves_upload_intent_for_valid_retry`:
  vero `peritial_app` soggetto a FORCE RLS, protezione SQL
  anche quando bypassa `AddAuditedAsync`, nessuna perdita dell'intent,
  nessun documento/audit succeeded su fallimento e successivo commit
  con digest corretto.
- Controlli di restore PostgreSQL, legacy V001 e pre-lease aggiornati
  a 14 migrazioni; prove di recupero mantengono checksum valido o
  `NULL` di origine legacy. Fixture di isolamento multi-tenant e
  snapshot recovery portate al contratto SHA.
- CI backend/Compose dell'ultimo HEAD da attestare dopo la revisione,
  evitando di chiamare verdi le run RED o quelle intermedie.

## Limiti ancora aperti (#27)

Un digest persistito nello stesso PostgreSQL non protegge contro
compromissioni dell'intero database o del ruolo di amministratore
e non autentica la provenienza delle firme ClamAV. Restano necessari
la versione approvata del backfill storico, integrità e retention
storage reali su SeaweedFS/Azurite/Azure, fault injection PUT/commit
ACK, audit del download prima della consegna HTTP e gate
operativi Azure/on-prem. #27 e #6 **non si chiudono** con V014.
