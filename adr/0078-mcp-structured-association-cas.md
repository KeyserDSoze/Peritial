# ADR 0078 — Output MCP strutturati per associazioni con CAS monotono

- Data: 21 novembre 2026
- Stato: implementato su `main`; la CI del commit è da verificare
- Issue: #12 (MCP), #23 (CAS/ABA), #7 e #9 (relazioni), #4 e #21 (sicurezza/audit)
- `death`: storico, nessuna modifica

## Decisione

Abilitare `UseStructuredContent=true` solo per le tre
mutazioni esistenti delle associazioni Perizia–Sinistro:

| Tool | DTO allowlisted | Precondizione |
|---|---|---|
| `link_perizia_sinistro` | `PeriziaSinistroLinkDto` | expectedLinkVersion positiva, associazione libera |
| `reparent_perizia_sinistro` | `PeriziaSinistroLinkDto` | expectedSinistroId, nuovo ID diverso, revisione corrente |
| `unlink_perizia_sinistro` | `PeriziaSinistroAssociationDto` | expectedSinistroId e revisione corrente |

Il manifesto dei structured tools 1.0.0, riferito al
catalogo MCP 1.2.0, aumenta da **15 a 18/25**.
Il controllo statico mantiene la allowlist per nome
dei tre nuovi mutatori e rifiuta drift di `UseStructuredContent`
e dell'elenco, oltre a bloccare `ingest_file` e
`delete_sinistro` (non revisionati per questa tranche).
Nessuna concessione generale ai writer: restano
`access=write`, `effect=mutating` e
`destructiveHint=true` / `openWorldHint=false`.

## Proprietà preservate

- Gli argomenti REST e MCP restano invariati e delegano
  a `PeriziaSinistroLinkService`, che richiede JWT
  `peritial.write` e tenant firmato.
- Il repository esegue una singola transazione con CAS,
  history e audit atomici sotto FORCE RLS.
- `linkVersion` passa 1→2→3→4 attraverso
  link→reparent→unlink e non torna a 1 dopo unlink.
- `PeriziaSinistroAssociationDto.SinistroId` è
  nullable: dopo unlink REST può inviare `null` e MCP
  può omettere il campo. Nessun campo tenant, subject,
  audit o storage è esposto nei DTO.
- I replay con versione obsoleta restano negati,
  inclusa la condizione ABA, senza modificare la
  revisione né cancellare Sinistri/Perizie.

## Prove

Playwright su Keycloak e PostgreSQL reali usa
`MCP-Protocol-Version: 2026-07-28`, confronta
`tools/list.outputSchema` con il contratto DTO,
verifica `tools/call.structuredContent`, il diniego
del JWT reader e la parità campo per campo con
`GET /api/perizie/{id}/sinistro` dopo commit.
Il test controlla ogni replay stale e l'assenza
di modifiche a `Sinistro.Version`. La copertura
dei test PG già presenti su audit, CAS, history
e isolamento tenant rimane obbligatoria in CI.

## Limiti

Questa tranche non modifica algoritmo CAS, schema
PostgreSQL, audit, versioning degli input, scope
OAuth, REST, import XML, PWA o gli altri tool.
La #23 resta aperta per requisiti di audit/provenienza
non interamente soddisfatti; la #12 rimane aperta
per i **7 tool senza structuredContent**, client
MCP esterni, negoziazione versioni e futuri use case.
