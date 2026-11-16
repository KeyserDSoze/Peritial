# ADR 0030 — Primo aggregate Sinistro tenant-scoped, senza trasferimento schema legacy

**Stato:** Accepted (solo fondazione di dominio; #7 rimane aperta).  
**Data:** 2026-11-16

## Contesto

L'inventario read-only del branch storico `death` mostra un `TableAccident`
con `ExternalId` obbligatorio (max 100), `AccidentDate` obbligatoria,
`DenunciationDate` facoltativa e riferimenti numerici alle anagrafiche.
Il controller storico incorpora navigazione MVC, import, filtri per ruolo
e logiche non necessariamente conformi al modello OAuth/RLS attuale.
Non si importano classi, endpoint, codice o migrazioni da `death`.

## Decisione

- Introdurre `Sinistro` in `Peritial.Domains`, con `Guid` nuovo, `TenantId`
  sempre esplicito e riferimento esterno normalizzato (trim, 1–100 caratteri).
- Per il **primo modello minimale, non ancora persistito**, rappresentare la
  **data di calendario** dell'evento con `DateOnly` e l'eventuale data di
  denuncia con `DateOnly?`; rifiutare le date default. Il legacy usa
  `datetime`: eventuali ore/minuti, timezone e relazioni fra date sono da
  caratterizzare prima di import, persistenza e contratti REST/MCP. Non si
  inventano regole temporali (es. denuncia necessariamente successiva).
- `Create`, `Restore` di persistenza e `Update` validano il dato; `Update`
  richiede il tenant attore e non modifica lo stato in caso di input invalido.
  La validazione nel dominio **non** sostituisce l'autenticazione JWT, i controlli
  di scope, RLS PostgreSQL, CAS/versioning e l'audit obbligatorio quando si
  implementeranno i repository e i relativi use case.
- `ExternalReference` non è considerato un identificatore globale né è
  attualmente garantito univoco per tenant. I futuri vincoli di unicità e
  riconciliazione con `YearlyId` richiedono caratterizzazione e test DB.
- Polizze, assicurazioni, agenzie, periti, persone, indirizzi e collegamento
  perizie restano **fuori da questo step**, coordinati con #7/#8.

## Verifica e limiti

Test xUnit di dominio su creazione, normalizzazione, data facoltativa,
rifiuto input, isolamento tenant, aggiornamento atomico e `Restore`.
TDD RED e GREEN registrati nella #7. Nessun endpoint `/api/sinistri`,
tool MCP, tabella PostgreSQL, UI o import legacy è disponibile ancora.
La parità funzionale e le condizioni di completamento della #7 restano aperte.
