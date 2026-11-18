# ADR 0039 — Paginazione keyset del catalogo Sinistri REST/MCP/PWA

- Data: 18 novembre 2026
- Stato: implementata, CI finale da verificare sul commit HEAD
- Issue: #7, #12, #13
- Solo `main` modificato; nessuna operazione sul branch storico `death`.

## Contesto

`GET /api/sinistri?limit=50` e `list_sinistri` rimangono compatibili, ma
per liste di oltre 50 elementi non è corretto usare un OFFSET: nuove
pratiche possono spostare la finestra tra due richieste. Lo schema V003
possiede già l'indice `(tenant_id, occurred_on DESC, id)`.

## Decisione e contratto

- Nuovo REST `GET /api/sinistri/page?limit=50&cursor=...` e tool MCP
  read-only `page_sinistri(cursor?, limit=50)`, entrambi delegano a
  `SinistroService.PageAsync` (scope `peritial.read` o `peritial.write`).
- DTO `{items: SinistroDto[], nextCursor: string|null}` (camelCase REST).
  `nextCursor=null` indica l'ultima pagina. Nessun tenant ID, audit,
  riferimento oggetto o credenziale è serializzato nel DTO o cursore.
- Limite rigoroso **1–50**; repository recupera **limit+1** per
  distinguere l'ultima pagina senza COUNT, con SQL filtrato per tenant:
  `ORDER BY occurred_on DESC, id ASC` e condizione
  `occurred_on < cursor.date OR (occurred_on = cursor.date AND id > cursor.id)`.
  Non si usa OFFSET, né si creano migrazioni o nuovi indici.
- Cursore opaco al client ma **non segreto né firmato**: base64url senza
  padding di `yyyy-MM-dd|UUID-D`, dimensione massima 128 caratteri;
  decodifica canonica e validazione fail-closed. Un cursore manipolato
  può alterare il punto di partenza, **mai** il tenant o il permesso.
  Contesto tenant JWT + scope Business + `TenantDbScope` +
  PostgreSQL FORCE RLS sono verificati **a ogni pagina**.
- La PWA carica la prima pagina on demand quando si apre il catalogo,
  mostra «Carica altri sinistri» se `nextCursor` è presente,
  effettua GET firmata per la pagina successiva e deduplica gli ID già
  visibili. Refresh, filtro, chiusura, rotazione JWT, 401/403/503,
  rete/offline invalidano cursori/risposte tardive; nessun retry
  automatico di mutazioni o caching API. Un POST confermato in vista
  non filtrata invalida il vecchio cursore e richiede refresh
  per proseguire la navigazione; l'editor CAS continua a imporre
  `expectedVersion`.
- La ricerca per prefisso/date di ADR 0038 rimane **max 50** e non ha
  ancora un cursore: solo il catalogo completo supporta «Carica altri».
  I vecchi endpoint REST/MCP rimangono invariati.

## Consistenza e limiti

Il keyset evita salti e duplicati causati da **inserimenti più recenti**
tra richieste. Non è uno snapshot transazionale dell'intero catalogo:
un UPDATE concorrente che cambia `occurred_on` può riordinare
una pratica tra le pagine. Il browser deduplica gli ID visti;
per una proiezione fresca usa «Aggiorna sinistri». In questa tranche
non si promettono snapshot stabili durante ogni tipo di mutazione,
full-text/fuzzy, cursori della ricerca filtrata, cancellazione sinistri,
filtri assicurazioni/polizze o indici/benchmark a scala.

## Test ed evidenze

- Test-first Business `SinistroPaginationTests`: parità di data con
  tie-break UUID, scope, tenant, inserimento concorrente, fine pagina,
  limiti/cursori invalidi e assenza audit per letture.
- PostgreSQL reale `SinistroPersistenceTests`: seek sotto FORCE RLS,
  più tenant, stessa data e ultima pagina, limite rigettato.
- Playwright OAuth Keycloak desktop/mobile: REST `/page`, MCP
  `page_sinistri`, 400/401, pagina successiva dopo inserimento e
  assenza di tenant nel JSON; PWA append/refresh e scarto dei duplicati.
- La CI dell'HEAD è il criterio finale: eventuali run intermedi RED
  sono intenzionali o errori corretti, non valgono come GREEN.

## Evoluzione successiva

La limitazione «ricerca filtrata senza cursore» indicata in questa
decisione è stata superata dall'[ADR 0040](0040-sinistri-filtered-keyset-search.md)
con un endpoint e tool MCP separati. Il contratto originario di
`page_sinistri` e la compatibilità di `search_sinistri` non cambiano.
