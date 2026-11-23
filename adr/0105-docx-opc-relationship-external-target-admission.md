# ADR 0105 — DOCX: OPC relationship external targets fail-closed

- Data: 23 novembre 2026
- Stato: implementato su `main`; attendere esito CI terminale dell'HEAD
- Tracciamento: issue #5 (sicurezza documenti), #4 (REST/MCP condividono Business)
- Branch storico `death`: non modificato

## Contesto

Le ADR 0100–0104 limitano ZIP bomb, XML DTD/XXE, tipi Unix speciali e
ambiguità dei nomi ZIP. Gli XML `.rels` erano letti in modo sicuro da
`XmlReader`, ma l'ammissione non distingueva un link interno da una
relazione OPC che un renderer può risolvere via rete o filesystem:

```xml
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="urn:testing"
    TargetMode="External" Target="https://example.invalid/image.png"/>
</Relationships>
```

XML ben formato e DTD disabilitati **non** impediscono a un successivo
consumer di seguire il target. Anche `TargetMode` mancante o
`Internal` non rende un URL assoluto implicitamente sicuro.

## Decisione

Durante la seconda passata bounded del DOCX, ogni parte ZIP `.rels`
(case-insensitive) continua a essere validata come XML sicuro.
Per ogni elemento `Relationship` (test sul `LocalName`) si controlla
*il valore dell'attributo XML decodificato*:

- `TargetMode` assente oppure `Internal` è consentito; ogni altro
  valore, compreso `External` con target relativo e valori ignoti,
  è respinto prima dello scanner.
- `Target` non deve essere un URI assoluto (`https:`, `file:`,
  `urn:` ecc.), un riferimento network-path `//host/...`, un
  percorso UNC `\\host\...` o un ibrido `/\\host`.
- Trimming del target prima della classificazione per evitare
  l'interpretazione differita di whitespace.
- Il percorso con **una sola slash** iniziale
  `/word/media/image.png` è un percorso *interno alla package OPC*,
  non una risorsa filesystem esterna; deve restare valido, insieme
  ai link relativi `../media/image.png`.
- Nessun fetch, risoluzione URI, DNS, accesso a file o estrazione
  avviene nella validazione. Il controllo si applica solo
  alle parti `.rels`; altre forme di contenuto attivo nel DOCX
  non sono automaticamente sicure.

`ArgumentException` generica senza ripetere URL/target/parti ostili;
fallimento precede `Clamd`, blob/S3, metadati e audit successo.
Nessuna modifica a OAuth, DB/RLS, budget ZIP o branch legacy.

## Evidenza e regressioni

- Test RED: [6c60c00](https://github.com/KeyserDSoze/Peritial/commit/6c60c00613b235b81bbc498b0258e1ebc37e86a4);
  [CI #45193386203](https://github.com/KeyserDSoze/Peritial/actions/runs/45193386203):
  **9 fallimenti attesi** sul vecchio validatore, 202 Business esistenti,
  23 Domains e 201 Infrastructure passati. I tre link interni
  rimanevano ammessi.
- Prima correzione: [28418bc](https://github.com/KeyserDSoze/Peritial/commit/28418bc7faf2d1285ebcb9fe5ac045c1516fa523);
  [CI #45193490504](https://github.com/KeyserDSoze/Peritial/actions/runs/45193490504):
  un test positivo ha rilevato che `Uri.TryCreate` può considerare
  `/word/media/image.png` un URI file assoluto. Questa prova
  non è un successo GREEN.
- Test aggiuntivi per whitespace/XML entity: [a191435](https://github.com/KeyserDSoze/Peritial/commit/a191435620220710aba7d5d4a996309510a3be5e).
- Correzione semantica: [15fa8a5](https://github.com/KeyserDSoze/Peritial/commit/15fa8a55308c870c827c5212c9417f35583fe14b).
  CI [#45193944965](https://github.com/KeyserDSoze/Peritial/actions/runs/45193944965)
  da verificare fino a stato terminale.
- E2E Keycloak REST multipart e MCP `ingest_file`:
  [028394e](https://github.com/KeyserDSoze/Peritial/commit/028394e5cdcbcce7aae2030e98b032f0d04b6537),
  ZIP DOCX a tre entry con `.rels` ben formato ma
  `TargetMode="External"` e URL marker. Attesi HTTP 400 e
  `isError:true` senza disclosure del target. Compose terminale
  resta da attestare.

## Limiti / follow-up

Il gate non prova la conformità semantica completa al formato
OOXML né neutralizza contenuti attivi in altre parti XML o
embedded files; un downstream renderer deve mantenere sandbox
senza accesso rete/filesystem e un proprio safe resolver.
Rimangono aperti #5 per quarantena, rescan, FreshClam,
retention, rate limiting multi-replica, deployment reali e
validazione di oggetti incorporati; #4 per client esterni
e #21 per atomicità delle mutazioni e audit.
