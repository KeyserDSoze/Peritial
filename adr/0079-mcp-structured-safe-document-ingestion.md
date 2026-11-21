# ADR 0079 — Output MCP strutturato per ingestion documentale revisionata

- Data: 21 novembre 2026
- Stato: implementato su `main`; CI del commit da verificare
- Issue: #12 (catalogo MCP), #5 (malware/security), #11 (documenti), #21 (audit, idempotenza)
- `death`: non modificato

## Ambito

Il tool MCP esistente `ingest_file(fileName,contentType,base64,
periziaId?,idempotencyKey?)` restituisce `Task<DocumentDto>`.
Nell'epoca MCP 2026-07-28 il server ora espone uno
`outputSchema` e un `structuredContent` naturale e
revisionato. Prima del cambiamento lo stesso tool
emetteva un blocco `content` testuale JSON.
Il catalogo input e l'implementazione Business non
cambiano.

## Decisione

`UseStructuredContent=true` viene abilitato **soltanto**
per questo nome tool. Il gate statico verifica
`access=write`, `effect=mutating` e la firma
`Task<DocumentDto>` (non un DTO arbitrario), più
l'allineamento completo tra sorgente e manifesto.
`delete_sinistro` rimane escluso e protetto dalla
regola di hard-delete dedicata, senza `outputSchema`.

Il DTO ha esattamente cinque proprietà:

| Proprietà | Tipo |
|---|---|
| `id` | Guid |
| `periziaId` | Guid? |
| `fileName` | string |
| `contentType` | string |
| `length` | long |

Niente `base64`, bytes, URL presigned, storage keys,
tenant ID, subject, audit, malware verdict o metadata
degli intenti. Il manifesto `mcp-structured-outputs.v1.json`
sale da **18 a 19 su 25** tool strutturati; il catalogo
resta versione 1.2.0 e non aggiunge tool o argomenti.

## Invarianti di sicurezza non modificati

- Il token `peritial.write` e il tenant dal JWT sono
  richiesti dal servizio Business prima di leggere il file.
  Il read-only token è rifiutato e auditato.
- Validazione MIME/magic bytes, lunghezza e antivirus
  ClamAV fail-closed prima di metadata/storage permanenti.
- Registro di intenti con riparazione dopo crash,
  audit PostgreSQL atomico e chiave idempotente
  legata a tenant/subject/fingerprint già esistenti.
- L'eventuale `periziaId` deve essere visibile nel tenant.
- Il limite MCP Base64 resta 4 MiB; upload presigned,
  rate limiting, quarantena e download sono **non obiettivi**
  e non vengono implicitamente considerati completati.

## Regressioni

Il contratto Python rifiuta l'assenza dell'opt-in,
l'assenza dal manifesto, un ritorno DTO diverso e
alterazioni di access/effect. Playwright Keycloak
con PostgreSQL, SeaweedFS e ClamAV reali verifica
`tools/list.outputSchema`, `tools/call.structuredContent`,
diniego read-only senza metadati nuovi, confronti
campo per campo con REST, nessun eco dell'input Base64,
replay MCP e REST con stesso documento e nessun
duplicato, e HTTP 409 su key con fingerprint diverso.

Questa tranche migliora il **contratto di output**:
non introduce protocollo di upload grandi file né
garanzia di disponibilità operativa oltre ai test
e ai limiti descritti. #12, #5, #11, #21 restano aperte.
