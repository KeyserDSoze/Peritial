import { test, expect } from "@playwright/test";

test.describe("OAuth reale, REST e MCP su Docker Compose", () => {
  test.skip(!process.env.E2E_FULL_STACK, "Richiede la stack completa con Keycloak e PostgreSQL");

  test.setTimeout(90_000);

  test("API down while browser stays online hides cases and blocks writes until retry", async ({page}) => {
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});

    const ref = "API-DOWN-" + Date.now() + "-" + Math.round(Math.random() * 100000);
    await page.locator("#reference").fill(ref);
    await page.getByRole("button", {name: "Crea"}).click();
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).getByText(ref, {exact: true})).toBeVisible();

    await page.route("**/api/perizie", async route => {
      if (route.request().method() === "GET") {
        await route.fulfill({status: 503, body: "Backend unavailable"});
      } else {
        await route.continue();
      }
    });
    await page.getByRole("button", {name: "Aggiorna elenco"}).click();
    await expect(page.getByRole("alert")).toContainText("Servizio perizie non disponibile");
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).getByText(ref, {exact: true})).toHaveCount(0);
    await expect(page.getByRole("button", {name: "Crea"})).toHaveCount(0);
    await expect(page.getByRole("button", {name: "Carica documento"})).toHaveCount(0);
    expect(await page.evaluate(() => navigator.onLine)).toBe(true);

    await page.unroute("**/api/perizie");
    await page.getByRole("button", {name: "Riprova"}).click();
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).getByText(ref, {exact: true})).toBeVisible();
    await expect(page.getByRole("button", {name: "Crea"})).toBeEnabled();

    // A browser may report online while DNS, proxy or origin connections fail.
    // This must follow the same fail-closed path as the HTTP 503 case.
    await page.route("**/api/perizie", async route => {
      if (route.request().method() === "GET") {
        await route.abort("failed");
      } else {
        await route.continue();
      }
    });
    await page.getByRole("button", {name: "Aggiorna elenco"}).click();
    await expect(page.getByRole("alert")).toContainText("Servizio perizie non disponibile");
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).getByText(ref, {exact: true})).toHaveCount(0);
    expect(await page.evaluate(() => navigator.onLine)).toBe(true);
    await page.unroute("**/api/perizie");
    await page.getByRole("button", {name: "Riprova"}).click();
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).getByText(ref, {exact: true})).toBeVisible();

    const otherRef = "API-DOWN-WRITE-" + Date.now();
    await page.route("**/api/perizie", async route => {
      if (route.request().method() === "POST") {
        await route.fulfill({status: 503, body: "Backend unavailable"});
      } else {
        await route.continue();
      }
    });
    await page.locator("#reference").fill(otherRef);
    await page.getByRole("button", {name: "Crea"}).click();
    await expect(page.getByRole("alert")).toContainText("Servizio perizie non disponibile");
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).getByText(ref, {exact: true})).toHaveCount(0);
    await expect(page.getByRole("button", {name: "Crea"})).toHaveCount(0);
    expect(await page.evaluate(() => navigator.onLine)).toBe(true);

    await page.unroute("**/api/perizie");
    await page.getByRole("button", {name: "Riprova"}).click();
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).getByText(ref, {exact: true})).toBeVisible();
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).getByText(otherRef, {exact: true})).toHaveCount(0);
  });

  test("a stale GET never restores protected rows after a concurrent failed mutation", async ({page}) => {
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});

    const ref = "API-RACE-" + Date.now() + "-" + Math.round(Math.random() * 100000);
    await page.locator("#reference").fill(ref);
    await page.getByRole("button", {name: "Crea"}).click();
    const row = page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).locator("li").filter({hasText: ref});
    await expect(row).toBeVisible();
    await row.getByRole("textbox", {name: "Assegnatario " + ref}).fill("race-assignee");

    let failMutation: () => void = () => undefined;
    let completeOldRead: () => void = () => undefined;
    let markReadStarted: () => void = () => undefined;
    const mutationGate = new Promise<void>(resolve => {failMutation = resolve;});
    const readGate = new Promise<void>(resolve => {completeOldRead = resolve;});
    const readStarted = new Promise<void>(resolve => {markReadStarted = resolve;});
    await page.route("**/api/perizie/*/assign", async route => {
      await mutationGate;
      await route.fulfill({status: 503, body: "API unavailable"});
    });
    await page.route("**/api/perizie", async route => {
      if (route.request().method() === "GET") {
        markReadStarted();
        await readGate;
      }
      await route.continue();
    });

    await row.getByRole("button", {name: "Assegna"}).click();
    await page.getByRole("button", {name: "Aggiorna elenco"}).click();
    await readStarted; // Guarantee the GET captured the pre-failure epoch.
    failMutation();
    await expect(page.getByRole("alert")).toContainText("Servizio perizie non disponibile");

    // The GET began before the failed POST. Completing it afterwards must
    // NOT re-enable mutations or display possibly stale tenant records.
    const oldRead = page.waitForResponse(response =>
      response.url().endsWith("/api/perizie") && response.request().method() === "GET");
    completeOldRead();
    const oldResponse = await oldRead;
    await oldResponse.finished();
    await expect(page.getByRole("button", {name: "Riprova"})).toBeVisible();
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).getByText(ref, {exact: true})).toHaveCount(0);
    await expect(page.getByRole("button", {name: "Crea"})).toHaveCount(0);

    await page.unroute("**/api/perizie");
    await page.unroute("**/api/perizie/*/assign");
    await page.getByRole("button", {name: "Riprova"}).click();
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).getByText(ref, {exact: true})).toBeVisible();
    await expect(row.getByText("Registrata")).toBeVisible();
  });

  test("PWA lists safe document metadata after an authenticated upload", async ({page}) => {
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});

    const name = "pwa-catalog-" + Date.now() + ".pdf";
    const pdf = Buffer.from("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF\n");
    await page.locator("#file").setInputFiles({
      name, mimeType: "application/pdf", buffer: pdf
    });
    const upload = page.waitForResponse(response =>
      response.url().endsWith("/api/files") && response.request().method() === "POST");
    await page.getByRole("button", {name: "Carica documento"}).click();
    const response = await upload;
    expect(response.status(), await response.text()).toBe(201);

    const documentList = page.getByRole("list", {name: "Documenti caricati"});
    await expect(documentList.getByText(name, {exact: true})).toBeVisible();
    const row = documentList.locator("li").filter({hasText: name});
    await expect(row).toContainText(String(pdf.length));
    // The PWA exposes metadata only: no download, object-storage link or bytes.
    await expect(row.locator("a,button")).toHaveCount(0);

    await page.getByRole("button", {name: "Aggiorna elenco"}).click();
    await expect(documentList.getByText(name, {exact: true})).toBeVisible();
    await expect(row.locator("a,button")).toHaveCount(0);
  });

  test("PWA collega un upload alla perizia scelta senza esporre chiavi storage", async ({page}) => {
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});

    const reference = "DOC-LINK-" + Date.now() + "-" + Math.round(Math.random() * 100000);
    const create = page.waitForResponse(response =>
      response.url().endsWith("/api/perizie") && response.request().method() === "POST");
    await page.locator("#reference").fill(reference);
    await page.getByRole("button", {name: "Crea"}).click();
    const created = await create;
    expect(created.status(), await created.text()).toBe(201);
    const perizia = await created.json() as {id: string};

    const selector = page.getByRole("combobox", {name: "Perizia collegata"});
    await expect(selector).toBeVisible();
    await expect(selector).toHaveValue("");
    await selector.selectOption({label: reference});

    const name = "pwa-linked-" + Date.now() + ".pdf";
    const pdf = Buffer.from("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF\n");
    await page.locator("#file").setInputFiles({
      name, mimeType: "application/pdf", buffer: pdf
    });
    const upload = page.waitForResponse(response =>
      response.url().endsWith("/api/files") && response.request().method() === "POST");
    await page.getByRole("button", {name: "Carica documento"}).click();
    const uploaded = await upload;
    expect(uploaded.status(), await uploaded.text()).toBe(201);
    const record = await uploaded.json() as {periziaId: string | null};
    expect(record.periziaId).toBe(perizia.id);

    const docs = page.getByRole("list", {name: "Documenti caricati"});
    const row = docs.locator("li").filter({hasText: name});
    await expect(row).toContainText(reference);
    await expect(row.locator("a,button")).toHaveCount(0);
  });

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
    const row = page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).locator("li").filter({hasText: reference});
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
    await expect(row.getByText("Assegnata", {exact: true})).toBeVisible();
    await expect(row.getByText("ui-inspector")).toBeVisible();

    await page.getByRole("button", {name: "Lingua"}).click();
    await expect(row.getByText("Assigned")).toBeVisible();
    await page.getByRole("button", {name: "Language"}).click();

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

  test("offline dopo login non espone pratiche né mutazioni e ricarica online da REST", async ({page, context}) => {
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});

    const reference = "OFFLINE-REFETCH-" + Date.now() + "-" + Math.round(Math.random() * 100000);
    await page.locator("#reference").fill(reference);
    await page.getByRole("button", {name: "Crea"}).click();
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).getByText(reference)).toBeVisible();

    // This test uses the built nginx-hosted PWA, not the Vite dev server.
    // Workbox may precache the public shell only, never API/MCP or tenant
    // data. Explicitly check caches after a real authenticated API write.
    const cachedPaths = await page.evaluate(async () => {
      await navigator.serviceWorker.ready;
      const names = await caches.keys();
      const entries = await Promise.all(names.map(async name => (await caches.open(name)).keys()));
      return entries.flat().map(entry => new URL(entry.url).pathname);
    });
    expect(cachedPaths.length, "production PWA must precache its public shell").toBeGreaterThan(0);
    expect(cachedPaths.filter(path => /^\/(?:api|mcp|realms|\.well-known)(?:\/|$)/.test(path)))
      .toEqual([]);

    await context.setOffline(true);
    const offlineApiResult = await page.evaluate(async () => {
      try {
        const response = await fetch("/api/perizie");
        return response.status;
      } catch {
        return 0; // Expected: no network, no SW runtime API fallback.
      }
    });
    expect(offlineApiResult, "offline API must not be answered with cached data").toBe(0);
    await expect(page.getByRole("status")).toContainText("offline");
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).getByText(reference)).toHaveCount(0);
    await expect(page.getByRole("button", {name: "Crea"})).toHaveCount(0);
    await expect(page.getByRole("button", {name: "Carica documento"})).toHaveCount(0);

    await context.setOffline(false);
    await expect(page.getByRole("status")).toHaveCount(0);
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).getByText(reference)).toBeVisible({timeout: 30_000});
    await expect(page.getByRole("button", {name: "Crea"})).toBeEnabled();
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
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).getByText(reference)).toBeVisible({timeout: 30_000});

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
