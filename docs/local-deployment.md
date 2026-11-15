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

Il browser e i token usano `http://localhost:8081/realms/peritial`, l'issuer configurato in Keycloak. Per l'API, che gira in un container, `localhost` indica il container stesso: solo nell'ambiente Development il `OidcDevelopmentBackchannelHandler` reindirizza le richieste di retrieval JWKS verso `http://keycloak:8080`, lasciando intatto l'issuer e la validazione di firma, audience e scadenza.

Le richieste di metadata OpenID partono dall'indirizzo interno configurato in `Auth:MetadataAddress`. Il rewrite è rifiutato in Production.

Test E2E reale: `E2E_FULL_STACK=1 E2E_BASE_URL=http://localhost:5173 npm run test:e2e` da `src/frontend` dopo avvio Compose. Questa verifica fa parte della CI Compose.

In produzione usare sempre OIDC HTTPS raggiungibile sia dai client che dall'applicazione, e vietare i rewrite di sviluppo.
