# ADR 0096 — Validare dati non mappati e fatti del replay al confine di persistenza XML

- Data: 23 novembre 2026
- Stato: implementato su `main`; CI HEAD da attestare
- Issue: #24 (P0), #10, #21, #25
- Ramo legacy `death`: sola lettura, non modificato

## Problema

`LegacyXmlCaseImportService` usa `PreviewCaseOnly` per bloccare XML
ricchi di fatti non rappresentabili dal modello Sinistro. Lo store
`PgLegacyXmlCaseImportStore` accettava tuttavia un DTO
`LegacyXmlPreviewDto` con `HasUnmappedBusinessData=true` purché
`AttachmentCount=0` e formato noto. Un caller interno che avesse
saltato il Business preflight poteva committare una pratica parziale
con audit di successo.

Il replay del ledger confrontava il SHA-256 richiesto con quello
persistito e verificava soltanto l'esistenza del Sinistro. Non
confrontava il riferimento esterno e le due date della preview con
la riga realmente collegata all'idempotency key. Un DTO forgiato
con lo stesso digest poteva ricevere l'UUID originario come
`Replayed=true` anche con fatti incoerenti.

## Decisione

1. La validazione preliminare dello store rifiuta
   `HasUnmappedBusinessData=true`, oltre ai guard preesistenti
   su allegati/formato/riferimento canonico; prima di aprire
   una connessione o creare ledger, Sinistro o audit.
2. Per un replay, dopo confronto del digest in tempo costante,
   leggere dal Sinistro del tenant soltanto
   `external_reference,occurred_on,reported_on` tramite
   `TenantDbScope` FORCE RLS. Eseguire confronti esatti
   `StringComparison.Ordinal`, `DateOnly` e nullabilità;
   record mancante o mismatch → `LegacyXmlIdempotencyConflictException`.
3. Un replay genuino produce sempre lo stesso UUID e zero
   nuovi audit di successo. Nessun errore di collisione si
   converte in "busy" e nessun retry automatico viene aggiunto.
4. Nessun nuovo endpoint REST/MCP, DI o variazione a schema
   V001–V012/trigger V012/privilegi PostgreSQL. Resta vietata
   la deduplicazione per mero riferimento storico.

## Prove

- Test RED-only [7347638](https://github.com/KeyserDSoze/Peritial/commit/7347638497db4e10eae167a5376e06ae34ac0192):
  tre fallimenti attesi su PostgreSQL 17 reale nei casi
  `HasUnmappedBusinessData=true` (Classic, Accertatore, replay).
  Nel run [#45161929108](https://github.com/KeyserDSoze/Peritial/actions/runs/45161929108),
  Domain 23/23 e Business 145/145 verdi; Infrastructure
  3 FAIL sui nuovi test, 197 PASS.
- Test aggiuntivo [ef94777](https://github.com/KeyserDSoze/Peritial/commit/ef947772a571716ea2496cdf62ff63ab43124a59)
  con riferimento, data evento, data denuncia alterati a key/digest
  costanti e replay genuino successivo.
- Fix [c59cfed](https://github.com/KeyserDSoze/Peritial/commit/c59cfed0348bb76d1f989499c0d35513fa1d77db):
  guard fail-closed e verifica read-only dei fatti originali prima
  di ritornare il replay. I contatori verificano nessuna nuova
  riga, nessun ledger estraneo e nessun audit spurio.

## Limiti

Il flag `HasUnmappedBusinessData` è una proprietà del DTO e
**non** un'attestazione del contenuto XML. Il solo parser protetto
può calcolarlo correttamente; un caller interno che falsifichi
il flag e il digest resta fuori dal modello di garanzia.
La correttezza dell'identità `YearlyId`/compagnia/polizza,
l'import ricco con timestamps/allegati, la governance legale,
la concorrenza multi-replica e i test di rilascio Azure/on-prem
sono ancora aperti. La funzione non autorizza la pubblicazione
dell'import: #24 e #10 rimangono aperte.
