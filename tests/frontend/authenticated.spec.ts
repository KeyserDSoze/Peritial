import { test, expect } from "@playwright/test";

test.describe("OAuth reale, REST e MCP su Docker Compose", () => {
  test.skip(!process.env.E2E_FULL_STACK, "Richiede la stack completa con Keycloak e PostgreSQL");

  test.setTimeout(90_000);

  test("workflow perizia UI: assign, advance, cancel con conferma e i18n", async ({page}) => {
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});

    const reference = "UI-WORKFLOW-" + Date.now() + "-" + Math.round(Math.random() * 100000);
    await page.locator("#reference").fill(reference);
    await page.getByRole("button", {name: "Crea"}).click();
    const row = page.locator("li").filter({hasText: reference});
    await expect(row).toBeVisible();
    await expect(row.getByText("Registrata")).toBeVisible();

    await row.getByRole("textbox", {name: "Assegnatario " + reference}).fill("ui-inspector");

    // An intercepted CAS conflict must never advance local status or pretend
    // that an audit-success was committed by the server.
    const assignPattern = "**/api/perizie/*/assign";
    await page.route(assignPattern, async route => {
      await route.fulfill({status: 409, contentType: "application/json", body: '{"error":"stale"}'});
    });
    await row.getByRole("button", {name: "Assegna"}).click();
    await expect(row.getByRole("alert")).toContainText("modificata");
    await expect(row.getByText("Registrata")).toBeVisible();
    await page.unroute(assignPattern);
    await row.getByRole("button", {name: "Ricarica stato"}).click();
    await expect(row.getByRole("alert")).toHaveCount(0);

    await row.getByRole("button", {name: "Assegna"}).click();
    await expect(row.getByText("Assegnata")).toBeVisible();
    await expect(row.getByText("ui-inspector")).toBeVisible();

    await page.getByRole("button", {name: "Lingua"}).click();
    await expect(row.getByText("Assigned")).toBeVisible();
    await page.getByRole("button", {name: "Lingua"}).click();

    await row.getByRole("button", {name: "Avanza"}).click();
    await expect(row.getByText("Da revisionare")).toBeVisible();

    await row.getByRole("button", {name: "Annulla"}).click();
    await expect(row.getByRole("button", {name: "Conferma annullamento"})).toBeVisible();
    await expect(row.getByText("Da revisionare")).toBeVisible();
    await row.getByRole("button", {name: "Conferma annullamento"}).click();
    await expect(row.getByText("Annullata")).toBeVisible();
    await expect(row.getByRole("button", {name: "Assegna"})).toHaveCount(0);
    await expect(row.getByRole("button", {name: "Avanza"})).toHaveCount(0);
    await expect(row.getByRole("button", {name: "Annulla"})).toHaveCount(0);
  });

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

    // REST and MCP share one idempotency ledger per signed tenant and subject.
    const retryKey = "retry-e2e-" + Date.now() + "-" + Math.round(Math.random() * 100000);
    const retryBytes = Buffer.from("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF\n");
    const retryFile = {
      file: { name: "retry-proof.pdf", mimeType: "application/pdf", buffer: retryBytes }
    };
    const firstProof = await request.post("http://localhost:8080/api/files", {
      headers: { Authorization: "Bearer " + accessToken, "Idempotency-Key": retryKey },
      multipart: retryFile
    });
    expect(firstProof.status(), await firstProof.text()).toBe(201);
    const originalId = (await firstProof.json() as {id: string}).id;
    expect(originalId).toBeTruthy();

    const restRetry = await request.post("http://localhost:8080/api/files", {
      headers: { Authorization: "Bearer " + accessToken, "Idempotency-Key": retryKey },
      multipart: retryFile
    });
    expect(restRetry.status(), await restRetry.text()).toBe(201);
    expect((await restRetry.json() as {id: string}).id).toBe(originalId);

    const mcpRetry = await request.post("http://localhost:8080/mcp", {
      headers: {
        Authorization: "Bearer " + accessToken,
        Accept: "application/json,text/event-stream",
        "Content-Type": "application/json"
      },
      data: {jsonrpc: "2.0", id: 48, method: "tools/call", params: {
        name: "ingest_file", arguments: {
          fileName: "retry-proof.pdf", contentType: "application/pdf",
          base64: retryBytes.toString("base64"), idempotencyKey: retryKey
        }
      }}
    });
    expect(mcpRetry.ok(), await mcpRetry.text()).toBeTruthy();
    expect(await mcpRetry.text()).toContain(originalId);
    expect(await mcpRetry.text()).not.toContain('"isError":true');

    const changedProof = await request.post("http://localhost:8080/api/files", {
      headers: { Authorization: "Bearer " + accessToken, "Idempotency-Key": retryKey },
      multipart: {
        file: { name: "retry-proof.pdf", mimeType: "application/pdf",
          buffer: Buffer.from("%PDF-1.7\nDifferent PDF bytes\n%%EOF\n") }
      }
    });
    expect(changedProof.status(), await changedProof.text()).toBe(409);

    // Safe EICAR test string is an entire stored file inside a minimal DOCX ZIP.
    // Appending it to a synthetic PDF is NOT reliable: ClamAV's PDF decoder
    // can legitimately skip bytes not referenced by the PDF structure.
    const infectedDocx = Buffer.from([
      "UEsDBBQAAAAAAHx0b10Uo0WfVAAAAFQAAAATAAAAW0NvbnRlbnRfVHlwZXNdLnhtbDxUeXBlcyB4bWxucz0iaHR0cDovL3NjaGVtYXMub3BlbnhtbGZvcm1h",
      "dHMub3JnL3BhY2thZ2UvMjAwNi9jb250ZW50LXR5cGVzIj48L1R5cGVzPlBLAwQUAAAAAAB8dG9dTNdhw2kAAABpAAAAEQAAAHdvcmQvZG9jdW1lbnQueG1s",
      "PHc6ZG9jdW1lbnQgeG1sbnM6dz0iaHR0cDovL3NjaGVtYXMub3BlbnhtbGZvcm1hdHMub3JnL3dvcmRwcm9jZXNzaW5nbWwvMjAwNi9tYWluIj48dzpib2R5",
      "Lz48L3c6ZG9jdW1lbnQ+UEsDBBQAAAAAAHx0b108z1FoRAAAAEQAAAAJAAAAZWljYXIuY29tWDVPIVAlQEFQWzRcUFpYNTQoUF4pN0NDKTd9JEVJQ0FSLVNU",
      "QU5EQVJELUFOVElWSVJVUy1URVNULUZJTEUhJEgrSCpQSwECFAMUAAAAAAB8dG9dFKNFn1QAAABUAAAAEwAAAAAAAAAAAAAAgAEAAAAAW0NvbnRlbnRfVHlw",
      "ZXNdLnhtbFBLAQIUAxQAAAAAAHx0b11M12HDaQAAAGkAAAARAAAAAAAAAAAAAACAAYUAAAB3b3JkL2RvY3VtZW50LnhtbFBLAQIUAxQAAAAAAHx0b108z1Fo",
      "RAAAAEQAAAAJAAAAAAAAAAAAAACAAR0BAABlaWNhci5jb21QSwUGAAAAAAMAAwC3AAAAiAEAAAAA"
    ].join(""), "base64");
    const blockedFile = await request.post("http://localhost:8080/api/files", {
      headers: { Authorization: "Bearer " + accessToken },
      multipart: {
        file: {
          name: "scan-test.docx",
          mimeType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
          buffer: infectedDocx
        }
      }
    });
    expect(blockedFile.status(), await blockedFile.text()).toBe(422);
    expect(await blockedFile.text()).not.toContain("EICAR-STANDARD-ANTIVIRUS-TEST-FILE");

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
