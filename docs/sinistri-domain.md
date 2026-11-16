# Sinistri — primo aggregate di dominio (#7)

Il porting dei sinistri inizia con `src/backend/Domains/Sinistro.cs`
e `tests/backend/Peritial.Domains.Tests/SinistroTests.cs`.
Questo è un **incremento interno di dominio**, non una CRUD disponibile.

## Caratterizzazione minima

Una ricognizione *sola lettura* dello storico `death` individua
`ExternalId` (obbligatorio, massimo 100 caratteri),
`AccidentDate` e `DenunciationDate` (opzionale), oltre a riferimenti
a assicurazione, agenzia, località e perizie. Non sono stati copiati
componenti legacy.

Il modello nuovo espone:
- `Id: Guid` e `TenantId` esplicito e validato.
- `ExternalReference: string` normalizzato con trim, lunghezza 1–100.
- `OccurredOn: DateOnly` e `ReportedOn: DateOnly?` come **sole date di
  calendario provvisorie**; non è presunta una cronologia di denuncia.
- `CreatedAtUtc: DateTime` di creazione, più `Restore` per future
  implementazioni di persistenza.
- `Update(actorTenant, ...)` con blocco cross-tenant e validazione prima
  di applicare tutte le modifiche.

**Limiti critici:** nello storico la data evento è `datetime`, dunque
ore, timezone e rappresentazione di dati importati dovranno essere
caratterizzate prima di scegliere lo schema persistente. Il solo
controllo `TenantId` nell'aggregate non costituisce autorizzazione
REST/MCP né isolamento di database.

## Lavoro ancora necessario per #7

- Contratti REST/MCP con scope e tenant derivato dal JWT firmato.
- Repository PostgreSQL tenant-scoped con FORCE RLS, vincoli per tenant,
  migrazioni versionate, audit atomico, concorrenza e test DB reali.
- CRUD e ricerca/autocomplete con filtri e paginazione.
- Collegamento a compagnie, polizze, anagrafiche e perizie (#8);
  import Classic/Accertatore (#10) senza assunzioni sul legacy.
- UI PWA it/en, test E2E desktop/mobile e documentazione API.

Riferimento decisione: [ADR 0030](../adr/0030-tenant-scoped-sinistro-domain.md).
