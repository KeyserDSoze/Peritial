# ADR 0050 — V010: indici PostgreSQL per seek del catalogo documenti

- Data: 19 novembre 2026
- Stato: implementato su `main`; CI finale e performance staging da attestare
- Issue: #11, #6
- `death`: riferimento storico intatto

## Problema

L'ADR 0049 ha introdotto `GET /api/files/page`,
`page_documents` e la PWA con `nextCursor`.
Lo schema V001 offre PK `(tenant_id,id)` e
`ix_documents_perizia(tenant_id,perizia_id)`;
nessuna copre direttamente il keyset ordinato
`created_at_utc DESC,id DESC`, globale e filtrato
per Perizia. Per gli archivi crescenti questo può
causare sort o scan di più righe del necessario.
Non si dispone di statistiche di produzione: la scelta
è guidata dalla forma dei predicati, non da una
promessa di p95.

## Decisione

La V010 aggiunge, senza DML o modifiche V001–V009:

```sql
CREATE INDEX ix_documents_tenant_created_id
    ON documents (tenant_id, created_at_utc DESC, id DESC);
CREATE INDEX ix_documents_tenant_perizia_created_id
    ON documents (tenant_id, perizia_id, created_at_utc DESC, id DESC);
```

L'indice preesistente `ix_documents_perizia` non viene
rimosso per evitare regressioni dei caller legacy.
`PgDocumentCatalogRepository.PageAsync` genera due
forme SQL: se manca il cursore nessun filtro seek,
se esiste `AND (created_at_utc,id) < (@afterTime,@afterId)`.
Le colonne di ordinamento sono entrambe DESC;
il confronto tuple coincide quindi con la pagina successiva.
Il tenant rimane selezionato tramite JWT firmato nel
servizio, `TenantDbScope` e PostgreSQL FORCE RLS;
ogni pagina conserva il limite max 50 (+1 per hasMore).
Non cambia la sicurezza né l'immutabilità metadata
stabilita dalla V009.

## Rilascio e limiti

Il migrator DDL esegue le due `CREATE INDEX`
nella stessa transazione in cui registra journal/
checksum V010, sotto advisory lock. Non usa
`CREATE INDEX CONCURRENTLY`: può bloccare
writer e richiedere spazio disco / lock significativi
sulle tabelle popolate. Per produzione: inventario
dimensioni, finestra con writer fermati, backup
DB+blob verificato, test restore e piano di ritorno
prima dell'applicazione amministrativa. Non usare
SQL diretto con `peritial_app`; la CI Compose
effimera è una fixture, non un percorso di upgrade.
I nomi degli indici non usano `IF NOT EXISTS`
per fallire su drift anziché certificare un indice
dalla definizione errata.

Test TDD: prima V010 il test attende 10 versioni
e due indici B-tree validi e prova `EXPLAIN`
con `enable_seqscan/bitmapscan/sort=off`;
la CI RED deve fallire. Dopo V010 il piano
controllato deve contenere l'indice pertinente
senza `Sort`, e test tenant-scoped già esistenti
continuano a verificare pagine/cursori.
Il test del piano non sostituisce
`EXPLAIN ANALYZE BUFFERS` su dati realistici:
staging deve misurare throughput di upload,
bloat, index size, latenza p95/p99,
performance del filtro e del listing globale.
Nessun criterio di chiusura globale di #11/#6
è soddisfatto dal solo incremento.
