# ADR 0072 — Pagine MCP strutturate, cursori e DTO annidati

- Data: 21 novembre 2026
- Stato: implementato su main, in attesa di CI
- Issue: #12, #7, #11 e #4
- death: storico, mai modificato

## Decisione

Aggiungere `UseStructuredContent = true` ai tool
read-only `page_documents` (`DocumentPageDto`) e
`search_sinistri_page` (`SinistroPageDto`).
Entrambi hanno ritorni diretti `Task<...Dto>` e
usano già i servizi REST esistenti con FORCE RLS.
Gli output annunciano `outputSchema` con
`items` annidati e `nextCursor` nullable.

Il manifesto `docs/mcp-structured-outputs.v1.json`
include così sei tool. La suite statica verifica
l'esatta corrispondenza fra manifesto e attributi
sorgente, e si assicura che anche un tool read-only
con DTO appropriato non possa essere aggiunto
silenziosamente al manifesto senza opt-in C#.

## Prove live Keycloak

La suite `mcp-wire-output.spec.ts` esegue richieste
HTTP MCP 2026-07-28 autenticate con `peritial.read`
e usa `peritial.write` solo per creare fixture:

- due file PDF archiviati con ClamAV/S3 sulla stessa
  Perizia, `page_documents(periziaId,limit:1)`;
- due Sinistri con prefisso esclusivo e date
  delimitate, `search_sinistri_page(prefix,range,limit:1)`;
- seconda pagina tramite `nextCursor` non vuoto
  e assenza di terza pagina;
- confronti con REST su ID, cursor e nomi ammessi
  senza tenantId o storageKey;
- riuso dello stesso cursor con un filtro diverso
  rifiutato prima di esporre altri oggetti;
- verifica di `outputSchema` pubblico che non
  introduce chiavi di storage o dati tenant interni.

## Limiti

Il cursore opaco è un riferimento alla paginazione,
non un token di autorizzazione. Il tenant è sempre
ricavato dal JWT firmato; non vengono effettuate
modifiche al runtime dei servizi, OAuth, DB,
autorizzazioni, audit o cancellazione #22.
Rimangono 19 tool senza outputSchema strutturato,
client esterni non certificati e requisiti
restanti della #12.
