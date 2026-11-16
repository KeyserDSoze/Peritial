import { test, expect, type APIRequestContext } from "@playwright/test";

// Explicit development-only Keycloak clients allow negative JWT tests without
// weakening the real PWA's Authorization Code + PKCE settings.
const api = "http://localhost:8080";
const issuer = "http://localhost:8081/realms/peritial";
const mcpHeaders = (token: string) => ({
  Authorization: "Bearer " + token,
  Accept: "application/json,text/event-stream",
  "Content-Type": "application/json"
});

async function tokenFor(request: APIRequestContext, clientId: string): Promise<string> {
  const response = await request.post(issuer + "/protocol/openid-connect/token", {
    form: {
      grant_type: "password",
      client_id: clientId,
      username: "localuser",
      password: "local-only-user"
    }
  });
  expect(response.status(), await response.text()).toBe(200);
  const json = await response.json() as { access_token: string };
  expect(json.access_token).toBeTruthy();
  const claims = JSON.parse(Buffer.from(json.access_token.split(".")[1], "base64url").toString("utf-8")) as {sub?: string};
  expect(claims.sub, "A signed subject is required for authorization/audit").toBeTruthy();
  return json.access_token;
}

test.describe("OAuth/MCP negative authorization and RFC 9728 discovery", () => {
  test.skip(!process.env.E2E_FULL_STACK, "Keycloak development realm + API needed");

  test("MCP protected resource metadata uses canonical endpoint and discoverable challenge", async ({request}) => {
    const metadata = await request.get(api + "/.well-known/oauth-protected-resource/mcp");
    expect(metadata.status()).toBe(200);
    const value = await metadata.json() as {resource: string; authorization_servers: string[]; scopes_supported: string[]};
    expect(value.resource).toBe(api + "/mcp");
    expect(value.authorization_servers).toContain(issuer);
    expect(value.scopes_supported).toContain("peritial.read");
    expect(value.scopes_supported).toContain("peritial.write");

    const denied = await request.post(api + "/mcp", {
      headers: {"Accept": "application/json,text/event-stream", "Content-Type": "application/json"},
      data: {jsonrpc: "2.0", id: 1, method: "tools/list"}
    });
    expect(denied.status()).toBe(401);
    expect(denied.headers()["www-authenticate"]).toContain(
      'resource_metadata="' + api + '/.well-known/oauth-protected-resource/mcp"'
    );
  });

  test("wrong audience, malformed token and missing tenant deny with 401", async ({request}) => {
    const wrong = await tokenFor(request, "peritial-test-wrong-audience");
    const noTenant = await tokenFor(request, "peritial-test-no-tenant");

    for (const token of [wrong, noTenant, "this-is-not-a-jwt"]) {
      const denied = await request.get(api + "/api/perizie", {
        headers: {Authorization: "Bearer " + token}
      });
      expect(denied.status(), await denied.text()).toBe(401);
    }
  });

  test("read-only tokens can list but not mutate REST or MCP", async ({request}) => {
    const readOnly = await tokenFor(request, "peritial-test-readonly");
    const list = await request.get(api + "/api/perizie", {
      headers: {Authorization: "Bearer " + readOnly}
    });
    expect(list.status(), await list.text()).toBe(200);

    const create = await request.post(api + "/api/perizie", {
      headers: {Authorization: "Bearer " + readOnly},
      data: {reference: "REJECT-" + Date.now()}
    });
    expect(create.status(), await create.text()).toBe(403);

    const tool = await request.post(api + "/mcp", {
      headers: mcpHeaders(readOnly),
      data: {jsonrpc: "2.0", id: 2, method: "tools/call",
        params: {name: "create_perizia", arguments: {reference: "REJECT-MCP-" + Date.now()}}}
    });
    // Protocol-level error result is allowed for an unauthorized tool.
    expect(tool.ok(), await tool.text()).toBeTruthy();
    expect(await tool.text()).toContain('"isError":true');
  });

  test("tokens without Peritial scope cannot enter REST or MCP", async ({request}) => {
    const none = await tokenFor(request, "peritial-test-no-scope");
    const list = await request.get(api + "/api/perizie", {
      headers: {Authorization: "Bearer " + none}
    });
    expect(list.status(), await list.text()).toBe(403);

    const tools = await request.post(api + "/mcp", {
      headers: mcpHeaders(none),
      data: {jsonrpc: "2.0", id: 3, method: "tools/list"}
    });
    expect(tools.status(), await tools.text()).toBe(403);
  });
  test("Keycloak-signed expired access tokens deny REST and MCP after configured clock skew", async ({request}) => {
    // The API intentionally allows 60 s clock skew. Test the real issuer's
    // short-lived, correctly signed token rather than forging a JWT payload.
    test.setTimeout(110_000);
    const shortLived = await tokenFor(request, "peritial-test-expired");
    const claims = JSON.parse(
      Buffer.from(shortLived.split(".")[1], "base64url").toString("utf-8")
    ) as {iat?: number; exp?: number};
    expect(claims.iat).toBeGreaterThan(0);
    expect(claims.exp).toBeGreaterThan(claims.iat!);
    expect(claims.exp! - claims.iat!, "Keycloak test client must issue a short-lived JWT")
      .toBeLessThanOrEqual(10);

    const beforeRest = await request.get(api + "/api/perizie", {
      headers: {Authorization: "Bearer " + shortLived}
    });
    expect(beforeRest.status(), await beforeRest.text()).toBe(200);
    const beforeMcp = await request.post(api + "/mcp", {
      headers: mcpHeaders(shortLived),
      data: {jsonrpc: "2.0", id: 71, method: "tools/list"}
    });
    expect(beforeMcp.ok(), await beforeMcp.text()).toBeTruthy();

    // After exp + the API's one-minute ClockSkew, neither HTTP entrypoint
    // may accept the same token, even though its signature remains valid.
    const expiryWithSkew = claims.exp! * 1_000 + 65_000;
    await new Promise<void>(resolve =>
      setTimeout(resolve, Math.max(0, expiryWithSkew - Date.now()))
    );

    const expiredRest = await request.get(api + "/api/perizie", {
      headers: {Authorization: "Bearer " + shortLived}
    });
    expect(expiredRest.status(), await expiredRest.text()).toBe(401);
    const expiredMcp = await request.post(api + "/mcp", {
      headers: mcpHeaders(shortLived),
      data: {jsonrpc: "2.0", id: 72, method: "tools/list"}
    });
    expect(expiredMcp.status(), await expiredMcp.text()).toBe(401);
  });
});
