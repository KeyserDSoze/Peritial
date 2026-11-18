# ADR 0043 — Perizia–Sinistro: unlink esplicito con CAS e audit atomico

- Data: 18 novembre 2026
- Stato: implementato; attestazione CI definitiva sul commit HEAD da verificare
- Issue: #7, #9, #12, #13, #21
- Branch: `main`; `death` resta storico senza nuovi commit.

## Problema

Il collegamento V004 `POST /api/perizie/{id}/sinistro` inserisce
un legame una sola volta, proteggendolo con tenant RLS, FK composta
e audit, e `GET /api/perizie/{id}/sinistro` legge la relazione.
Il vecchio workflow non consentiva di correggere un link errato;
un UPDATE cieco o una riassegnazione implicita rischierebbero
di cancellare un collegamento concorrente senza osservazione o audit.

## Decisione

1. Consentire **solo scollegamento esplicito**: REST
   `DELETE /api/perizie/{id}/sinistro?expectedSinistroId=UUID` con
   `peritial.write` e risposta 204 dopo commit; MCP
   `unlink_perizia_sinistro(periziaId,expectedSinistroId)` con stesso
   `PeriziaSinistroLinkService.UnlinkAsync` e DTO
   `{periziaId,sinistroId:null}`. Campo expected obbligatorio:
   non accettare `Guid.Empty`. Nessun tenant ID da browser/MCP.
2. La porta `IPeriziaSinistroLinkRepository.UnlinkAuditedAsync`
   esegue l'UPDATE PostgreSQL tenant-scoped
   `SET sinistro_id=NULL WHERE tenant_id=@tenant AND id=@perizia
   AND sinistro_id=@expected`, usando `TenantDbScope` e FORCE RLS.
   Il CAS avviene sulla **specifica associazione osservata**:
   se un altro operatore l'ha rimossa o cambiata, il risultato è
   `409 PeriziaConcurrencyConflictException`, non un successo
   idempotente né una cancellazione del nuovo link.
   Una Perizia assente/foreign è indistinguibile (404).
3. Validare action `perizia.unlink-sinistro`, outcome `succeeded`
   e resource ID nel repository. L'UPDATE e
   `PgSecurityAuditTrail.AppendWithinScopeAsync` devono effettuare
   commit nella **stessa transazione**. Fallimento audit → rollback
   del collegamento, nessun falso successo su 404/409.
   Se manca lo scope OAuth `peritial.write`, registrare
   `denied` con `resourceId=null` prima del lookup. Letture non
   mutative mantengono `peritial.read|write`.
4. PWA: la conferma è visibile soltanto su relazione esistente,
   firmata e per un utente writer; «Scollega sinistro» apre il
   controllo accessibile, «Annulla scollegamento» esce senza API
   e «Conferma scollegamento» invia una sola DELETE con
   l'UUID osservato. Dopo 204 il browser effettua un nuovo
   GET e mostra lo stato **autoritativo**; su 409 invalida
   ogni link precedente e richiede esplicitamente un reload,
   senza retry automatico. Offline, 401/403/503 restano fail-closed;
   etichette e aiuti disponibili in italiano e inglese.
5. Nessun reparent **implicito**. Per cambiare Sinistro l'utente
   deve confermare uno scollegamento e poi ricercare/collegare
   esplicitamente: **due operazioni separate**, non un reparent
   atomico. Non sono modificati V001–V005, audit storici, Perizie,
   Sinistri o documenti fuori dalla colonna nullable già esistente.
   Il gate `SinistroDeletionGate` di #22/ADR 0042 resta disabilitato
   negli ambienti produttivi e indipendente dall'unlink.

## Test e limiti

TDD RED con test Business e PostgreSQL 17 reale: writer/reader,
404 foreign/missing, 409 stale/unlinked, linked→unlinked→relinked,
rollback alla collisione dell'audit. Playwright autenticato
Keycloak con REST/MCP (anonimo 401, CAS 409, success 204,
`sinistroId:null`) e PWA con conferma, annullamento,
conflitto simulato, reload e refetch.
Rimangono non implementati reparent atomico approvato, regole di
correzione per fasi/fatturazione, ricostruzione eventi per
audit legale, relazioni polizze/anagrafiche, import legacy e
verifica WCAG produttiva. Issue #7/#9/#12/#13/#21 rimangono aperte.
