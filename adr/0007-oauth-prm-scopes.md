# ADR 0007 — OAuth protected resource discovery e autorizzazioni REST/MCP

Stato: adottata per il vertical slice in sviluppo — 2026-11-15.

## Problema
L'endpoint MCP aveva una discovery RFC 9728 non allineata alla risorsa canonica: la URL inviata dal challenge Bearer non identificava correttamente il path del server MCP. Un access token con tenant assente veniva rifiutato soltanto nel service layer, con status 403 anziché 401. Un token senza scope poteva interrogare il catalogo dei tool MCP.

## Decisione
1. Auth:Resource identifica esattamente l'URL pubblico canonico dell'endpoint MCP, con path /mcp. Obbligo HTTPS in Production, nessun resource URL derivato dall'header Host. Pubblicare /.well-known/oauth-protected-resource/mcp, e metadata root distinta per API REST. Il challenge 401 di /mcp punta alla metadata path-aware.
2. Token JWT validati da JwtBearer (issuer, audience, signature, expiry). Nel callback OnTokenValidated il tenant UUID non vuoto e il soggetto firmato devono esistere; i token privi di claim essenziali sono rifiutati con 401 prima di accedere a REST/MCP.
3. Policy di endpoint richiedono peritial.read (con peritial.write come superset) o peritial.write per la mutazione. L'ingresso MCP richiede almeno uno scope applicativo; gli use case Business applicano permessi per ciascuna invocazione dei tool, inclusi quelli di upload. Scope insufficiente dà 403 sugli endpoint REST; un tool MCP negato può essere restituito come errore nel protocollo, senza modificare dati.
4. Keycloak 26 locale: client scope basic con oidc-sub-mapper per garantire il subject firmato nel token di accesso. Test-only clients con password grant sono permessi *soltanto* nel file del realm Compose; la PWA usa Authorization Code + PKCE.
5. Non registrare bearer token o payload file in log. L'identità del tenant resta derivata dal token firmato, mai dall'input REST/MCP.

## Limitazioni
La conformità discovery e i test negativi locali non provano l'interoperabilità con gli auth server e client MCP reali. Da verificare: Entra in Azure, almeno due client MCP esterni, scadenze token, authorization Resource Indicators RFC 8707 e audit delle mutazioni in storage durevole (#4 e #19). Gli account di test direct grant non sono adatti ad ambienti di produzione. Le dipendenze di deployment sono tracciate da #17.
