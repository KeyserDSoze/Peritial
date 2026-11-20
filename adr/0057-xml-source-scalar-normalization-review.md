# ADR 0057 — Proteggere la forma sorgente degli scalari XML legacy

- Data: 20 novembre 2026
- Stato: implementato su `main`, validazione E2E in corso
- Issue: #25, #10, #24, #7
- `death` resta soltanto storico

## Contesto

Le preview Classic/Accertatore ammettono solo tre
scalari diretti: `NumeroSinistro`,
`DataOraSinistro`, `DataIncarico` opzionale.
Il decoder leggeva `XElement.Value.Trim()`,
restituendo `SIN-1` da `<NumeroSinistro> SIN-1
</NumeroSinistro>` e un `reportedOn=null`
sia quando `DataIncarico` mancava sia quando
compariva come `<DataIncarico/>`. Questo
comportamento è utile per una **preview**
read-only, ma non autorizza una modifica
silenziosa di un'identità o della semantica
dell'input destinato al commit. È particolarmente
rischioso durante la riconciliazione con un
Sinistro già esistente: `Trim` può introdurre
una corrispondenza che non è fedele all'XML.

## Decisione e confini

1. La preview continua a esporre il riferimento
   e le date già normalizzati, senza aggiungere
   valori grezzi, XML, PII o campi sensibili.
2. La mera **presenza** di scalari che richiedono
   trim è rappresentata dal booleano
   `HasUnmappedBusinessData=true`. Vale per
   ciascuno dei tre scalari diretti permessi.
3. Il nodo `DataIncarico` presente ma vuoto
   (anche se contiene solo whitespace) imposta
   il flag: l'assenza del nodo è diversa da una
   presenza esplicita senza data. Non attribuiamo
   oggi significati di business alle due varianti.
4. La sola indentazione fra elementi XML,
   fuori dagli scalari, continua a essere
   ignorata come formattazione.
5. Il planner tenant-scoped segue l'ADR0055:
   zero match = `new`, due o più =
   `ambiguous`, data non compatibile =
   `conflict`; con singolo match coerente
   ma scalari normalizzati =
   `review-required`, senza UUID.
   `existing` resta un suggerimento solo
   per XML minimale e canonico.
6. `PreviewCaseOnly`, usato esclusivamente
   dal commit *interno, non esposto*, rigetta
   questi input **prima** di contattare lo
   store PG e prima di qualsiasi audit.
   La V011 rimane append-only/idempotente;
   la V012 protegge i riferimenti già importati.

## Validazione

TDD Business copre spazi prima/dopo i
riferimenti, tab, trim su due date,
`DataIncarico` esplicitamente vuoto,
indentazione innocua, `review-required`
senza UUID ed import rifiutato senza chiamate
al repository. E2E con Keycloak esercita
REST e MCP come client firmati, conservando
`Cache-Control: no-store`.
Non cambiamo lo schema o la semantica
`YearlyId`, né rendiamo pubblico il commit.
La risoluzione definitiva richiede caratterizzare
i campioni XML reali e fissare la policy di
normalizzazione nella #25/#24 senza inventarla.
