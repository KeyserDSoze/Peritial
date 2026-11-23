# ADR 0107 — DOCX OPC relationship: target URI bounded within package

- Data: 23 novembre 2026
- Stato: implementato su main; CI full-stack dell'HEAD da verificare
- Tracciamento: issue #5 sicurezza documenti; #4 test condivisi REST/MCP
- Branch death: archivio storico, nessuna scrittura

## Contesto

L'ADR 0105 ha bloccato TargetMode=External, URI assoluti e UNC nei
documenti OOXML. Un attributo Relationship TargetMode="Internal",
però, può puntare oltre la radice del DOCX mediante ../../,
percent-encoding (%2e%2e/%2e%2e), slash encode (%2f) o valori
double-encoded. Un consumer OOXML futuro che risolve quel riferimento
senza sandbox potrebbe leggere fuori dal pacchetto ZIP. Inoltre i
target possono nominare dispositivi DOS (NUL, CON, COM1.bin)
anche quando il membro ZIP non esiste: il filtro sui soli nomi delle
entry dell'ADR 0104 non li rilevava.

## Decisione

La validazione Business già esistente per ogni file .rels ora:

1. mantiene XmlReader con DTD proibite e XmlResolver = null;
2. considera soltanto relazioni interne (TargetMode assente o Internal);
3. rimuove whitespace ai confini e decodifica una sola volta
   il percent-encoding del Target, dopo l'espansione degli attributi
   XML del parser; rifiuta ulteriori percentuali residue per evitare
   interpretazioni di doppia decodifica downstream;
4. rifiuta path con backslash, caratteri di controllo, URI/schemi
   assoluti, un'autorità //host o due punti nel percorso;
5. calcola il directory depth della source part OPC:
   word/_rels/document.xml.rels appartiene a word/document.xml
   (depth 1), _rels/.rels alla radice; i target con slash singola
   iniziale sono relativi alla radice del pacchetto (depth 0);
6. percorre i segmenti del path, ignorando il segmento punto e
   i delimitatori query/fragment; ogni parent ".." che porterebbe
   il depth sotto zero viene rifiutato. Non esegue IO, richieste
   DNS o file open;
7. rifiuta in qualsiasi segmento del target i nomi dispositivo
   Windows riservati e i nomi terminanti in punto/spazio (per
   uniformità con i controlli dei membri ZIP).

Restano ammessi collegamenti interni tipo ../media/image.png,
../media/my%20image.png, /word/media/image.png, #bookmark
e percorsi validi derivati da .rels annidati. Il file riferito
non deve esistere al momento dell'ingestione; la validazione
non pretende di certificare un parser OOXML completo.

Tutti i rifiuti sono ArgumentException generiche senza il target
ostile. Avvengono prima di ClamAV, PUT, metadata e audit successo.
Non si modifica database/RLS, schema o limiti di upload.

## Evidenza di sviluppo e CI

- Test per traversal e percent encoding:
  [84ecb5a](https://github.com/KeyserDSoze/Peritial/commit/84ecb5a2ea7cca023dcef95da44e2ffa1da91071).
  L'iniziale CI [#45198971059](https://github.com/KeyserDSoze/Peritial/actions/runs/45198971059)
  NON è una prova RED comportamentale, avendo rilevato un
  escape C# invalido nella fixture di test. Corretto in
  [c704a2b](https://github.com/KeyserDSoze/Peritial/commit/c704a2b76130722ef0721b9fe3138d57a76fa20b).
- Fix package-root e decode:
  [2a3cc82](https://github.com/KeyserDSoze/Peritial/commit/2a3cc822c82a792f0bc162fb8e9c2e52d196b721).
  [CI backend #45199472363](https://github.com/KeyserDSoze/Peritial/actions/runs/45199472363):
  **461/461 test PASS** (23 Domain, 237 Business, 201 Infrastructure),
  prima delle cinque ulteriori prove sui nomi Windows.
- Test-first RED effettivo sui device name nei target:
  [a9f7a09](https://github.com/KeyserDSoze/Peritial/commit/a9f7a09a52a96963dfbdf33161e767a8504af58c)
  e [CI #45199769891](https://github.com/KeyserDSoze/Peritial/actions/runs/45199769891):
  **5 Business FAIL attesi**, 237 Business verdi, 23 Domain e
  201 Infrastructure verdi. Il fix è
  [59803b1](https://github.com/KeyserDSoze/Peritial/commit/59803b1db45c5cdbe4aebb55c9aed4d9b382c014);
  [CI #45199924025](https://github.com/KeyserDSoze/Peritial/actions/runs/45199924025)
  da attestare fino a test backend/Compose completati.
- Test E2E JWT Keycloak REST multipart e MCP ingest_file:
  [c0fcec1](https://github.com/KeyserDSoze/Peritial/commit/c0fcec16bfed8549f86ccc523cbf655915025f58).
  DOCX ZIP sintetico con tre membri e .rels ben formato che
  dichiara TargetMode="Internal" ma usa ../../PRIVATE-ROOT-ESCAPE.xml.
  Le aspettative sono HTTP 400 REST e isError:true MCP,
  senza divulgazione del target; esito Compose definitivo da verificare.

## Limiti e requisiti ancora aperti

La decisione è un gate di ammissione che conserva i byte
DOCX integri, non un parser OPC/OOXML di riferimento o un
renderer sicuro. TargetMode e URI possono avere interpretazioni
diverse nei converter; questi devono restare isolati da rete e
filesystem host. Quarantena persistente, rescan, approval,
retention/legal hold, firme reali ClamAV/FreshClam, rate-limit
multi-replica, verifiche Azure/on-prem e semantic validation
completa restano aperti su #5. #4 e #21 mantengono i gate
OAuth client esterni e atomicità.
