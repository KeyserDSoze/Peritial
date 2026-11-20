# ADR 0058 — CDATA e riferimenti XML: identità decodificata non autorizza il commit

- Data: 20 novembre 2026
- Stato: implementato su `main`; CI completa dell'ultimo HEAD da verificare
- Issue: #25 (fedeltà sorgente), #10 (import XML), #24 (identità Sinistro)
- `death` consultato esclusivamente in lettura

## Problema

`XmlReader` espande riferimenti come `&#45;`, `&#x2D;`
e `&amp;`. `XDocument` può preservare un `XCData`,
ma `XElement.Value` unisce il suo contenuto al testo
senza indicare al chiamante che la forma del valore
sorgente era `<![CDATA[...]]>`. Per esempio:

- `<NumeroSinistro>AB-1</NumeroSinistro>`
- `<NumeroSinistro>AB&#45;1</NumeroSinistro>`
- `<NumeroSinistro><![CDATA[AB-1]]></NumeroSinistro>`

Producono un `ExternalReference` decodificato uguale.
Il valore normalizzato è utile per la **preview**,
ma non dimostra la fedeltà del dato originale, né
autorizza a selezionare un identico Sinistro o a
scartare silenziosamente la forma sorgente nel commit.

## Decisione incrementale fail-closed

Conserviamo le API e il parser sicuro:
root e scalar allowlist, max 1 MiB, DTD proibite,
`XmlResolver=null`, niente payload e contenuti
XML/PII negli output. Estendiamo la presenza
`HasUnmappedBusinessData` già introdotta
da ADR0055/0056/0057:

1. Se `document.DescendantNodes()` contiene nodi
   `XCData`, il booleano è `true`.
2. Il documento raw è conservato entro il budget.
   Per riconoscere i riferimenti **prima**
   dell'espansione di `XmlReader`, si controlla
   conservativamente il marker byte `0x26` (`&`)
   in `xml.Span`. Nei formati XML UTF-8/UTF-16/UTF-32
   supportati questo riconosce i riferimenti;
   possibili falsi positivi in sequenze non scalari
   portano solo alla revisione, mai a un commit
   non autorizzato. Non si pubblica né conserva il
   contenuto originale nel DTO.
3. `PreviewCaseOnly` dell'import V011 interno
   e non esposto rigetta ogni payload con il
   booleano `true` **prima** di richiamare
   `ILegacyXmlCaseImportStore`.
4. `LegacyXmlImportPlanService` continua a
   trattare `new`, `ambiguous` e `conflict`
   senza selezionare un candidato; con un unico
   match per riferimento/date ma markup decodificato,
   lo stato è `review-required`, ID nullo.
   XML minimale date-only canonico può ancora
   ricevere `existing`, ma soltanto come
   consiglio read-only.

## Evidenza e limiti

I nuovi test TDD Business verificano numerici
decimali/esadecimali, entità predefinite, CDATA
integrale e mista, campo data ed import bloccato
senza contattare il repository. Prova del parser
anche con BOM UTF-16 LE e BE. Playwright autentica
client REST e MCP con Keycloak e verifica che
un reference codificato non esponga l'UUID
del Sinistro abbinato né la forma XML originale.
Nessuna nuova migrazione, policy RLS, grant,
route o metodo di commit: V011/V012 invariati.

Il gate volutamente **non** garantisce la
conservazione dei byte sorgente in un registro
duraturo: la #25 resta aperta fino a progettazione
di storage, timestamp/timezone, firma/provenienza
e revisione dei dati reali; #24 resta aperta
per identità composita e concorrenza globali.
