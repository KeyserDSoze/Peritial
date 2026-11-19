# ADR 0049 — Catalogo documenti con paginazione keyset

- Data: 19 novembre 2026
- Stato: implementazione su `main`, CI finale da attestare
- Issue: #11, #12, #13
- `death` storico, non modificato

## Contesto e contratto

Il vecchio `GET /api/files`/`list_documents` limita il
risultato a 100 e la PWA ne chiedeva soltanto 50.
Per mantenere compatibilità, vengono aggiunti
`GET /api/files/page?limit=50&periziaId=<uuid>&cursor=<opaque>`
e `page_documents(periziaId?,limit=50,cursor?)`, entrambi
tramite `DocumentCatalogService`. Il DTO
`{items:[DocumentSummaryDto],nextCursor:string|null}`
offre 1–50 elementi per pagina; i contratti precedenti
restano invariati. Le liste non contengono tenantId,
storage_key, URI interni, byte o risultati antivirus.

La query SQL usa `tenant_id=@tenant` e opzionalmente
`perizia_id=@perizia`, protetta da PostgreSQL FORCE RLS.
Il seek `created_at_utc < @time OR
(created_at_utc=@time AND id < @id)` è applicato prima
di `ORDER BY created_at_utc DESC,id DESC` e di
`LIMIT (richiesti+1)`. Nessun OFFSET. La riga extra
serve a decidere se restituire `nextCursor`.

## Sicurezza e concorrenza

Il cursore versione `d1` serializza in Base64URL
canonico i tick UTC del timestamp e l'UUID della
posizione; un fingerprint SHA-256 sul `periziaId`
normalizzato (o `-` per il filtro globale) lega il
cursore alla selezione. Sono verificati lunghezza,
caratteri, forma canonica, data, ID e fingerprint
prima di interrogare SQL. Errori sono HTTP 400
o errore MCP. Il fingerprint **non è un HMAC**:
il cursore non è una credenziale, non fornisce
integrità crittografica e non sostituisce OAuth
e FORCE RLS. Pagina per pagina la query applica
sempre i privilegi del tenant. Nuovi inserimenti
più recenti non spostano il boundary già letto,
ma non è garantito uno snapshot immutabile fra
richieste indipendenti.

## UI e prove

La PWA usa la prima pagina dopo autenticazione,
carica altre voci solo su click e deduplica ID.
Cambio filtro/JWT, refresh, offline o indisponibilità
invalidano dati e cursore. Una continuazione
non valida cancella la lista e richiede nuovo GET
autenticato, senza cache offline o retry mutativi.
Etichette it/en e presentazione solo metadati.

Test Business: ordinamento con timestamp uguali,
cursor noncanonico/cross-filter, cap e OAuth prima
del repository. Test PostgreSQL: perizie di due
tenant anche con medesimo UUID, seek su data e
ID, assenza di leak dal pool. Playwright:
REST/MCP con OAuth Keycloak e due upload reali
per pagina da un elemento; separata prova UI con
risposte pagina simulate per il pulsante, senza
scambiarla per test backend.

Non viene introdotta alcuna migrazione database;
restano in #11/#13 indici/benchmark per cataloghi
grandi, ricerca, versioni, report, download
autorizzato, accessibilità e staging reale.
Non cambia l'hardening V009 sui privilegi metadata.
