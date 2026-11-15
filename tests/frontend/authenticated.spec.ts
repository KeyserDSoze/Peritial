import { test, expect } from "@playwright/test";

test.describe("OAuth reale, REST e MCP su Docker Compose", () => {
  test.skip(!process.env.E2E_FULL_STACK, "Richiede la stack completa con Keycloak e PostgreSQL");

  test.setTimeout(90_000);

  test("un token JWT del tenant permette REST e discovery MCP, ma MCP anonimo è negato", async ({page, request}) => {
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});

    const reference = "E2E-" + Date.now().toString() + "-" + Math.round(Math.random() * 10000);
    await page.locator("#reference").fill(reference);
    const creation = page.waitForResponse(response =>
      response.url().endsWith("/api/perizie") && response.request().method() === "POST");
    await page.getByRole("button", {name: "Crea"}).click();
    const created = await creation;
    expect(created.status(), await created.text()).toBe(201);
    await expect(page.getByText(reference)).toBeVisible({timeout: 30_000});

    const accessToken = await page.evaluate(() => {
      const key = Object.keys(sessionStorage).find(k => k.startsWith("oidc.user:"));
      if (!key) return null;
      const user = JSON.parse(sessionStorage.getItem(key) || "{}") as {access_token?: string};
      return user.access_token || null;
    });
    expect(accessToken).toBeTruthy();
    const signedClaims = JSON.parse(Buffer.from(accessToken!.split(".")[1], "base64url").toString("utf-8")) as {sub?: string};
    expect(signedClaims.sub, "PKCE access token must include signed sub claim").toBeTruthy();

    // A successful MCP mutation must be audited through the same Business layer as REST.
    const mcpReference = "E2E-MCP-" + Date.now() + "-" + Math.round(Math.random() * 100000);
    const mcpCreated = await request.post("http://localhost:8080/mcp", {
      headers: {
        Authorization: "Bearer " + accessToken,
        Accept: "application/json,text/event-stream",
        "Content-Type": "application/json"
      },
      data: {jsonrpc: "2.0", id: 44, method: "tools/call",
        params: {name: "create_perizia", arguments: {reference: mcpReference}}}
    });
    expect(mcpCreated.ok(), await mcpCreated.text()).toBeTruthy();
    expect(await mcpCreated.text()).toContain(mcpReference);
    expect(await mcpCreated.text()).not.toContain('"isError":true');

    // Actual REST multipart ingestion uses the same OAuth identity as the UI and MCP.
    const badFile = await request.post("http://localhost:8080/api/files", {
      headers: { Authorization: "Bearer " + accessToken },
      multipart: { file: { name: "fake.pdf", mimeType: "application/pdf", buffer: Buffer.from("this is not a PDF") } }
    });
    expect(badFile.status(), await badFile.text()).toBe(400);

    const pdf = Buffer.from("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF\n");
    const goodFile = await request.post("http://localhost:8080/api/files", {
      headers: { Authorization: "Bearer " + accessToken },
      multipart: { file: { name: "valid.pdf", mimeType: "application/pdf", buffer: pdf } }
    });
    expect(goodFile.status(), await goodFile.text()).toBe(201);
    expect((await goodFile.json() as {fileName: string}).fileName).toBe("valid.pdf");

    // EICAR is a harmless industry-standard malware-test signature. Even with
    // a valid PDF prefix and a signed write token it MUST never enter storage.
    const eicar = "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";
    const infectedPdf = Buffer.concat([
      Buffer.from("%PDF-1.7\n"), Buffer.from(eicar, "ascii"), Buffer.from("\n%%EOF\n")
    ]);
    const blockedFile = await request.post("http://localhost:8080/api/files", {
      headers: { Authorization: "Bearer " + accessToken },
      multipart: { file: { name: "scan-test.pdf", mimeType: "application/pdf", buffer: infectedPdf } }
    });
    expect(blockedFile.status(), await blockedFile.text()).toBe(422);
    expect(await blockedFile.text()).not.toContain(eicar);

    // Body-size enforcement is performed by ASP.NET, not only by the business service.
    const tooLarge = await request.post("http://localhost:8080/api/files", {
      headers: { Authorization: "Bearer " + accessToken },
      multipart: {
        file: { name: "oversized.pdf", mimeType: "application/pdf",
          buffer: Buffer.concat([pdf, Buffer.alloc(25 * 1024 * 1024)]) }
      }
    });
    expect(tooLarge.status()).toBe(413);

    const invalidMcp = await request.post("http://localhost:8080/mcp", {
      headers: {
        Authorization: "Bearer " + accessToken,
        Accept: "application/json,text/event-stream",
        "Content-Type": "application/json"
      },
      data: {
        jsonrpc: "2.0", id: 22, method: "tools/call",
        params: { name: "ingest_file", arguments: {
          fileName: "spoof.pdf", contentType: "application/pdf",
          base64: Buffer.from("not really a PDF").toString("base64")
        } }
      }
    });
    expect(invalidMcp.ok(), await invalidMcp.text()).toBeTruthy();
    expect(await invalidMcp.text()).toContain('"isError":true');



    const unauthenticated = await request.post("http://localhost:8080/mcp", {
      headers: {"Accept": "application/json,text/event-stream", "Content-Type": "application/json"},
      data: {jsonrpc: "2.0", id: 1, method: "tools/list"}
    });
    expect(unauthenticated.status()).toBe(401);

    const response = await request.post("http://localhost:8080/mcp", {
      headers: {
        Authorization: "Bearer " + accessToken,
        "Accept": "application/json,text/event-stream",
        "Content-Type": "application/json"
      },
      data: {jsonrpc: "2.0", id: 2, method: "tools/list"}
    });
    expect(response.ok(), await response.text()).toBeTruthy();
    expect(await response.text()).toContain("list_perizie");
    expect(await response.text()).toContain("ingest_file");
  });
});
