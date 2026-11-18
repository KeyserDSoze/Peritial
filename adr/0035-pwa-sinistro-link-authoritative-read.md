# ADR 0035 — Collegamento Sinistri–Perizie nella PWA con lettura autoritativa

**Stato:** Accepted (incremento parziale #7/#12/#13).  
**Data:** 2026-11-18

## Contesto

La relazione V004 consente di collegare una perizia a un sinistro con
compare-and-swap e audit atomico, ma il DTO della lista delle perizie
non contiene `sinistroId`. Una UI che assume l'assenza del collegamento
dalla lista può tentare di collegare una perizia già associata,
confondere la concorrenza e rivelare risultati di una ricerca precedente
dopo il cambio d'identità o un errore API.

## Decisione

1. Introdurre il read model **`PeriziaSinistroAssociationDto`**:
   `{periziaId,sinistroId: UUID|null}`. Una perizia visibile
   non collegata restituisce `null`; ID mancante o di un altro tenant
   produce **404**, senza differenze pubbliche. L'adapter
   `PgPeriziaSinistroLinkRepository.FindSinistroIdForPeriziaAsync`
   esegue un `SELECT` parametrizzato sotto `TenantDbScope`
   e FORCE RLS; nessuna lettura scrive audit di successo.
   Il `PeriziaSinistroLinkService` applica `peritial.read`
   oppure `peritial.write`. REST:
   `GET /api/perizie/{id}/sinistro`; MCP:
   `get_perizia_sinistro(periziaId)`. Nessun tenant o
   subject è esposto nel DTO.
2. Aggiungere un componente PWA **`PeriziaSinistroPicker`** per
   ogni perizia, chiuso per default. L'apertura effettua il
   `GET` autoritativo con il JWT corrente; se il link esiste,
   legge il riferimento del sinistro dal normale
   `GET /api/sinistri/{id}` e visualizza il collegamento
   persistito **senza** proporre una seconda scrittura.
   Non caricare tutte le relazioni N+1 durante la lista:
   lettura **on demand** soltanto per la perizia aperta.
3. Se non collegata, l'utente può cercare per **prefisso
   esplicito di almeno 2 caratteri** tramite
   `GET /api/sinistri/search?prefix=...&limit=20`
   (server-side, tenant-scoped, wildcard letterali,
   no cache) e scegliere un risultato identificato da
   riferimento e data evento. La UI non inventa un tenant
   né invia ID fuori dalla lista restituita.
   `POST /api/perizie/{id}/sinistro` viene eseguito **una sola
   volta su click**: soltanto la risposta **201** aggiorna
   lo stato visibile; nessuna scrittura ottimistica.
4. Il permesso `peritial.write` letto dalla sessione OIDC
   **controlla soltanto la visibilità del pulsante**: la
   decisione di sicurezza resta nel Business layer con
   JWT validato, RLS e FK composta.
   La PWA rimane consultabile con `peritial.read`.
5. Su **HTTP 409** (perizia collegata altrove nel frattempo),
   la PWA azzera le opzioni e il link presunto, mostra
   l'errore e richiede un **nuovo GET firmato** mediante
   «Ricarica collegamento»; non ripete automaticamente
   il POST né assume quale sinistro abbia vinto.
   Su 401/403, rete indisponibile, timeout, 429 o 5xx
   chiude l'intera vista protetta e svuota le pratiche,
   senza coda/retry delle mutazioni. Il 404 durante
   la lettura dell'associazione causa fail-closed,
   perché la perizia non è più visibile.
   Una sequenza locale invalida i risultati di richieste
   precedenti dopo cambio input, chiusura, token o
   unmount; alla riconnessione occorre ricominciare
   dal GET tenant-scoped. Le API/MCP non entrano in
   CacheStorage/service worker.
6. Etichette e messaggi della nuova interazione disponibili
   in italiano e inglese, con input/combobox
   esplicitamente etichettati, stato busy, messaggi
   `role=alert` e pulsanti tastiera.

## Test e limiti

TDD RED backend per read model/porta mancanti,
GREEN Business+PostgreSQL reale: perizia linked/unlinked,
404 con ID assente o altro tenant, RLS e lettura senza audit.
E2E Playwright in Keycloak con PKCE autentico:
creazione perizia UI, ricerca sinistro REST, link
201, refresh e lettura persistita, conflitto
409 creato da client esterno e reload, comportamento
offline senza esposizione né coda mutazioni.
Altri test a due tenant REST/MCP già coprono le mutazioni.

Questa è la prima UI di **collegamento**, non il CRUD Sinistri
completo. Non include creazione/modifica/eliminazione sinistri
da PWA, ricerca con debounce, cursor/paginazione oltre i
20 risultati esposti dalla UI, autocomplete fuzzy,
unlink/reparent, anagrafiche/polizze, import Classic,
WCAG audit formale o provider Entra ID produttivo.
Nessuna migrazione nuova: V004 resta esplicita.
Le issue #7, #12 e #13 restano aperte.
