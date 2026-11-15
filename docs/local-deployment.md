# Esecuzione locale

Prerequisiti: Docker Engine, Docker Compose v2.

- Avvio: docker compose -f docker/compose.yml up --build -d
- PWA http://localhost:5173 ; REST http://localhost:8080/api/perizie ; MCP http://localhost:8080/mcp
- Health: http://localhost:8080/health/live
- Keycloak: http://localhost:8081 ; SeaweedFS S3: http://localhost:8333
- Utente solo locale: localuser / local-only-user; admin Keycloak locale: localadmin / local-only-admin.
- Test backend PostgreSQL reale: docker compose -f docker/compose.yml --profile test run --rm tests

ATTENZIONE: i segreti dimostrativi e il singolo tenant hardcoded nel realm Keycloak sono esclusivamente per il computer di sviluppo. Non distribuire questa configurazione in produzione. Servono HTTPS, provider OAuth e claim tenant reali, secret manager, backup, migrazioni versionate, scansione file e security tests.

## Login OAuth end-to-end

Per l'ambiente Docker locale, l'issuer OIDC è `http://keycloak.localhost:8081/realms/peritial`. È **lo stesso URL** per browser, token JWT e validazione lato API: l'alias `keycloak.localhost` risolve localmente sul browser, mentre `extra_hosts` mappa l'alias al gateway host nel container API. Evita il caso in cui la validazione JWT non possa scaricare le chiavi JWKS esposte da un issuer raggiungibile solo dal browser.

Test di login, creazione perizia, Bearer auth e tool MCP: `E2E_FULL_STACK=1 E2E_BASE_URL=http://localhost:5173 npm run test:e2e` in `src/frontend`, dopo l'avvio Compose. I test con OAuth sono eseguiti anche nella job `compose` di GitHub Actions.

In produzione usare un issuer **HTTPS** risolvibile sia dai client che dall'applicazione, senza alias di sviluppo, e vietare `RequireHttpsMetadata=false`.
