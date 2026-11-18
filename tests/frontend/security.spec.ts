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

async function tokenFor(
  request: APIRequestContext, clientId: string,
  username = "localuser", password = "local-only-user"
): Promise<string> {
  const response = await request.post(issuer + "/protocol/openid-connect/token", {
    form: {
      grant_type: "password",
      client_id: clientId,
      username,
      password
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

  test("signed REST and MCP responses are never browser-cacheable", async ({request}) => {
    const readOnly = await tokenFor(request, "peritial-test-readonly");
    const rest = await request.get(api + "/api/perizie", {
      headers: {Authorization: "Bearer " + readOnly}
    });
    expect(rest.status(), await rest.text()).toBe(200);
    expect(rest.headers()["cache-control"]).toContain("no-store");

    const mcp = await request.post(api + "/mcp", {
      headers: mcpHeaders(readOnly),
      data: {jsonrpc: "2.0", id: 307, method: "tools/list"}
    });
    expect(mcp.ok(), await mcp.text()).toBeTruthy();
    expect(mcp.headers()["cache-control"]).toContain("no-store");

    const anonymous = await request.get(api + "/api/perizie");
    expect(anonymous.status()).toBe(401);
    expect(anonymous.headers()["cache-control"]).toContain("no-store");
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

  test("document metadata catalog is scoped, bounded and identical in REST and MCP", async ({request}) => {
    const writer = await tokenFor(request, "peritial-test-write");
    const reader = await tokenFor(request, "peritial-test-readonly");
    const noScope = await tokenFor(request, "peritial-test-no-scope");
    const headers = {Authorization: "Bearer " + writer};
    const create = await request.post(api + "/api/perizie", {
      headers, data: {reference: "DOC-CATALOG-" + Date.now() + "-" + Math.random()}
    });
    expect(create.status(), await create.text()).toBe(201);
    const periziaId = (await create.json() as {id: string}).id;

    const pdf = Buffer.from("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF\n");
    const fileName = "catalog-" + Date.now() + ".pdf";
    const uploaded = await request.post(api + "/api/files", {
      headers,
      multipart: {file: {name: fileName, mimeType: "application/pdf", buffer: pdf},
        periziaId}
    });
    expect(uploaded.status(), await uploaded.text()).toBe(201);
    const documentId = (await uploaded.json() as {id: string}).id;

    const list = await request.get(api + "/api/files?limit=50&periziaId=" + periziaId, {
      headers: {Authorization: "Bearer " + reader}
    });
    expect(list.status(), await list.text()).toBe(200);
    const rows = await list.json() as {
      id: string; periziaId: string; fileName: string; length: number;
      createdAtUtc: string; storageKey?: string;
    }[];
    const row = rows.find(x => x.id === documentId);
    expect(row).toBeTruthy();
    expect(row?.fileName).toBe(fileName);
    expect(row?.periziaId).toBe(periziaId);
    expect(row?.length).toBe(pdf.length);
    expect(row?.createdAtUtc).toBeTruthy();
    expect(row?.storageKey).toBeUndefined();
    expect(await list.text()).not.toContain("storageKey");

    const detail = await request.get(api + "/api/files/" + documentId, {
      headers: {Authorization: "Bearer " + reader}
    });
    expect(detail.status(), await detail.text()).toBe(200);
    expect((await detail.json() as {id: string}).id).toBe(documentId);
    expect(await detail.text()).not.toContain("storageKey");

    const filtered = await request.get(
      api + "/api/files?periziaId=00000000-0000-4000-8000-000000000002",
      {headers: {Authorization: "Bearer " + reader}});
    expect(filtered.status(), await filtered.text()).toBe(200);
    expect((await filtered.json() as {id: string}[]).some(x => x.id === documentId)).toBe(false);
    for (const badLimit of ["0", "101"]) {
      const bad = await request.get(api + "/api/files?limit=" + badLimit, {
        headers: {Authorization: "Bearer " + reader}
      });
      expect(bad.status(), await bad.text()).toBe(400);
    }
    const missing = await request.get(
      api + "/api/files/00000000-0000-4000-8000-000000000002",
      {headers: {Authorization: "Bearer " + reader}});
    expect(missing.status(), await missing.text()).toBe(404);
    const forbidden = await request.get(api + "/api/files", {
      headers: {Authorization: "Bearer " + noScope}
    });
    expect(forbidden.status(), await forbidden.text()).toBe(403);
    const anonymous = await request.get(api + "/api/files");
    expect(anonymous.status(), await anonymous.text()).toBe(401);

    const listTools = await request.post(api + "/mcp", {
      headers: mcpHeaders(reader),
      data: {jsonrpc: "2.0", id: 701, method: "tools/list"}
    });
    expect(listTools.ok(), await listTools.text()).toBeTruthy();
    expect(await listTools.text()).toContain("list_documents");
    expect(await listTools.text()).toContain("get_document");

    const listTool = await request.post(api + "/mcp", {
      headers: mcpHeaders(reader),
      data: {jsonrpc: "2.0", id: 702, method: "tools/call", params: {
        name: "list_documents", arguments: {periziaId, limit: 50}
      }}
    });
    expect(listTool.ok(), await listTool.text()).toBeTruthy();
    expect(await listTool.text()).toContain(documentId);
    expect(await listTool.text()).not.toContain("storageKey");
    expect(await listTool.text()).not.toContain('"isError":true');

    const getTool = await request.post(api + "/mcp", {
      headers: mcpHeaders(reader),
      data: {jsonrpc: "2.0", id: 703, method: "tools/call", params: {
        name: "get_document", arguments: {id: documentId}
      }}
    });
    expect(getTool.ok(), await getTool.text()).toBeTruthy();
    expect(await getTool.text()).toContain(fileName);
    expect(await getTool.text()).not.toContain("storageKey");

    const missingTool = await request.post(api + "/mcp", {
      headers: mcpHeaders(reader),
      data: {jsonrpc: "2.0", id: 704, method: "tools/call", params: {
        name: "get_document", arguments: {id: "00000000-0000-4000-8000-000000000002"}
      }}
    });
    expect(missingTool.ok(), await missingTool.text()).toBeTruthy();
    expect(await missingTool.text()).toContain('"isError":true');
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

  test("two distinct signed tenant identities cannot read, update or enumerate each other's sinistri through REST or MCP", async ({request}) => {
    test.setTimeout(90_000);
    const writerA = await tokenFor(request, "peritial-test-write");
    const writerB = await tokenFor(
      request, "peritial-test-write-tenant-b", "otheruser", "local-only-other-user"
    );
    const readerA = await tokenFor(request, "peritial-test-readonly");
    const claimsA = JSON.parse(Buffer.from(writerA.split(".")[1], "base64url").toString("utf-8")) as {tenant_id: string; sub: string};
    const claimsB = JSON.parse(Buffer.from(writerB.split(".")[1], "base64url").toString("utf-8")) as {tenant_id: string; sub: string};
    expect(claimsA.tenant_id).toBe("11111111-1111-1111-1111-111111111111");
    expect(claimsB.tenant_id).toBe("22222222-2222-2222-2222-222222222222");
    expect(claimsB.sub).not.toBe(claimsA.sub);

    const identityA = {Authorization: "Bearer " + writerA};
    const identityB = {Authorization: "Bearer " + writerB};
    const base = api + "/api/sinistri";
    const unique = Date.now() + "-" + Math.round(Math.random() * 100000);
    const aReference = "TENANT-A-CASE-" + unique;
    const bReference = "TENANT-B-CASE-" + unique;
    // Spoofing tenant_id in either HTTP headers or JSON must never override
    // the server-verified JWT tenant identity.
    const aCreated = await request.post(base, {
      headers: {...identityA, "X-Tenant-Id": claimsB.tenant_id},
      data: {
        tenantId: claimsB.tenant_id,
        externalReference: aReference, occurredOn: "2040-01-04", reportedOn: null
      }
    });
    expect(aCreated.status(), await aCreated.text()).toBe(201);
    const a = await aCreated.json() as {id: string; version: number; externalReference: string};
    expect(a.version).toBe(1);
    expect(await aCreated.text()).not.toContain("tenantId");
    expect(await aCreated.text()).not.toContain(claimsA.tenant_id);
    expect(await aCreated.text()).not.toContain(claimsB.tenant_id);

    async function tool(token: string, name: string, args: Record<string, unknown>, id: number) {
      const response = await request.post(api + "/mcp", {
        headers: mcpHeaders(token),
        data: {
          jsonrpc: "2.0", id, method: "tools/call",
          params: {name, arguments: args}
        }
      });
      expect(response.ok(), await response.text()).toBeTruthy();
      return response.text();
    }
    // Create B via MCP to prove both protocols are scoped to its JWT identity.
    const bCreatedMcp = await tool(writerB, "create_sinistro", {
      externalReference: bReference, occurredOn: "2040-01-05",
      reportedOn: null
    }, 901);
    expect(bCreatedMcp).toContain(bReference);
    expect(bCreatedMcp).not.toContain('"isError":true');
    expect(bCreatedMcp).not.toContain(claimsA.tenant_id);
    expect(bCreatedMcp).not.toContain(claimsB.tenant_id);

    const ownB = await request.get(base + "?limit=100", {headers: identityB});
    expect(ownB.status(), await ownB.text()).toBe(200);
    const listB = await ownB.json() as {id: string;externalReference: string;version:number}[];
    const b = listB.find(row => row.externalReference === bReference);
    expect(b).toBeDefined();
    expect(listB.some(row => row.id === a.id)).toBe(false);
    expect(await ownB.text()).not.toContain(aReference);

    const ownA = await request.get(base + "?limit=100", {headers: identityA});
    expect(ownA.status(), await ownA.text()).toBe(200);
    const listA = await ownA.json() as {id: string;externalReference: string}[];
    expect(listA.some(row => row.id === a.id)).toBe(true);
    expect(listA.some(row => row.id === b!.id)).toBe(false);
    expect(await ownA.text()).not.toContain(bReference);

    for (const [token,foreignId] of [[writerA,b!.id],[writerB,a.id]] as const) {
      const missing = await request.get(base + "/00000000-0000-4000-8000-000000000007", {
        headers: {Authorization: "Bearer " + token}
      });
      const foreign = await request.get(base + "/" + foreignId, {
        headers: {Authorization: "Bearer " + token}
      });
      expect(missing.status()).toBe(404);
      expect(foreign.status()).toBe(missing.status());
      expect(await foreign.text()).toBe(await missing.text());

      const update = await request.put(base + "/" + foreignId, {
        headers: {Authorization: "Bearer " + token},
        data: {
          tenantId: claimsA.tenant_id,
          externalReference: "FOREIGN-UPDATE-" + unique,
          occurredOn: "2040-01-06", reportedOn: null,
          expectedVersion: 1
        }
      });
      expect(update.status(), await update.text()).toBe(404);
    }

    const foreignGet = await tool(writerB, "get_sinistro", {id: a.id}, 902);
    expect(foreignGet).toContain('"isError":true');
    expect(foreignGet).not.toContain(aReference);
    const foreignUpdate = await tool(writerB, "update_sinistro", {
      id: a.id, externalReference: "FORBIDDEN-MCP-" + unique,
      occurredOn: "2040-01-08", expectedVersion: 1
    }, 903);
    expect(foreignUpdate).toContain('"isError":true');
    expect(foreignUpdate).not.toContain(aReference);
    const bCatalog = await tool(writerB, "list_sinistri", {limit: 100}, 904);
    expect(bCatalog).toContain(bReference);
    expect(bCatalog).not.toContain(aReference);
    const aCatalog = await tool(writerA, "list_sinistri", {limit: 100}, 905);
    expect(aCatalog).toContain(aReference);
    expect(aCatalog).not.toContain(bReference);

    // Read-only access cannot mutate its own tenant through either transport.
    const aRead = await request.get(base + "/" + a.id, {
      headers: {Authorization: "Bearer " + readerA}
    });
    expect(aRead.status(), await aRead.text()).toBe(200);
    const blockedWrite = await request.put(base + "/" + a.id, {
      headers: {Authorization: "Bearer " + readerA},
      data: {externalReference: "DENIED-" + unique, occurredOn: "2040-01-09",
        expectedVersion: 1}
    });
    expect(blockedWrite.status()).toBe(403);
    const blockedMcp = await tool(readerA, "update_sinistro", {
      id: a.id, externalReference: "DENIED-MCP-" + unique,
      occurredOn: "2040-01-09", expectedVersion: 1
    }, 906);
    expect(blockedMcp).toContain('"isError":true');

    const aFinal = await request.get(base + "/" + a.id, {headers: identityA});
    const bFinal = await request.get(base + "/" + b!.id, {headers: identityB});
    expect((await aFinal.json() as {externalReference: string; version:number}))
      .toMatchObject({externalReference: aReference, version: 1});
    expect((await bFinal.json() as {externalReference: string; version:number}))
      .toMatchObject({externalReference: bReference, version: 1});
  });

  test("search_sinistri autocomplete filters literal prefixes and dates server-side without cross-tenant leaks", async ({request}) => {
    const a = await tokenFor(request, "peritial-test-write");
    const b = await tokenFor(request, "peritial-test-write-tenant-b", "otheruser", "local-only-other-user");
    const reader = await tokenFor(request, "peritial-test-readonly");
    const path = api + "/api/sinistri";
    const prefix = "SR_%"+Date.now()+"-";
    async function create(token: string, reference: string, occurredOn: string) {
      const response = await request.post(path, {
        headers: {Authorization: "Bearer " + token},
        data: {externalReference: reference, occurredOn, reportedOn: null}
      });
      expect(response.status(), await response.text()).toBe(201);
      return response.json() as Promise<{id: string}>;
    }
    const recordA = await create(a, prefix + "PRIVATE-A", "2040-01-16");
    const recordB = await create(b, prefix + "PRIVATE-B", "2040-01-17");
    await create(a, "SRXXX" + Date.now() + "-DECOY", "2040-02-20");
    const url = path + "/search?prefix=" + encodeURIComponent(prefix.toLowerCase()) +
      "&occurredFrom=2040-01-01&occurredTo=2040-01-31&limit=1";
    const responseA = await request.get(url, {headers: {Authorization: "Bearer " + reader}});
    expect(responseA.status(), await responseA.text()).toBe(200);
    expect(responseA.headers()["cache-control"]).toContain("no-store");
    const matchesA = await responseA.json() as {id:string;externalReference:string;version:number}[];
    expect(matchesA).toHaveLength(1);
    expect(matchesA[0].id).toBe(recordA.id);
    expect(matchesA[0].externalReference).toBe(prefix + "PRIVATE-A");
    expect(await responseA.text()).not.toContain("tenantId");
    expect(await responseA.text()).not.toContain("PRIVATE-B");

    const responseB = await request.get(url, {headers: {Authorization: "Bearer " + b}});
    expect(responseB.status(), await responseB.text()).toBe(200);
    const matchesB = await responseB.json() as {id:string}[];
    expect(matchesB.map(x => x.id)).toEqual([recordB.id]);
    expect(await responseB.text()).not.toContain("PRIVATE-A");

    const mcp = async (token: string, id: number) => {
      const response = await request.post(api + "/mcp", {
        headers: mcpHeaders(token),
        data: {jsonrpc: "2.0", id, method: "tools/call",
          params: {name: "search_sinistri", arguments: {
            prefix: prefix.toLowerCase(), occurredFrom: "2040-01-01",
            occurredTo: "2040-01-31", limit: 1}}}
      });
      expect(response.ok(), await response.text()).toBeTruthy();
      return response.text();
    };
    const mcpA = await mcp(reader, 1101);
    expect(mcpA).toContain(prefix + "PRIVATE-A");
    expect(mcpA).not.toContain("PRIVATE-B");
    expect(mcpA).not.toContain('"isError":true');
    const mcpB = await mcp(b, 1102);
    expect(mcpB).toContain(prefix + "PRIVATE-B");
    expect(mcpB).not.toContain("PRIVATE-A");
    expect(mcpB).not.toContain('"isError":true');

    // Reject expensive/invalid queries before touching the database.
    for (const bad of [
      "?prefix=X",
      "?prefix=" + encodeURIComponent(prefix) + "&limit=51",
      "?prefix=" + encodeURIComponent(prefix) + "&occurredFrom=2040-03-01&occurredTo=2040-01-01"
    ]) {
      const result = await request.get(path + "/search" + bad, {
        headers: {Authorization: "Bearer " + reader}
      });
      expect(result.status(), await result.text()).toBe(400);
    }
    const noAuth = await request.get(url);
    expect(noAuth.status()).toBe(401);
    const noScope = await tokenFor(request, "peritial-test-no-scope");
    const forbidden = await request.get(url, {headers: {Authorization: "Bearer " + noScope}});
    expect(forbidden.status()).toBe(403);
  });

  test("signed tenants link Perizie to their own Sinistri across REST and MCP with 404 and CAS", async ({request}) => {
    test.setTimeout(110_000);
    const a = await tokenFor(request, "peritial-test-write");
    const b = await tokenFor(request, "peritial-test-write-tenant-b",
      "otheruser", "local-only-other-user");
    const reader = await tokenFor(request, "peritial-test-readonly");
    const unique = Date.now() + "-" + Math.floor(Math.random() * 100000);
    const auth = (token: string) => ({Authorization: "Bearer " + token});

    async function create(token: string, path: string, data: Record<string, unknown>) {
      const response = await request.post(api + path, {headers: auth(token), data});
      expect(response.status(), await response.text()).toBe(201);
      return response.json() as Promise<{id:string}>;
    }
    const aSinistro = await create(a, "/api/sinistri", {
      externalReference: "LINK-A-" + unique, occurredOn: "2026-11-15"
    });
    const bSinistro = await create(b, "/api/sinistri", {
      externalReference: "LINK-B-" + unique, occurredOn: "2026-11-15"
    });
    const aPerizia = await create(a, "/api/perizie", {reference: "LINK-PA-" + unique});
    const bPerizia = await create(b, "/api/perizie", {reference: "LINK-PB-" + unique});
    const aUnlinked = await create(a, "/api/perizie", {reference: "LINK-UNLINKED-" + unique});

    async function mcp(token: string, name: string, args: Record<string, unknown>, id: number) {
      const response = await request.post(api + "/mcp", {
        headers: mcpHeaders(token),
        data: {jsonrpc: "2.0", id, method: "tools/call",
          params: {name, arguments: args}}
      });
      expect(response.ok(), await response.text()).toBeTruthy();
      return response.text();
    }
    const link = await request.post(api + "/api/perizie/" + aPerizia.id + "/sinistro", {
      headers: {...auth(a), "X-Tenant-Id": "22222222-2222-2222-2222-222222222222"},
      data: {sinistroId: aSinistro.id, tenantId: "22222222-2222-2222-2222-222222222222"}
    });
    expect(link.status(), await link.text()).toBe(201);
    expect(await link.json()).toMatchObject({
      periziaId: aPerizia.id, sinistroId: aSinistro.id
    });
    expect(await link.text()).not.toContain("tenantId");

    const mcpB = await mcp(b, "link_perizia_sinistro", {
      periziaId: bPerizia.id, sinistroId: bSinistro.id
    }, 1201);
    expect(mcpB).not.toContain('"isError":true');
    expect(mcpB).toContain(bPerizia.id);
    expect(mcpB).not.toContain(aPerizia.id);

    // The PWA's reverse read model must distinguish a visible unlinked
    // perizia (sinistroId:null) from foreign/missing IDs (same HTTP 404).
    const linkedA = await request.get(api + "/api/perizie/" + aPerizia.id + "/sinistro", {
      headers: auth(reader)
    });
    expect(linkedA.status(), await linkedA.text()).toBe(200);
    expect(linkedA.headers()["cache-control"]).toContain("no-store");
    expect(await linkedA.json()).toEqual({periziaId: aPerizia.id, sinistroId: aSinistro.id});
    expect(await linkedA.text()).not.toContain("tenantId");

    const unlinkedA = await request.get(api + "/api/perizie/" + aUnlinked.id + "/sinistro", {
      headers: auth(reader)
    });
    expect(unlinkedA.status(), await unlinkedA.text()).toBe(200);
    expect(await unlinkedA.json()).toEqual({periziaId: aUnlinked.id, sinistroId: null});

    const linkedB = await mcp(b, "get_perizia_sinistro", {periziaId: bPerizia.id}, 1210);
    expect(linkedB).toContain(bSinistro.id);
    expect(linkedB).not.toContain('"isError":true');
    const unlinkedMcp = await mcp(reader, "get_perizia_sinistro", {
      periziaId: aUnlinked.id
    }, 1211);
    // JSON-RPC text content may escape the inner DTO quotes.
    expect(unlinkedMcp).toMatch(/sinistroId.{0,7}null/);
    expect(unlinkedMcp).not.toContain('"isError":true');

    const missingReverse = await request.get(api +
      "/api/perizie/00000000-0000-4000-8000-000000000004/sinistro", {headers: auth(b)});
    const foreignReverse = await request.get(api +
      "/api/perizie/" + aPerizia.id + "/sinistro", {headers: auth(b)});
    expect(missingReverse.status()).toBe(404);
    expect(foreignReverse.status()).toBe(404);
    expect(await foreignReverse.text()).toBe(await missingReverse.text());
    const foreignMcp = await mcp(b, "get_perizia_sinistro", {
      periziaId: aPerizia.id
    }, 1212);
    expect(foreignMcp).toContain('"isError":true');
    expect(foreignMcp).not.toContain(aSinistro.id);
    const anonymousReverse = await request.get(api +
      "/api/perizie/" + aPerizia.id + "/sinistro");
    expect(anonymousReverse.status()).toBe(401);

    const listA = await request.get(api + "/api/sinistri/" + aSinistro.id + "/perizie", {
      headers: auth(reader)
    });
    expect(listA.status(), await listA.text()).toBe(200);
    expect(listA.headers()["cache-control"]).toContain("no-store");
    expect(await listA.json()).toEqual([aPerizia.id]);
    const listB = await request.get(api + "/api/sinistri/" + bSinistro.id + "/perizie", {
      headers: auth(b)
    });
    expect(listB.status(), await listB.text()).toBe(200);
    expect(await listB.json()).toEqual([bPerizia.id]);

    const toolA = await mcp(reader, "list_perizie_for_sinistro",
      {sinistroId: aSinistro.id}, 1202);
    expect(toolA).toContain(aPerizia.id);
    expect(toolA).not.toContain(bPerizia.id);
    const toolB = await mcp(b, "list_perizie_for_sinistro",
      {sinistroId: bSinistro.id}, 1203);
    expect(toolB).toContain(bPerizia.id);
    expect(toolB).not.toContain(aPerizia.id);

    // Missing and foreign records share the same externally visible 404.
    const missing = await request.get(api +
      "/api/sinistri/00000000-0000-4000-8000-000000000003/perizie", {headers: auth(b)});
    const foreignList = await request.get(api +
      "/api/sinistri/" + aSinistro.id + "/perizie", {headers: auth(b)});
    expect(missing.status()).toBe(404);
    expect(foreignList.status()).toBe(404);
    expect(await foreignList.text()).toBe(await missing.text());

    const foreignTarget = await request.post(api +
      "/api/perizie/" + aUnlinked.id + "/sinistro", {
      headers: auth(a), data: {sinistroId: bSinistro.id}
    });
    expect(foreignTarget.status(), await foreignTarget.text()).toBe(404);
    const foreignPerizia = await request.post(api +
      "/api/perizie/" + aUnlinked.id + "/sinistro", {
      headers: auth(b), data: {sinistroId: bSinistro.id}
    });
    expect(foreignPerizia.status(), await foreignPerizia.text()).toBe(404);

    const replay = await request.post(api + "/api/perizie/" + aPerizia.id + "/sinistro", {
      headers: auth(a), data: {sinistroId: aSinistro.id}
    });
    expect(replay.status(), await replay.text()).toBe(409);
    const mcpForeign = await mcp(b, "link_perizia_sinistro", {
      periziaId: aUnlinked.id, sinistroId: bSinistro.id
    }, 1204);
    expect(mcpForeign).toContain('"isError":true');
    expect(mcpForeign).not.toContain("LINK-UNLINKED-" + unique);
    const mcpForeignList = await mcp(b, "list_perizie_for_sinistro",
      {sinistroId: aSinistro.id}, 1205);
    expect(mcpForeignList).toContain('"isError":true');
    expect(mcpForeignList).not.toContain(aPerizia.id);

    const denied = await request.post(api +
      "/api/perizie/" + aUnlinked.id + "/sinistro", {
      headers: auth(reader), data: {sinistroId: aSinistro.id}
    });
    expect(denied.status()).toBe(403);
    const deniedMcp = await mcp(reader, "link_perizia_sinistro", {
      periziaId: aUnlinked.id, sinistroId: aSinistro.id
    }, 1206);
    expect(deniedMcp).toContain('"isError":true');
    const anonymous = await request.get(api +
      "/api/sinistri/" + aSinistro.id + "/perizie");
    expect(anonymous.status()).toBe(401);

    // Rejected attempts did not create or change any association.
    const final = await request.get(api +
      "/api/sinistri/" + aSinistro.id + "/perizie", {headers: auth(a)});
    expect(await final.json()).toEqual([aPerizia.id]);
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
