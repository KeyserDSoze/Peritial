# ADR 0002 — Identity e isolamento tenant

Stato: accettata — 2026-11-15

Decisione: OIDC esterno, access token JWT con issuer e audience verificati; claim `tenant_id` obbligatorio. Keycloak on-prem, Microsoft Entra ID Azure (configurazioni separate). La stessa identità/claims autorizza REST e MCP, con scope read/write.

Motivazioni: evita provider proprietario nel dominio; l'identità di tenant non è un dato in input da client.

Conseguenze: ogni query e oggetto di storage è scoped per tenant; gli identity provider devono emettere il claim obbligatorio. Prevedere test cross-tenant e revisione delle configurazioni OIDC prima della produzione.
