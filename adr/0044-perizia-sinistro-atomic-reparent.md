# ADR 0044 — Riassegnazione atomica e controllata Perizia–Sinistro

- Data: 18 novembre 2026
- Stato: implementata, verifica CI definitiva su HEAD da attestare
- Issue: #7, #9, #12, #13, #21
- Branch: esclusivamente `main`; `death` è archivio intatto.

## Motivazione

ADR 0043 ha introdotto un unlink esplicito e auditato, ma
correggere un Sinistro collegato mediante `DELETE` + `POST`
introduce uno stato `NULL` intermedio, rilascia il lock
fra operazioni e permette a un concorrente di associare
la Perizia a un terzo Sinistro. Serve un CAS **singolo**,
con target esistente, workflow non terminale e audit atomico.

## Scelta

1. Contratti espliciti: REST `PUT /api/perizie/{id}/sinistro`
   con `{expectedSinistroId,newSinistroId}`,
   HTTP 200 `{periziaId,sinistroId}`; MCP
   `reparent_perizia_sinistro(periziaId,expectedSinistroId,newSinistroId)`
   con lo stesso `PeriziaSinistroLinkService.ReparentAsync`.
   Richiedere `peritial.write` firmato prima di accedere
   ai record, UUID non vuoti e Sinistri source/target distinti.
2. `IPeriziaSinistroLinkRepository.ReparentAuditedAsync`
   usa `TenantDbScope` e verifica il target visibile nel
   tenant firmato, mai un tenantId del client. In un solo
   `UPDATE perizie SET sinistro_id=@target WHERE
   tenant_id=@tenant AND id=@perizia AND
   sinistro_id=@expected AND status NOT IN
   (@billed,@duplicated,@cancelled)`, PostgreSQL
   blocca la riga, verifica l'expected e preserva la
   FK V004 tenant-scoped; nessun `NULL` transitorio.
   Il filtro stage è dentro lo stesso SQL, non una
   lettura separata e soggetta a race. Status terminali
   `Billed`, `Duplicated`, `Cancelled` rifiutati.
3. Se il target non è visibile, o la Perizia non
   esiste/è foreign, 404 indistinguibile; source obsoleta,
   link nullo o status terminale dà 409. Se la FK
   impedisce l'UPDATE perché il target scompare tra
   controllo e mutazione, 404 generico senza leakage.
   Nessun risultato 2xx o success audit su conflitto.
4. Il singolo UPDATE e il log append-only
   `perizia.reparent-sinistro/succeeded` vanno in
   **un'unica transazione**, rollback completa se
   l'audit fallisce. L'evento identifica Perizia,
   tenant/soggetto firmati, outcome e timestamp,
   non registra payload HTTP o token.
5. PWA: azione `Cambia sinistro collegato`
   visibile soltanto a writer e con associazione
   letta dal server. Ricerca server-side target,
   scelta diversa dal link corrente, doppio passo
   «Rivedi riassegnazione»/«Conferma riassegnazione»
   con «Annulla riassegnazione» senza invio.
   Nessun optimistic success, la vista effettua GET
   firmato dopo il commit; 409 invalida dati stantii
   e richiede reload. 401/403/offline non innescano retry.
   Testo it/en, tastiera e role=status/alert.
6. Nessun DDL/migrazione V006, modifica a file/documenti,
   nessun hard-delete di Sinistri: il gate V005 produttivo
   di #22/ADR 0042 resta chiuso. Il vecchio `link` e
   `unlink` rimangono compatibili e separati.

## Test e limiti residui

Test-first RED→GREEN Business e PostgreSQL 17 reale
(tenant RLS, source stale, target foreign/missing,
CAS, status Cancelled, rollback audit), Playwright OAuth
PKCE desktop/mobile su REST/MCP e PWA
(401/403/400/404/409, confirm/cancel, GET post-commit).
Il source UUID non è un revision counter: un eventuale
**ABA** A→B→A non sarebbe rilevato. L'audit append-only
al momento registra Perizia ID e action, non l'intero
source/target: la prova storica dei passaggi richiederà
revisione monotona/evento dettagliato con policy privacy.
Rimangono policy di approvazione per stati sensibili,
audit WCAG formale, estensione a provider reali,
anagrafiche/polizze e import. Tutte le issue restano
aperte per i rispettivi criteri di accettazione.

## Aggiornamento UX stati terminali

La PWA riceve ora lo stato della Perizia dal proprio elenco
firmato e nasconde l'azione «Cambia sinistro collegato» per
`Billed`, `Duplicated` e `Cancelled`. Una transizione di
workflow verso stato terminale azzera ogni proposta locale di
riassegnazione. Il controllo di autorizzazione resta comunque
nell'UPDATE CAS PostgreSQL: nascondere un pulsante non è una
misura di sicurezza sufficiente. Playwright verifica che, dopo
l'annullamento della Perizia, l'azione scompaia e il REST PUT
rifiuti comunque la mutazione con 409.
