# ADR 0051 — Preflight sicuro degli XML Classic/Accertatore (#10)

- Data: 20 novembre 2026
- Stato: implementato su `main`, verifica CI completa da attestare
- Branch `death`: storico, consultato soltanto in lettura
- Issue: #10, #4, #12

## Contesto e decisione

Gli importatori legacy in `death` differenziano:
`it.autosoftws.ReIncaricoRecord`,
`it.autosoftws.ReIncaricoBaseRecord` (Classic) e
`it.autosoftws.IncaricoAccertatoreRecord`
(Accertatore). Il vecchio `ImportManager` crea
anagrafiche, Sinistri, Perizie e salva allegati durante
l'import senza un unico confine transazionale; non deve
essere trasferito inalterato al sistema OAuth/tenant-RLS.

La prima tranche implementa **solo** un parser di
anteprima senza side effect, dentro Business:
`LegacyXmlPreviewService` con `peritial.write`
obbligatorio. REST `POST /api/imports/xml/preview`
accetta `application/xml`/`text/xml` e MCP
`preview_legacy_xml(xml)` richiama lo stesso codice.
La risposta allowlist consiste soltanto in:
`format`, `externalReference`, `occurredOn`,
`reportedOn`, `attachmentCount`. Non contiene
persona, indirizzo, filename, base64 allegati,
scanner output, storage keys, tenantId o URL interni.
Non viene creato un Sinistro e non avvengono
mutazioni di PostgreSQL, upload, audit di successo
né riserve di idempotenza.

## Misure di sicurezza

- Budget di 1 MiB **in byte**, controllato sia prima
  del parsing sia nel ciclo di lettura HTTP senza
  dipendere da Content-Length; massimo Kestrel
  dedicato al path, budget UTF-8 su MCP.
- `XmlReader` con `DtdProcessing.Prohibit`,
  `XmlResolver=null`, limiti di caratteri e
  `ConformanceLevel.Document`. Il parser non
  usa XMLSerializer su input non fidato né carica
  entità remote.
- Identificazione esatta delle tre radici senza
  namespace; nessuna euristica da singoli nodi.
  `NumeroSinistro` non vuoto, max 100 caratteri,
  `DataOraSinistro` obbligatoria; `DataIncarico`
  facoltativa. Date strict
  `yyyy-MM-dd`/`yyyy-MM-dd HH:mm:ss` convertite
  alla sola data civile, senza timezone implicito.
  Rifiuta duplicati e campi obbligatori annidati.
- `allegati` può contenere massimo 128
  `it.autosoftws.AllegatoRecord`; la preview
  conta i nodi senza interpretare il Base64,
  eseguire download o trasferire contenuti al
  motore AV. L'input XML deve ancora passare
  malware scan se diventerà un file importato.
- OAuth `peritial.write` è richiesto **prima**
  dell'operazione HTTP e nuovamente dal servizio;
  accesso MCP di sola lettura non abilita il tool.

## Test e confini della tranche

Test-first: `LegacyXmlPreviewTests` per i
tre tracciati, campi e date, payload malformati,
DTD/XXE, limiti, duplicati, forma della risposta
e scope negato. Playwright con Keycloak reale
verifica parità REST/MCP, 401/403, 400/415,
assenza di PII e assenza di inserimento Sinistri.

**Non è un parser completo di polizze, compagnia,
anagrafiche o danneggiati**, né una vera importazione:
#10 resta aperta per validazione approfondita dei
sottotipi, associazioni tenant-scoped, gestione
duplicati, workflow di confronto/approvazione,
idempotenza e transazione PostgreSQL+audit,
report di import, allegati con ClamAV e recovery
object store. Non chiamare questa API `import`
né esporre un endpoint di commit prima dei gate.
