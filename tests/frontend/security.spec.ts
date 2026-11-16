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
  test("REST advance accepts only exact symbolic workflow targets, like MCP", async ({request}) => {
    const writer = await tokenFor(request, "peritial-test-write");
    const headers = {Authorization: "Bearer " + writer};
    const created = await request.post(api + "/api/perizie", {
      headers,
      data: {reference: "REST-ADVANCE-" + Date.now() + "-" + Math.random()}
    });
    expect(created.status(), await created.text()).toBe(201);
    const id = (await created.json() as {id: string}).id;
    const assigned = await request.post(api + "/api/perizie/" + id + "/assign", {
      headers, data: {assignee: "api-operator"}
    });
    expect(assigned.status(), await assigned.text()).toBe(200);

    for (const invalidTarget of [2, "Cancelled", "toberevised", "999"]) {
      const invalid = await request.post(api + "/api/perizie/" + id + "/advance", {
        headers, data: {target: invalidTarget}
      });
      expect(invalid.status(), await invalid.text()).toBe(400);
    }

    const malformed = await request.post(api + "/api/perizie/" + id + "/advance", {
      headers: {...headers, "Content-Type": "application/json"},
      data: '{"target":'
    });
    expect(malformed.status(), await malformed.text()).toBe(400);

    const advanced = await request.post(api + "/api/perizie/" + id + "/advance", {
      headers, data: {target: "ToBeRevised"}
    });
    expect(advanced.status(), await advanced.text()).toBe(200);
    expect((await advanced.json() as {status: string}).status).toBe("ToBeRevised");
    const replay = await request.post(api + "/api/perizie/" + id + "/advance", {
      headers, data: {target: "ToBeRevised"}
    });
    expect(replay.status(), await replay.text()).toBe(400);
  });

  test("perizia cancellation is scoped, audited and exposed in REST and MCP", async ({request}) => {
    const readOnly = await tokenFor(request, "peritial-test-readonly");
    const writer = await tokenFor(request, "peritial-test-write");
    const writeHeaders = {Authorization: "Bearer " + writer};

    const create = await request.post(api + "/api/perizie", {
      headers: writeHeaders,
      data: {reference: "CANCEL-E2E-" + Date.now() + "-" + Math.random()}
    });
    expect(create.status(), await create.text()).toBe(201);
    const firstId = (await create.json() as {id: string}).id;

    const forbidden = await request.post(api + "/api/perizie/" + firstId + "/cancel", {
      headers: {Authorization: "Bearer " + readOnly}
    });
    expect(forbidden.status(), await forbidden.text()).toBe(403);

    const readOnlyTool = await request.post(api + "/mcp", {
      headers: mcpHeaders(readOnly),
      data: {jsonrpc: "2.0", id: 94, method: "tools/call",
        params: {name: "cancel_perizia", arguments: {id: firstId}}}
    });
    expect(readOnlyTool.ok(), await readOnlyTool.text()).toBeTruthy();
    expect(await readOnlyTool.text()).toContain('"isError":true');

    const cancelled = await request.post(api + "/api/perizie/" + firstId + "/cancel", {
      headers: writeHeaders
    });
    expect(cancelled.status(), await cancelled.text()).toBe(200);
    expect((await cancelled.json() as {status: string}).status).toBe("Cancelled");

    const replay = await request.post(api + "/api/perizie/" + firstId + "/cancel", {
      headers: writeHeaders
    });
    expect(replay.status(), await replay.text()).toBe(400);

    // Same use case through MCP on a separate perizia, with the same signed tenant.
    const secondCreate = await request.post(api + "/api/perizie", {
      headers: writeHeaders,
      data: {reference: "CANCEL-MCP-" + Date.now() + "-" + Math.random()}
    });
    expect(secondCreate.status(), await secondCreate.text()).toBe(201);
    const secondId = (await secondCreate.json() as {id: string}).id;
    const mcp = await request.post(api + "/mcp", {
      headers: mcpHeaders(writer),
      data: {jsonrpc: "2.0", id: 95, method: "tools/call",
        params: {name: "cancel_perizia", arguments: {id: secondId}}}
    });
    expect(mcp.ok(), await mcp.text()).toBeTruthy();
    expect(await mcp.text()).toContain("Cancelled");
    expect(await mcp.text()).not.toContain('"isError":true');

    const list = await request.get(api + "/api/perizie", {headers: writeHeaders});
    expect(list.status()).toBe(200);
    const perizie = await list.json() as {id: string; status: string}[];
    expect(perizie.find(p => p.id === firstId)?.status).toBe("Cancelled");
    expect(perizie.find(p => p.id === secondId)?.status).toBe("Cancelled");
  });

  test("MCP assignment and advancement share REST authorization, state and audit", async ({request}) => {
    const writer = await tokenFor(request, "peritial-test-write");
    const readOnly = await tokenFor(request, "peritial-test-readonly");
    const listTools = await request.post(api + "/mcp", {
      headers: mcpHeaders(writer),
      data: {jsonrpc: "2.0", id: 201, method: "tools/list"}
    });
    expect(listTools.ok(), await listTools.text()).toBeTruthy();
    expect(await listTools.text()).toContain("assign_perizia");
    expect(await listTools.text()).toContain("advance_perizia");

    const created = await request.post(api + "/api/perizie", {
      headers: {Authorization: "Bearer " + writer},
      data: {reference: "MCP-WORKFLOW-" + Date.now() + "-" + Math.random()}
    });
    expect(created.status(), await created.text()).toBe(201);
    const id = (await created.json() as {id: string}).id;
    const call = (token: string, requestId: number, name: string, args: object) =>
      request.post(api + "/mcp", {
        headers: mcpHeaders(token),
        data: {jsonrpc: "2.0", id: requestId, method: "tools/call",
          params: {name, arguments: args}}
      });

    const deniedAssign = await call(readOnly, 202, "assign_perizia", {
      id, assignee: "forbidden-assignee"
    });
    expect(deniedAssign.ok(), await deniedAssign.text()).toBeTruthy();
    expect(await deniedAssign.text()).toContain('"isError":true');
    const deniedAdvance = await call(readOnly, 203, "advance_perizia", {
      id, target: "ToBeRevised"
    });
    expect(deniedAdvance.ok(), await deniedAdvance.text()).toBeTruthy();
    expect(await deniedAdvance.text()).toContain('"isError":true');

    const assigned = await call(writer, 204, "assign_perizia", {
      id, assignee: "assigned-through-mcp"
    });
    expect(assigned.ok(), await assigned.text()).toBeTruthy();
    expect(await assigned.text()).toContain("Assigned");
    expect(await assigned.text()).not.toContain('"isError":true');

    const invalid = await call(writer, 205, "advance_perizia", {
      id, target: "Cancelled"
    });
    expect(invalid.ok(), await invalid.text()).toBeTruthy();
    expect(await invalid.text()).toContain('"isError":true');

    const advanced = await call(writer, 206, "advance_perizia", {
      id, target: "ToBeRevised"
    });
    expect(advanced.ok(), await advanced.text()).toBeTruthy();
    expect(await advanced.text()).toContain("ToBeRevised");
    expect(await advanced.text()).not.toContain('"isError":true');
    const replay = await call(writer, 207, "advance_perizia", {
      id, target: "ToBeRevised"
    });
    expect(replay.ok(), await replay.text()).toBeTruthy();
    expect(await replay.text()).toContain('"isError":true');

    const rest = await request.get(api + "/api/perizie", {
      headers: {Authorization: "Bearer " + writer}
    });
    expect(rest.status(), await rest.text()).toBe(200);
    const items = await rest.json() as {id: string; status: string; assignedTo: string | null}[];
    const item = items.find(p => p.id === id);
    expect(item?.status).toBe("ToBeRevised");
    expect(item?.assignedTo).toBe("assigned-through-mcp");
  });

  test("get_perizia returns only a tenant-scoped record to read-only REST and MCP tokens", async ({request}) => {
    const writer = await tokenFor(request, "peritial-test-write");
    const reader = await tokenFor(request, "peritial-test-readonly");
    const noScope = await tokenFor(request, "peritial-test-no-scope");
    const reference = "GET-MCP-" + Date.now() + "-" + Math.random();
    const created = await request.post(api + "/api/perizie", {
      headers: {Authorization: "Bearer " + writer},
      data: {reference}
    });
    expect(created.status(), await created.text()).toBe(201);
    const id = (await created.json() as {id: string}).id;
    const nonexistent = "00000000-0000-4000-8000-000000000001";

    const read = await request.get(api + "/api/perizie/" + id, {
      headers: {Authorization: "Bearer " + reader}
    });
    expect(read.status(), await read.text()).toBe(200);
    expect((await read.json() as {id: string; reference: string}).reference).toBe(reference);
    const missing = await request.get(api + "/api/perizie/" + nonexistent, {
      headers: {Authorization: "Bearer " + reader}
    });
    expect(missing.status(), await missing.text()).toBe(404);
    const blocked = await request.get(api + "/api/perizie/" + id, {
      headers: {Authorization: "Bearer " + noScope}
    });
    expect(blocked.status(), await blocked.text()).toBe(403);
    const anonymous = await request.get(api + "/api/perizie/" + id);
    expect(anonymous.status(), await anonymous.text()).toBe(401);

    const catalog = await request.post(api + "/mcp", {
      headers: mcpHeaders(reader),
      data: {jsonrpc: "2.0", id: 301, method: "tools/list"}
    });
    expect(catalog.ok(), await catalog.text()).toBeTruthy();
    expect(await catalog.text()).toContain("get_perizia");

    const tool = (token: string, rpcId: number, itemId: string) =>
      request.post(api + "/mcp", {
        headers: mcpHeaders(token),
        data: {jsonrpc: "2.0", id: rpcId, method: "tools/call",
          params: {name: "get_perizia", arguments: {id: itemId}}}
      });
    const readMcp = await tool(reader, 302, id);
    expect(readMcp.ok(), await readMcp.text()).toBeTruthy();
    expect(await readMcp.text()).toContain(reference);
    expect(await readMcp.text()).not.toContain('"isError":true');
    const missingMcp = await tool(reader, 303, nonexistent);
    expect(missingMcp.ok(), await missingMcp.text()).toBeTruthy();
    expect(await missingMcp.text()).toContain('"isError":true');

    const noScopeMcp = await tool(noScope, 304, id);
    expect(noScopeMcp.status(), await noScopeMcp.text()).toBe(403);
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
