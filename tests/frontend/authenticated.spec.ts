import { test, expect } from "@playwright/test";

test.describe("OAuth reale, REST e MCP su Docker Compose", () => {
  test.skip(!process.env.E2E_FULL_STACK, "Richiede la stack completa con Keycloak e PostgreSQL");

  test.setTimeout(90_000);

  test("stale failed create and upload cannot invalidate newer signed document refreshes", async ({page}) => {
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});

    const seed = "STALE-CREATE-SEED-" + Date.now() + "-" + Math.round(Math.random() * 100000);
    await page.locator("#reference").fill(seed);
    await page.getByRole("button", {name: "Crea", exact: true}).click();
    const row = page.locator("[data-perizia-id]").filter({hasText: seed});
    await expect(row).toBeVisible();
    const id = await row.getAttribute("data-perizia-id");
    expect(id).toBeTruthy();

    let requestStarted!: () => void;
    const started = new Promise<void>(resolve => { requestStarted = resolve; });
    let releaseResponse!: () => void;
    const release = new Promise<void>(resolve => { releaseResponse = resolve; });
    await page.route("**/api/perizie", async route => {
      if (route.request().method() !== "POST") return route.continue();
      requestStarted();
      await release;
      await route.fulfill({status: 503, body: "Simulated unavailable backend"});
    });

    await page.locator("#reference").fill("STALE-FAIL-" + Date.now());
    await page.getByRole("button", {name: "Crea", exact: true}).click();
    await started;
    // The document filter is still usable while POST is in flight.
    // Its authenticated GET supersedes the old request epoch.
    await page.locator("#documents-perizia").selectOption(id!);
    await expect(page.locator("#documents-perizia")).toHaveValue(id!);
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).getByText(seed, {exact: true})).toBeVisible();

    const lateFailure = page.waitForResponse(r =>
      r.url().endsWith("/api/perizie") && r.request().method() === "POST" && r.status() === 503);
    releaseResponse();
    await lateFailure;
    // The old 503 must not erase records fetched by the newer signed GET.
    // The existing active-503 test separately proves fail-closed behavior.
    await expect(page.getByRole("button", {name: "Crea", exact: true})).toBeEnabled();
    await expect(page.locator("#documents-perizia")).toHaveValue(id!);
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).getByText(seed, {exact: true})).toBeVisible();
    await expect(page.getByRole("button", {name: "Riprova"})).toHaveCount(0);
    // Uploads share the same parent epoch: a late failed file POST must not
    // invalidate a newer authenticated document projection either.
    let uploadStarted!: () => void;
    const uploading = new Promise<void>(resolve => { uploadStarted = resolve; });
    let releaseUpload!: () => void;
    const uploadGate = new Promise<void>(resolve => { releaseUpload = resolve; });
    await page.route("**/api/files", async route => {
      if (route.request().method() !== "POST") return route.continue();
      uploadStarted();
      await uploadGate;
      await route.fulfill({status: 503, body: "Simulated unavailable backend"});
    });
    await page.locator("#file").setInputFiles({
      name: "stale-upload.pdf", mimeType: "application/pdf",
      buffer: Buffer.from("%PDF-1.4\\n")
    });
    await page.getByRole("button", {name: "Carica documento"}).click();
    await uploading;
    await page.locator("#documents-perizia").selectOption("");
    await expect(page.locator("#documents-perizia")).toHaveValue("");
    const lateUploadFailure = page.waitForResponse(r =>
      r.url().endsWith("/api/files") && r.request().method() === "POST" && r.status() === 503);
    releaseUpload();
    await lateUploadFailure;
    await expect(page.getByRole("button", {name: "Carica documento"})).toBeEnabled();
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/}).getByText(seed, {exact: true})).toBeVisible();
    await expect(page.getByRole("button", {name: "Riprova"})).toHaveCount(0);
  });

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

  test("workflow ignores delayed mutation replies after authenticated list refresh", async ({page}) => {
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});

    const reference = "UI-STALE-WORKFLOW-" + Date.now() + "-" + Math.round(Math.random() * 100000);
    await page.locator("#reference").fill(reference);
    await page.getByRole("button", {name: "Crea"}).click();
    const row = page.getByRole("list", {name: /^(Perizie|Appraisals)$/})
      .locator("li").filter({hasText: reference});
    await expect(row.getByText("Registrata")).toBeVisible();

    const assignPattern = "**/api/perizie/*/assign";
    let releaseFailure: () => void = () => undefined;
    let markFailureStarted: () => void = () => undefined;
    const failureGate = new Promise<void>(resolve => {releaseFailure = resolve;});
    const failureStarted = new Promise<void>(resolve => {markFailureStarted = resolve;});
    await page.route(assignPattern, async route => {
      markFailureStarted();
      await failureGate;
      await route.fulfill({status: 503, body: "Temporary service outage"});
    });
    await row.getByRole("textbox", {name: "Assegnatario " + reference}).fill("old-request");
    await row.getByRole("button", {name: "Assegna"}).click();
    await failureStarted;

    // A fresh signed GET remounts the workflow while the old POST is
    // still in flight. The former component cannot revoke the newly
    // verified healthy view if its old 503 arrives after the refresh.
    await page.getByRole("button", {name: "Aggiorna elenco"}).click();
    await expect(row.getByText("Registrata")).toBeVisible();
    const failedReply = page.waitForResponse(response =>
      response.url().includes("/api/perizie/") &&
      response.url().endsWith("/assign") && response.status() === 503);
    releaseFailure();
    await (await failedReply).finished();
    await page.waitForTimeout(250);
    await expect(page.getByRole("button", {name: "Riprova"})).toHaveCount(0);
    await expect(row.getByText("Registrata")).toBeVisible();
    // A remounted workflow intentionally clears the old assignee value.
    // The textbox is available, but Submit remains disabled until the
    // user enters a new assignee with the newly verified signed view.
    await expect(row.getByRole("textbox", {name: "Assegnatario " + reference})).toBeEnabled();
    await expect(row.getByRole("button", {name: "Assegna"})).toBeDisabled();
    await page.unroute(assignPattern);

    // A delayed genuine 200 is also from the old workflow, not a
    // license to overwrite the new projection after an intervening
    // authenticated refresh. The next explicit GET reconciles it.
    let releaseSuccess: () => void = () => undefined;
    let markSuccessStarted: () => void = () => undefined;
    const successGate = new Promise<void>(resolve => {releaseSuccess = resolve;});
    const successStarted = new Promise<void>(resolve => {markSuccessStarted = resolve;});
    await page.route(assignPattern, async route => {
      markSuccessStarted();
      await successGate;
      await route.continue();
    });
    await row.getByRole("textbox", {name: "Assegnatario " + reference}).fill("confirmed-writer");
    await row.getByRole("button", {name: "Assegna"}).click();
    await successStarted;
    await page.getByRole("button", {name: "Aggiorna elenco"}).click();
    await expect(row.getByText("Registrata")).toBeVisible();
    const succeededReply = page.waitForResponse(response =>
      response.url().includes("/api/perizie/") &&
      response.url().endsWith("/assign") && response.status() === 200);
    releaseSuccess();
    await (await succeededReply).finished();
    await page.waitForTimeout(250);
    await expect(row.getByText("Registrata")).toBeVisible();
    await expect(row.getByText("Assegnata", {exact: true})).toHaveCount(0);
    await page.unroute(assignPattern);

    await page.getByRole("button", {name: "Aggiorna elenco"}).click();
    await expect(row.getByText("Assegnata", {exact: true})).toBeVisible();
    await expect(row.getByText("confirmed-writer", {exact: true})).toBeVisible();
  });

  test("active workflow 503 fails closed while a stale response after refresh is ignored", async ({page}) => {
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});

    const ref = "API-ACTIVE-POST-DOWN-" + Date.now() + "-" + Math.round(Math.random() * 100000);
    await page.locator("#reference").fill(ref);
    await page.getByRole("button", {name: "Crea"}).click();
    const row = page.getByRole("list", {name: /^(Perizie|Appraisals)$/})
      .locator("li").filter({hasText: ref});
    await expect(row.getByText("Registrata")).toBeVisible();

    // Unlike a request from a component already replaced by a fresh signed
    // GET (tested above), a 503 delivered to the ACTIVE workflow must
    // lock the entire protected view and require an explicit retry.
    const assignPattern = "**/api/perizie/*/assign";
    await page.route(assignPattern, async route => {
      await route.fulfill({status: 503, body: "API unavailable"});
    });
    await row.getByRole("textbox", {name: "Assegnatario " + ref})
      .fill("never-committed");
    await row.getByRole("button", {name: "Assegna"}).click();
    await expect(page.getByRole("alert")).toContainText("Servizio perizie non disponibile");
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/})
      .getByText(ref, {exact: true})).toHaveCount(0);
    await expect(page.getByRole("button", {name: "Crea"})).toHaveCount(0);

    await page.unroute(assignPattern);
    await page.getByRole("button", {name: "Riprova"}).click();
    await expect(row.getByText("Registrata")).toBeVisible();
    await expect(row.getByText("never-committed")).toHaveCount(0);
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

  test("PWA filtra i documenti per perizia tramite GET tenant-scoped e non riusa la lista precedente", async ({page}) => {
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});

    const stamp = Date.now() + "-" + Math.round(Math.random() * 100000);
    async function createCase(reference: string): Promise<string> {
      const responsePromise = page.waitForResponse(response =>
        response.url().endsWith("/api/perizie") && response.request().method() === "POST");
      await page.locator("#reference").fill(reference);
      await page.getByRole("button", {name: "Crea"}).click();
      const response = await responsePromise;
      expect(response.status(), await response.text()).toBe(201);
      return ((await response.json()) as {id: string}).id;
    }

    const firstId = await createCase("FILTER-A-" + stamp);
    const secondId = await createCase("FILTER-B-" + stamp);
    const pdf = Buffer.from("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF\n");
    async function uploadFor(id: string, name: string) {
      await page.getByRole("combobox", {name: "Perizia collegata"}).selectOption(id);
      await page.locator("#file").setInputFiles({name, mimeType: "application/pdf", buffer: pdf});
      const responsePromise = page.waitForResponse(response =>
        response.url().endsWith("/api/files") && response.request().method() === "POST");
      await page.getByRole("button", {name: "Carica documento"}).click();
      const response = await responsePromise;
      expect(response.status(), await response.text()).toBe(201);
      const list = page.getByRole("list", {name: "Documenti caricati"});
      await expect(list.getByText(name, {exact: true})).toBeVisible();
    }

    const firstName = "first-filter-" + stamp + ".pdf";
    const secondName = "second-filter-" + stamp + ".pdf";
    await uploadFor(firstId, firstName);
    await uploadFor(secondId, secondName);

    const list = page.getByRole("list", {name: "Documenti caricati"});
    await expect(list.getByText(firstName, {exact: true})).toBeVisible();
    await expect(list.getByText(secondName, {exact: true})).toBeVisible();

    const filter = page.getByRole("combobox", {name: "Filtra documenti per perizia"});
    const firstGet = page.waitForResponse(response =>
      response.request().method() === "GET" &&
      new URL(response.url()).pathname === "/api/files/page" &&
      new URL(response.url()).searchParams.get("periziaId") === firstId);
    await filter.selectOption(firstId);
    expect((await firstGet).status()).toBe(200);
    await expect(list.getByText(firstName, {exact: true})).toBeVisible();
    await expect(list.getByText(secondName, {exact: true})).toHaveCount(0);

    const secondGet = page.waitForResponse(response =>
      response.request().method() === "GET" &&
      new URL(response.url()).pathname === "/api/files/page" &&
      new URL(response.url()).searchParams.get("periziaId") === secondId);
    await filter.selectOption(secondId);
    expect((await secondGet).status()).toBe(200);
    await expect(list.getByText(firstName, {exact: true})).toHaveCount(0);
    await expect(list.getByText(secondName, {exact: true})).toBeVisible();
    await expect(list.locator("a,button")).toHaveCount(0);

    await page.getByRole("button", {name: "Lingua"}).click();
    await expect(page.getByRole("combobox", {name: "Filter documents by appraisal"})).toHaveValue(secondId);
    await page.getByRole("button", {name: "Language"}).click();

    await filter.selectOption("");
    await expect(list.getByText(firstName, {exact: true})).toBeVisible();
    await expect(list.getByText(secondName, {exact: true})).toBeVisible();
  });

  test("PWA progressively loads document metadata and rejects stale continuation", async ({page}) => {
    const firstId = "00000000-0000-4000-8000-000000000101";
    const secondId = "00000000-0000-4000-8000-000000000102";
    let rejectContinuation = false;
    await page.route("**/api/files/page?**", async route => {
      const cursor = new URL(route.request().url()).searchParams.get("cursor");
      if (cursor && rejectContinuation) {
        await route.fulfill({status: 400, contentType: "application/json",
          body: '{"error":"stale filter"}'});
        return;
      }
      const record = (id: string, name: string) => ({
        id, periziaId: null, fileName: name, contentType: "application/pdf",
        length: 21, createdAtUtc: "2026-11-19T20:00:00Z"
      });
      await route.fulfill({status: 200, contentType: "application/json",
        body: JSON.stringify(cursor
          ? {items: [record(secondId, "page-two.pdf")], nextCursor: null}
          : {items: [record(firstId, "page-one.pdf")], nextCursor: "opaque-test-cursor"})});
    });
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});

    const docs = page.getByRole("list", {name: "Documenti caricati"});
    await expect(docs.getByText("page-one.pdf")).toBeVisible();
    const more = page.getByRole("button", {name: "Carica altri documenti"});
    await expect(more).toBeVisible();
    await more.click();
    await expect(docs.getByText("page-two.pdf")).toBeVisible();
    await expect(docs.getByText("page-one.pdf")).toBeVisible();
    await expect(more).toHaveCount(0);
    await expect(docs.locator("a,button")).toHaveCount(0);

    // Refresh invalidates old pages and returns to page one.
    await page.getByRole("button", {name: "Aggiorna elenco"}).click();
    await expect(docs.getByText("page-one.pdf")).toBeVisible();
    await expect(docs.getByText("page-two.pdf")).toHaveCount(0);
    rejectContinuation = true;
    await more.click();
    await expect(page.getByRole("alert")).toContainText("Impossibile continuare");
    await expect(docs.getByText("page-one.pdf")).toHaveCount(0);
    await expect(more).toHaveCount(0);
  });

  test("PWA creates and lists tenant Sinistri with signed JWT, persisted refresh and no offline writes", async ({page, request, context}) => {
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});

    const token = await page.evaluate(() => {
      const key = Object.keys(sessionStorage).find(x => x.startsWith("oidc.user:"));
      return key ? (JSON.parse(sessionStorage.getItem(key) || "{}") as {access_token?:string}).access_token : undefined;
    });
    expect(token).toBeTruthy();
    const reference = "UI-SIN-CREATE-" + Date.now() + "-" + Math.floor(Math.random() * 100000);
    const catalog = page.getByRole("region", {name: "Catalogo sinistri"});
    await expect(page.getByRole("button", {name: "Apri catalogo sinistri"})).toBeVisible();
    await expect(page.getByRole("button", {name: "Crea sinistro"})).toHaveCount(0);
    await page.getByRole("button", {name: "Apri catalogo sinistri"}).click();
    await expect(catalog.getByRole("heading", {name: "Catalogo sinistri"})).toBeVisible();
    await expect(catalog.getByRole("textbox", {name: "Riferimento sinistro"})).toBeVisible();
    const referenceInput = catalog.getByRole("textbox", {name: "Riferimento sinistro"});
    const occurred = catalog.getByLabel("Data sinistro");
    const reported = catalog.getByLabel("Data denuncia");
    await referenceInput.fill(reference);
    await occurred.fill("2099-11-17");
    await reported.fill("2099-11-18");

    // A failed create must never appear as a successful local tenant record.
    await page.route("**/api/sinistri", async route => {
      if (route.request().method() === "POST") {
        await route.fulfill({status: 400, contentType: "application/json", body:'{"error":"invalid"}'});
      } else await route.continue();
    });
    await catalog.getByRole("button", {name: "Crea sinistro"}).click();
    await expect(catalog.getByRole("alert")).toContainText("Impossibile creare");
    await expect(catalog.getByText(reference, {exact:true})).toHaveCount(0);
    await page.unroute("**/api/sinistri");

    const created = page.waitForResponse(response => response.url().endsWith("/api/sinistri") &&
      response.request().method() === "POST");
    await catalog.getByRole("button", {name: "Crea sinistro"}).click();
    const result = await created;
    expect(result.status(), await result.text()).toBe(201);
    expect(result.headers()["cache-control"]).toContain("no-store");
    const sinistro = await result.json() as {id:string; externalReference:string; occurredOn:string; reportedOn:string; version:number};
    expect(sinistro.externalReference).toBe(reference);
    expect(sinistro.occurredOn).toBe("2099-11-17");
    expect(sinistro.reportedOn).toBe("2099-11-18");
    expect(sinistro.version).toBe(1);
    expect(await result.text()).not.toContain("tenantId");
    await expect(catalog.getByText(reference, {exact:true})).toBeVisible();
    await expect(referenceInput).toHaveValue("");

    const persisted = await request.get("http://localhost:8080/api/sinistri/" + sinistro.id,
      {headers: {Authorization: "Bearer " + token}});
    expect(persisted.status(), await persisted.text()).toBe(200);
    expect((await persisted.json() as {externalReference:string}).externalReference).toBe(reference);

    await page.reload();
    await expect(page.getByRole("button", {name:"Apri catalogo sinistri"})).toBeVisible({timeout:30_000});
    await page.getByRole("button", {name:"Apri catalogo sinistri"}).click();
    await expect(page.getByRole("region", {name:"Catalogo sinistri"})
      .getByText(reference, {exact:true})).toBeVisible({timeout:30_000});

    // The browser's offline state must hide this catalog and all write controls.
    await context.setOffline(true);
    await expect(page.getByRole("status")).toContainText("Sei offline");
    await expect(page.getByRole("region", {name:"Catalogo sinistri"})).toHaveCount(0);
    await expect(page.getByRole("button", {name:"Crea sinistro"})).toHaveCount(0);
    await context.setOffline(false);
    await expect(page.getByRole("button", {name:"Apri catalogo sinistri"})).toBeVisible({timeout:30_000});
    await page.getByRole("button", {name:"Apri catalogo sinistri"}).click();
    await expect(page.getByRole("region", {name:"Catalogo sinistri"})
      .getByText(reference, {exact:true})).toBeVisible({timeout:30_000});
  });

  test("PWA searches complete tenant Sinistri catalog by literal prefix and inclusive incident dates", async ({page, request, context}) => {
    test.setTimeout(120_000);
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});

    const token = await page.evaluate(() => {
      const key = Object.keys(sessionStorage).find(x => x.startsWith("oidc.user:"));
      return key ? (JSON.parse(sessionStorage.getItem(key) || "{}") as {access_token?:string}).access_token : undefined;
    });
    expect(token).toBeTruthy();
    const prefix = "UI-SIN-SEARCH-" + Date.now() + "-%_";
    const included = prefix + "-IN";
    const otherDate = prefix + "-OTHER-DATE";
    const wildcardDecoy = prefix.slice(0, -2) + "AB-DECOY";
    const headers = {Authorization: "Bearer " + token};
    for (const [ref, date] of [[included, "1981-01-10"], [otherDate, "1982-02-10"], [wildcardDecoy, "1981-01-10"]]) {
      const response = await request.post("http://localhost:8080/api/sinistri", {
        headers, data: {externalReference:ref, occurredOn:date}
      });
      expect(response.status(), await response.text()).toBe(201);
    }

    await page.getByRole("button", {name: "Apri catalogo sinistri"}).click();
    const catalog = page.getByRole("region", {name: "Catalogo sinistri"});
    // Verify the browser issues an authenticated server search, rather
    // than filtering whichever cases happen to fit the recent-50 window.
    await catalog.getByRole("textbox", {name: "Prefisso riferimento catalogo"}).fill(prefix);
    await catalog.getByLabel("Data evento da").fill("1981-01-10");
    await catalog.getByLabel("Data evento a").fill("1981-12-31");
    const searched = page.waitForResponse(response =>
      response.url().includes("/api/sinistri/search/page?") && response.status() === 200);
    await catalog.getByRole("button", {name: "Cerca nel catalogo"}).click();
    const response = await searched;
    const url = new URL(response.url());
    expect(url.searchParams.get("prefix")).toBe(prefix);
    expect(url.searchParams.get("occurredFrom")).toBe("1981-01-10");
    expect(url.searchParams.get("occurredTo")).toBe("1981-12-31");
    expect(url.searchParams.get("limit")).toBe("50");
    expect(response.headers()["cache-control"]).toContain("no-store");
    await expect(catalog.getByText(included, {exact:true})).toBeVisible();
    await expect(catalog.getByText(otherDate, {exact:true})).toHaveCount(0);
    await expect(catalog.getByText(wildcardDecoy, {exact:true})).toHaveCount(0);
    await catalog.getByRole("button", {name: "Aggiorna sinistri"}).click();
    await expect(catalog.getByText(included, {exact:true})).toBeVisible();

    // Creating a record while a search is applied must not put an unrelated
    // confirmed row into the filtered window. The receipt is still valid.
    const outside = "UI-SIN-OUTSIDE-" + Date.now();
    await catalog.getByRole("textbox", {name:"Riferimento sinistro"}).fill(outside);
    await catalog.getByLabel("Data sinistro").fill("2097-05-02");
    const post = page.waitForResponse(response =>
      response.url().endsWith("/api/sinistri") &&
      response.request().method() === "POST" && response.status() === 201);
    const refreshedMatches = page.waitForResponse(response =>
      response.url().includes("/api/sinistri/search/page?") && response.status() === 200);
    await catalog.getByRole("button", {name:"Crea sinistro"}).click();
    await post;
    await refreshedMatches;
    await expect(catalog.getByText("Sinistro creato e salvato.")).toBeVisible();
    await expect(catalog.getByRole("list", {name:"Catalogo sinistri"})
      .getByText(outside, {exact:true})).toHaveCount(0);
    await expect(catalog.getByRole("list", {name:"Catalogo sinistri"})
      .getByText(included, {exact:true})).toBeVisible();

    const defaultFetch = page.waitForResponse(response =>
      response.url().endsWith("/api/sinistri/page?limit=50") && response.status() === 200);
    await catalog.getByRole("button", {name: "Cancella filtri sinistri"}).click();
    await defaultFetch;
    await expect(catalog.getByRole("textbox", {name: "Prefisso riferimento catalogo"})).toHaveValue("");
    await expect(catalog.getByText("Risultati server per il prefisso:")).toHaveCount(0);
    await context.setOffline(true);
    await expect(page.getByRole("region", {name: "Catalogo sinistri"})).toHaveCount(0);
    await context.setOffline(false);
    await expect(page.getByRole("button", {name: "Apri catalogo sinistri"})).toBeVisible({timeout:30_000});
    await page.getByRole("button", {name: "Apri catalogo sinistri"}).click();
    await expect(page.getByRole("region", {name:"Catalogo sinistri"})
      .getByRole("textbox", {name:"Prefisso riferimento catalogo"})).toHaveValue("");
  });

  test("Filtered Sinistri pages REST and MCP honor literal prefix, inclusive dates and filter-bound cursors", async ({page, request}) => {
    test.setTimeout(120_000);
    await page.goto("/");
    await page.getByRole("button", {name:"Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout:30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name:"Esci"})).toBeVisible({timeout:30_000});
    const token = await page.evaluate(() => {
      const key = Object.keys(sessionStorage).find(x => x.startsWith("oidc.user:"));
      return key ? (JSON.parse(sessionStorage.getItem(key) || "{}") as {access_token?:string}).access_token : undefined;
    });
    expect(token).toBeTruthy();
    const headers = {Authorization:"Bearer " + token};
    const root = "http://localhost:8080/api/sinistri";
    const prefix = "UI-FPAGE-" + Date.now() + "-" + Math.round(Math.random() * 100000) + "-%_";
    const dates = ["1984-01-21", "1984-01-20", "1984-01-19"];
    const ids: string[] = [];
    for (const [i, date] of dates.entries()) {
      const created = await request.post(root, {headers,
        data:{externalReference:prefix + "-" + i, occurredOn:date}});
      expect(created.status(), await created.text()).toBe(201);
      ids.push((await created.json() as {id:string}).id);
    }
    for (const [ref, date] of [
      [prefix.slice(0,-2) + "AB-DECOY", "1984-01-21"],
      [prefix + "-OUTSIDE", "1985-01-21"]
    ]) {
      const created = await request.post(root, {headers,
        data:{externalReference:ref, occurredOn:date}});
      expect(created.status(), await created.text()).toBe(201);
    }
    const query = (cursor?:string, actualPrefix = prefix, to = "1984-01-31") =>
      "/search/page?" + new URLSearchParams({
        prefix:actualPrefix, occurredFrom:"1984-01-01", occurredTo:to,
        limit:"2", ...(cursor === undefined ? {} : {cursor})
      }).toString();
    const first = await request.get(root + query(), {headers});
    expect(first.status(), await first.text()).toBe(200);
    expect(first.headers()["cache-control"]).toContain("no-store");
    type Result = {items:Array<{id:string;externalReference:string}>;nextCursor:string|null};
    const one = await first.json() as Result;
    expect(one.items.map(x => x.id)).toEqual(ids.slice(0,2));
    expect(one.nextCursor).toBeTruthy();
    expect(await first.text()).not.toContain("tenantId");
    expect((await request.get(root + query(one.nextCursor!, prefix + "-OTHER"), {headers})).status()).toBe(400);
    expect((await request.get(root + query(one.nextCursor!, prefix, "1984-02-01"), {headers})).status()).toBe(400);
    expect((await request.get(root + query(one.nextCursor! + "="), {headers})).status()).toBe(400);
    expect((await request.get(root + query())).status()).toBe(401);

    // A newer matching record must not shift this filtered keyset position.
    const newer = await request.post(root, {headers,
      data:{externalReference:prefix + "-LATE", occurredOn:"1984-01-22"}});
    expect(newer.status(), await newer.text()).toBe(201);
    const second = await request.get(root + query(one.nextCursor!), {headers});
    expect(second.status(), await second.text()).toBe(200);
    const two = await second.json() as Result;
    expect(two.items.map(x => x.id)).toEqual([ids[2]]);
    expect(two.nextCursor).toBeNull();
    const legacy = await request.get(root + "/search?" + new URLSearchParams({
      prefix, limit:"10"
    }).toString(), {headers});
    expect(legacy.status(), await legacy.text()).toBe(200);
    expect(await legacy.text()).toContain(prefix);

    const mcp = await request.post("http://localhost:8080/mcp", {
      headers:{...headers, Accept:"application/json,text/event-stream", "Content-Type":"application/json"},
      data:{jsonrpc:"2.0", id:995, method:"tools/call",
        params:{name:"search_sinistri_page", arguments:{
          prefix, occurredFrom:"1984-01-01", occurredTo:"1984-01-31", limit:2
        }}}
    });
    expect(mcp.ok(), await mcp.text()).toBeTruthy();
    expect(await mcp.text()).toContain(prefix);
    expect(await mcp.text()).not.toContain("tenantId");
  });

  test("PWA loads filtered Sinistri pages and never reuses cursor after filter refresh", async ({page}) => {
    await page.goto("/");
    await page.getByRole("button", {name:"Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout:30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name:"Esci"})).toBeVisible({timeout:30_000});
    const sample = (n:number) => ({
      id:"00000000-0000-0000-0000-" + String(n).padStart(12,"0"),
      externalReference:"FILTERED-MOCK-" + n, occurredOn:"1984-01-" + String(30-n),
      reportedOn:null, createdAtUtc:"2026-11-18T09:00:00Z", version:1
    });
    const queries: Array<{prefix:string|null;from:string|null;to:string|null;cursor:string|null}> = [];
    await page.route(/\/api\/sinistri\/search\/page\?/, async route => {
      const url = new URL(route.request().url());
      const cursor = url.searchParams.get("cursor");
      queries.push({
        prefix:url.searchParams.get("prefix"), from:url.searchParams.get("occurredFrom"),
        to:url.searchParams.get("occurredTo"), cursor
      });
      await route.fulfill({status:200, contentType:"application/json", headers:{"Cache-Control":"no-store"},
        body:JSON.stringify(cursor === null
          ? {items:[sample(1),sample(2)],nextCursor:"FILTER-MOCK-CURSOR"}
          : {items:[sample(2),sample(3)],nextCursor:null})});
    });
    await page.getByRole("button", {name:"Apri catalogo sinistri"}).click();
    const catalog = page.getByRole("region", {name:"Catalogo sinistri"});
    await catalog.getByRole("textbox", {name:"Prefisso riferimento catalogo"}).fill("FILTERED-%_");
    await catalog.getByLabel("Data evento da").fill("1984-01-01");
    await catalog.getByLabel("Data evento a").fill("1984-12-31");
    await catalog.getByRole("button", {name:"Cerca nel catalogo"}).click();
    await expect(catalog.getByText("FILTERED-MOCK-1", {exact:true})).toBeVisible();
    await catalog.getByRole("button", {name:"Carica altri sinistri"}).click();
    await expect(catalog.getByText("FILTERED-MOCK-3", {exact:true})).toBeVisible();
    await expect(catalog.getByRole("list", {name:"Catalogo sinistri"}).locator("li")).toHaveCount(3);
    await expect(catalog.getByRole("button", {name:"Carica altri sinistri"})).toHaveCount(0);
    await catalog.getByRole("button", {name:"Aggiorna sinistri"}).click();
    await expect(catalog.getByText("FILTERED-MOCK-3", {exact:true})).toHaveCount(0);
    await expect(catalog.getByRole("button", {name:"Carica altri sinistri"})).toBeVisible();
    expect(queries).toEqual([
      {prefix:"FILTERED-%_",from:"1984-01-01",to:"1984-12-31",cursor:null},
      {prefix:"FILTERED-%_",from:"1984-01-01",to:"1984-12-31",cursor:"FILTER-MOCK-CURSOR"},
      {prefix:"FILTERED-%_",from:"1984-01-01",to:"1984-12-31",cursor:null},
    ]);
  });

  test("Sinistri keyset REST and MCP read return stable nextCursor, reject invalid cursors and never expose tenant", async ({page, request}) => {
    test.setTimeout(120_000);
    await page.goto("/");
    await page.getByRole("button", {name:"Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout:30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name:"Esci"})).toBeVisible({timeout:30_000});
    const token = await page.evaluate(() => {
      const key = Object.keys(sessionStorage).find(x => x.startsWith("oidc.user:"));
      return key ? (JSON.parse(sessionStorage.getItem(key) || "{}") as {access_token?:string}).access_token : undefined;
    });
    expect(token).toBeTruthy();
    const headers = {Authorization:"Bearer " + token};
    const path = "http://localhost:8080/api/sinistri";
    const ids: string[] = [];
    for (let i = 0; i < 3; i++) {
      const created = await request.post(path, {headers, data: {
        externalReference:"PAGE-E2E-" + Date.now() + "-" + i,
        occurredOn:"9998-12-30"
      }});
      expect(created.status(), await created.text()).toBe(201);
      ids.push((await created.json() as {id:string}).id);
    }
    const first = await request.get(path + "/page?limit=2", {headers});
    expect(first.status(), await first.text()).toBe(200);
    expect(first.headers()["cache-control"]).toContain("no-store");
    type Row = {id:string;externalReference:string;occurredOn:string};
    type Page = {items: Row[]; nextCursor:string|null};
    const page1 = await first.json() as Page;
    expect(page1.items.length).toBe(2);
    expect(page1.nextCursor).toBeTruthy();
    expect(await first.text()).not.toContain("tenantId");

    // Desktop and mobile E2E run concurrently against the SAME signed tenant.
    // Other workers may insert newer rows, so never assume our three rows
    // occupy exactly the first two pages. Assert the real cursor invariant
    // and seek until all our pre-existing IDs have been visited.
    const boundary = page1.items[1];
    const concurrent = await request.post(path, {headers, data: {
      externalReference:"PAGE-LATE-" + Date.now(), occurredOn:"9999-12-31"
    }});
    expect(concurrent.status(), await concurrent.text()).toBe(201);
    const lateId = (await concurrent.json() as {id:string}).id;
    const seen = new Set<string>(page1.items.map(x => x.id));
    let previous = boundary;
    let cursor = page1.nextCursor;
    let visitedPages = 0;
    while (cursor && !ids.every(id => seen.has(id)) && visitedPages < 30) {
      const response = await request.get(path + "/page?limit=2&cursor=" +
        encodeURIComponent(cursor), {headers});
      expect(response.status(), await response.text()).toBe(200);
      const next = await response.json() as Page;
      expect(next.items.length).toBeLessThanOrEqual(2);
      for (const item of next.items) {
        expect(seen.has(item.id), "keyset must not repeat a prior row").toBe(false);
        expect(item.occurredOn < previous.occurredOn ||
          (item.occurredOn === previous.occurredOn &&
            item.id.localeCompare(previous.id) > 0)).toBe(true);
        seen.add(item.id);
        previous = item;
      }
      cursor = next.nextCursor;
      visitedPages++;
    }
    expect(visitedPages).toBeGreaterThan(0);
    expect(ids.every(id => seen.has(id)), "all seeded rows remain reachable").toBe(true);
    if (boundary.occurredOn < "9999-12-31")
      expect(seen.has(lateId), "newer insertion must not shift an existing cursor").toBe(false);

    expect((await request.get(path + "/page?limit=0", {headers})).status()).toBe(400);
    expect((await request.get(path + "/page?limit=51", {headers})).status()).toBe(400);
    expect((await request.get(path + "/page?limit=2&cursor=broken", {headers})).status()).toBe(400);
    expect((await request.get(path + "/page?limit=2")).status()).toBe(401);

    const mcp = await request.post("http://localhost:8080/mcp", {
      headers:{...headers, Accept:"application/json,text/event-stream", "Content-Type":"application/json"},
      data:{jsonrpc:"2.0", id:991, method:"tools/call",
        params:{name:"page_sinistri", arguments:{limit:2}}}
    });
    expect(mcp.ok(), await mcp.text()).toBeTruthy();
    expect(await mcp.text()).toMatch(/nextCursor/i);
    expect(await mcp.text()).not.toContain("tenantId");
  });

  test("PWA appends Sinistri pages without duplicate rows and refresh drops old cursor", async ({page}) => {
    await page.goto("/");
    await page.getByRole("button", {name:"Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout:30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name:"Esci"})).toBeVisible({timeout:30_000});

    const sample = (suffix:string, date:string) => ({
      id:"00000000-0000-0000-0000-00000000000" + suffix,
      externalReference:"PAGED-MOCK-" + suffix, occurredOn:date,
      reportedOn:null, createdAtUtc:"2026-11-18T09:00:00Z", version:1
    });
    const cursors: Array<string|null> = [];
    await page.route(/\/api\/sinistri\/page\?/, async route => {
      const cursor = new URL(route.request().url()).searchParams.get("cursor");
      cursors.push(cursor);
      await route.fulfill({status:200, contentType:"application/json", headers:{"Cache-Control":"no-store"},
        body:JSON.stringify(cursor === null
          ? {items:[sample("1","9998-12-30"), sample("2","9998-12-29")], nextCursor:"UI-SENTINEL"}
          : {items:[sample("2","9998-12-29"), sample("3","9998-12-28")], nextCursor:null})});
    });
    await page.getByRole("button", {name:"Apri catalogo sinistri"}).click();
    const catalog = page.getByRole("region", {name:"Catalogo sinistri"});
    await expect(catalog.getByText("PAGED-MOCK-1", {exact:true})).toBeVisible();
    await expect(catalog.getByRole("button", {name:"Carica altri sinistri"})).toBeVisible();
    await catalog.getByRole("button", {name:"Carica altri sinistri"}).click();
    await expect(catalog.getByText("PAGED-MOCK-3", {exact:true})).toBeVisible();
    await expect(catalog.getByRole("list", {name:"Catalogo sinistri"}).locator("li")).toHaveCount(3);
    await expect(catalog.getByRole("button", {name:"Carica altri sinistri"})).toHaveCount(0);
    expect(cursors).toEqual([null, "UI-SENTINEL"]);

    await catalog.getByRole("button", {name:"Aggiorna sinistri"}).click();
    await expect(catalog.getByText("PAGED-MOCK-3", {exact:true})).toHaveCount(0);
    await expect(catalog.getByRole("button", {name:"Carica altri sinistri"})).toBeVisible();
    expect(cursors).toEqual([null, "UI-SENTINEL", null]);
  });

  test("PWA edits Sinistri with authoritative expectedVersion, invalid input and external CAS conflict", async ({page, request}) => {
    test.setTimeout(120_000);
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});

    const token = await page.evaluate(() => {
      const key = Object.keys(sessionStorage).find(x => x.startsWith("oidc.user:"));
      return key ? (JSON.parse(sessionStorage.getItem(key) || "{}") as {access_token?:string}).access_token : undefined;
    });
    expect(token).toBeTruthy();
    const headers = {Authorization: "Bearer " + token};
    const seed = "UI-SIN-CAS-" + Date.now() + "-" + Math.floor(Math.random() * 100000);
    const created = await request.post("http://localhost:8080/api/sinistri", {
      headers, data: {externalReference: seed, occurredOn: "2098-08-09"}
    });
    expect(created.status(), await created.text()).toBe(201);
    const id = (await created.json() as {id:string;version:number}).id;
    const row = page.locator('[data-sinistro-id="' + id + '"]');
    await page.getByRole("button", {name: "Apri catalogo sinistri"}).click();
    await expect(row.getByText(seed, {exact:true})).toBeVisible();
    await row.getByRole("button", {name: "Modifica sinistro"}).click();
    await expect(row.getByRole("textbox", {name: "Modifica riferimento sinistro"})).toHaveValue(seed);
    await expect(row.getByText("Versione 1")).toBeVisible();

    const refInput = row.getByRole("textbox", {name: "Modifica riferimento sinistro"});
    const occurredInput = row.getByLabel("Modifica data sinistro");
    const reportedInput = row.getByLabel("Modifica data denuncia");
    const first = seed + "-V2";
    await refInput.fill(first);
    await occurredInput.fill("2098-08-10");
    await reportedInput.fill("2098-08-11");
    const routePattern = "**/api/sinistri/" + id;
    // Server-side validation rejects this PUT. The UI must not claim
    // success, increment its version or change the catalog row.
    await page.route(routePattern, async route => {
      if (route.request().method() === "PUT")
        await route.fulfill({status: 400, contentType:"application/json", body:'{"error":"invalid"}'});
      else await route.continue();
    });
    await row.getByRole("button", {name:"Salva modifiche sinistro"}).click();
    await expect(row.getByRole("alert")).toContainText("Dati sinistro non validi");
    await expect(row.getByText(seed, {exact:true})).toBeVisible();
    await expect(row.getByText("Versione 1")).toBeVisible();
    await page.unroute(routePattern);

    const update1 = page.waitForResponse(response =>
      response.url().endsWith("/api/sinistri/" + id) &&
      response.request().method() === "PUT");
    await row.getByRole("button", {name:"Salva modifiche sinistro"}).click();
    const response1 = await update1;
    expect(response1.status(), await response1.text()).toBe(200);
    const requestBody = response1.request().postDataJSON() as {expectedVersion:number;externalReference:string};
    expect(requestBody.expectedVersion).toBe(1);
    expect(requestBody.externalReference).toBe(first);
    const updated1 = await response1.json() as {version:number;externalReference:string};
    expect(updated1).toMatchObject({version:2, externalReference:first});
    await expect(row.getByText(first, {exact:true})).toBeVisible();
    await expect(row.getByText("Versione 2")).toBeVisible();

    const authoritative = await request.get("http://localhost:8080/api/sinistri/" + id, {headers});
    expect(authoritative.status(), await authoritative.text()).toBe(200);
    expect(await authoritative.json()).toMatchObject({
      version:2, externalReference:first, occurredOn:"2098-08-10", reportedOn:"2098-08-11"
    });
    // Another client modifies the row after this editor read version 2.
    const foreignWriterReference = seed + "-OTHER";
    const other = await request.put("http://localhost:8080/api/sinistri/" + id, {
      headers,
      data: {externalReference:foreignWriterReference, occurredOn:"2098-08-12",
        reportedOn:null, expectedVersion:2}
    });
    expect(other.status(), await other.text()).toBe(200);
    expect((await other.json() as {version:number}).version).toBe(3);

    await refInput.fill(seed + "-STALE");
    const conflictResponse = page.waitForResponse(response =>
      response.url().endsWith("/api/sinistri/" + id) &&
      response.request().method() === "PUT");
    await row.getByRole("button", {name:"Salva modifiche sinistro"}).click();
    expect((await conflictResponse).status()).toBe(409);
    await expect(row.getByRole("alert")).toContainText("Ricarica il sinistro");
    await expect(row.getByText(first, {exact:true})).toHaveCount(0);
    await expect(row.getByRole("button", {name:"Salva modifiche sinistro"})).toHaveCount(0);

    // No invisible auto-retry: a fresh GET is required before re-editing.
    await row.getByRole("button", {name:"Ricarica sinistro"}).click();
    await expect(row.getByText(foreignWriterReference, {exact:true})).toBeVisible();
    await expect(refInput).toHaveValue(foreignWriterReference);
    await expect(row.getByText("Versione 3")).toBeVisible();
    await expect(row.getByRole("alert")).toHaveCount(0);

    const finalRef = seed + "-V4";
    await refInput.fill(finalRef);
    const update4 = page.waitForResponse(response =>
      response.url().endsWith("/api/sinistri/" + id) &&
      response.request().method() === "PUT");
    await row.getByRole("button", {name:"Salva modifiche sinistro"}).click();
    const response4 = await update4;
    expect(response4.status()).toBe(200);
    expect((response4.request().postDataJSON() as {expectedVersion:number}).expectedVersion).toBe(3);
    await expect(row.getByText(finalRef, {exact:true})).toBeVisible();
    await page.reload();
    await page.getByRole("button", {name:"Apri catalogo sinistri"}).click();
    await expect(page.locator('[data-sinistro-id="' + id + '"]')
      .getByText(finalRef, {exact:true})).toBeVisible();
    const final = await request.get("http://localhost:8080/api/sinistri/" + id, {headers});
    expect(await final.json()).toMatchObject({externalReference:finalRef, version:4});
  });

  test("PWA searches and links Sinistri to Perizie with persistence, conflict reload and offline-safe state", async ({page, request, context}) => {
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});
    const token = await page.evaluate(() => {
      const key = Object.keys(sessionStorage).find(x => x.startsWith("oidc.user:"));
      return key
        ? (JSON.parse(sessionStorage.getItem(key) || "{}") as {access_token?:string}).access_token
        : undefined;
    });
    expect(token).toBeTruthy();
    const headers = {Authorization: "Bearer " + token};
    const unique = Date.now() + "-" + Math.round(Math.random() * 100000);
    const sinRef = "UI-SIN-LINK-" + unique;
    const sinistro = await request.post("http://localhost:8080/api/sinistri", {
      headers, data: {externalReference: sinRef, occurredOn: "2026-11-17"}
    });
    expect(sinistro.status(), await sinistro.text()).toBe(201);
    const sinId = (await sinistro.json() as {id:string}).id;
    const reference = "UI-PERIZIA-LINK-" + unique;

    await page.locator("#reference").fill(reference);
    await page.getByRole("button", {name: "Crea"}).click();
    const row = page.getByRole("list", {name: /^(Perizie|Appraisals)$/})
      .locator("li").filter({hasText: reference});
    await expect(row).toBeVisible();
    const periziaId = await row.getAttribute("data-perizia-id");
    expect(periziaId).toBeTruthy();

    await row.getByRole("button", {name: "Sinistro della perizia"}).click();
    await expect(row.getByText("Perizia senza sinistro")).toBeVisible();
    await row.getByRole("textbox", {name: "Cerca per riferimento sinistro"}).fill("UI-SIN-LINK-" + unique);
    await row.getByRole("button", {name: "Cerca sinistri"}).click();
    await expect(row.getByRole("combobox", {name: "Seleziona sinistro"}))
      .toContainText(sinRef);
    await row.getByRole("combobox", {name: "Seleziona sinistro"})
      .selectOption({index: 1});
    const linked = page.waitForResponse(response =>
      response.url().endsWith("/api/perizie/" + periziaId + "/sinistro") &&
      response.request().method() === "POST");
    await row.getByRole("button", {name: "Collega sinistro", exact: true}).click();
    expect((await linked).status()).toBe(201);
    await expect(row.getByText("Revisione collegamento 2")).toBeVisible();
    await expect(row.getByText("Sinistro collegato", {exact:true})).toBeVisible();
    await expect(row.getByText(sinRef)).toBeVisible();
    await expect(row.getByRole("button", {name: "Collega sinistro", exact: true})).toHaveCount(0);

    // Reload must not mistake a pre-existing link for an unlinked record.
    await page.reload();
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/})
      .getByText(reference)).toBeVisible({timeout: 30_000});
    const restored = page.getByRole("list", {name: /^(Perizie|Appraisals)$/})
      .locator("li").filter({hasText: reference});
    await restored.getByRole("button", {name: "Sinistro della perizia"}).click();
    await expect(restored.getByText(sinRef)).toBeVisible({timeout: 30_000});
    await expect(restored.getByRole("button", {name: "Collega sinistro", exact: true})).toHaveCount(0);

    // Explicit unlink never triggers from the initial button alone.
    // A simulated CAS conflict must invalidate stale relation UI instead of
    // pretending a success or silently removing a concurrent replacement.
    await restored.getByRole("button", {name:"Scollega sinistro"}).click();
    await expect(restored.getByRole("button", {name:"Conferma scollegamento"})).toBeVisible();
    await restored.getByRole("button", {name:"Annulla scollegamento"}).click();
    await expect(restored.getByText(sinRef)).toBeVisible();
    await restored.getByRole("button", {name:"Scollega sinistro"}).click();
    const unlinkPattern = "**/api/perizie/*/sinistro?expectedSinistroId=*";
    await page.route(unlinkPattern, async route =>
      route.fulfill({status:409,contentType:"application/json",body:'{"error":"stale"}'}));
    await restored.getByRole("button", {name:"Conferma scollegamento"}).click();
    await expect(restored.getByRole("alert")).toContainText("Ricarica il collegamento");
    await expect(restored.getByRole("button", {name:"Conferma scollegamento"})).toHaveCount(0);
    await page.unroute(unlinkPattern);
    await restored.getByRole("button", {name:"Ricarica collegamento"}).click();
    await expect(restored.getByText(sinRef)).toBeVisible();

    await restored.getByRole("button", {name:"Scollega sinistro"}).click();
    const unlinkDone = page.waitForResponse(response =>
      response.url().includes("/api/perizie/" + periziaId + "/sinistro?") &&
      response.request().method() === "DELETE");
    await restored.getByRole("button", {name:"Conferma scollegamento"}).click();
    expect((await unlinkDone).status()).toBe(204);
    await expect(restored.getByText("Perizia senza sinistro")).toBeVisible();
    await expect(restored.getByRole("status")).toContainText("Collegamento rimosso");
    const persistedUnlink = await request.get(
      "http://localhost:8080/api/perizie/" + periziaId + "/sinistro", {headers});
    expect((await persistedUnlink.json() as {sinistroId:null}).sinistroId).toBeNull();

    // A concurrent link from a different client must become 409, never a
    // false optimistic UI success nor an automatic duplicate submission.
    const raceRef = "UI-PERIZIA-RACE-" + unique;
    await page.locator("#reference").fill(raceRef);
    await page.getByRole("button", {name: "Crea"}).click();
    const raceRow = page.getByRole("list", {name: /^(Perizie|Appraisals)$/})
      .locator("li").filter({hasText: raceRef});
    await expect(raceRow).toBeVisible();
    const raceId = await raceRow.getAttribute("data-perizia-id");
    expect(raceId).toBeTruthy();
    await raceRow.getByRole("button", {name: "Sinistro della perizia"}).click();
    await expect(raceRow.getByText("Perizia senza sinistro")).toBeVisible();
    await raceRow.getByRole("textbox", {name: "Cerca per riferimento sinistro"}).fill(sinRef);
    await raceRow.getByRole("button", {name: "Cerca sinistri"}).click();
    await expect(raceRow.getByRole("combobox", {name: "Seleziona sinistro"}))
      .toContainText(sinRef);
    await raceRow.getByRole("combobox", {name: "Seleziona sinistro"}).selectOption({index: 1});
    const externalLink = await request.post(
      "http://localhost:8080/api/perizie/" + raceId + "/sinistro",
      {headers, data: {sinistroId: sinId, expectedLinkVersion:1}}
    );
    expect(externalLink.status(), await externalLink.text()).toBe(201);
    await raceRow.getByRole("button", {name: "Collega sinistro", exact: true}).click();
    await expect(raceRow.getByRole("alert")).toContainText("Ricarica il collegamento");
    await expect(raceRow.getByRole("button", {name: "Collega sinistro", exact: true})).toHaveCount(0);
    await raceRow.getByRole("button", {name: "Ricarica collegamento"}).click();
    await expect(raceRow.getByText(sinRef)).toBeVisible({timeout: 30_000});
    await expect(raceRow.getByRole("alert")).toHaveCount(0);

    // Offline does not expose retained tenant data or allow queued writes.
    await context.setOffline(true);
    await expect(page.getByRole("status")).toContainText("Sei offline");
    await expect(page.getByText(reference, {exact:true})).toHaveCount(0);
    await expect(page.getByRole("button", {name: "Collega sinistro", exact: true})).toHaveCount(0);
    await context.setOffline(false);
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/})
      .getByText(reference, {exact:true})).toBeVisible({timeout: 30_000});
  });

  test("PWA read-only Keycloak scope hides all write controls and restores them on writer rotation", async ({page, request}) => {
    test.setTimeout(120_000);
    const issuer="http://localhost:8081/realms/peritial/protocol/openid-connect/token";
    async function grant(clientId:string){
      const response=await request.post(issuer,{
        form:{grant_type:"password",client_id:clientId,
          username:"localuser",password:"local-only-user"}
      });
      expect(response.status(),await response.text()).toBe(200);
      return response.json() as Promise<{access_token:string;scope:string}>;
    }
    const reader=await grant("peritial-test-readonly");
    const writer=await grant("peritial-test-write");
    expect(reader.scope.split(/\s+/)).toContain("peritial.read");
    expect(reader.scope.split(/\s+/)).not.toContain("peritial.write");
    expect(writer.scope.split(/\s+/)).toContain("peritial.write");
    const reference="READONLY-UI-"+Date.now()+"-"+Math.floor(Math.random()*100000);
    const created=await request.post("http://localhost:8080/api/perizie",{
      headers:{Authorization:"Bearer "+writer.access_token},
      data:{reference}
    });
    expect(created.status(),await created.text()).toBe(201);
    const id=(await created.json() as {id:string}).id;

    // Start with a normal PKCE browser session. The test then swaps the
    // session's bearer token for a real independently issued read-only JWT
    // belonging to the same Keycloak subject and tenant. No fake unsigned
    // permissions or mocked API response are used.
    await page.goto("/");
    await page.getByRole("button",{name:"Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout:30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button",{name:"Esci"})).toBeVisible({timeout:30_000});
    const row=page.locator('[data-perizia-id="'+id+'"]');
    await expect(row.getByText(reference,{exact:true})).toBeVisible({timeout:30_000});
    await expect(row.getByRole("button",{name:"Annulla"})).toBeVisible();
    await expect(row.getByRole("button",{name:"Assegna"})).toBeVisible();
    await expect(page.getByRole("button",{name:"Carica documento"})).toBeVisible();

    const oldStorage=await page.evaluate(()=>{
      const key=Object.keys(sessionStorage).find(k=>k.startsWith("oidc.user:"));
      if(!key) throw new Error("PKCE user is not stored in browser session");
      const raw=sessionStorage.getItem(key);
      if(!raw) throw new Error("PKCE user session unavailable");
      return {key,raw};
    });
    await page.evaluate(({key,token,scope})=>{
      const raw=sessionStorage.getItem(key);
      if(!raw) throw new Error("Cannot replace OIDC user");
      const user=JSON.parse(raw) as {access_token:string;scope:string};
      user.access_token=token;
      user.scope=scope;
      sessionStorage.setItem(key,JSON.stringify(user));
    },{key:oldStorage.key,token:reader.access_token,scope:reader.scope});

    const readList=page.waitForRequest(req=>req.url().endsWith("/api/perizie") &&
      req.method()==="GET" &&
      req.headers()["authorization"]==="Bearer "+reader.access_token);
    await page.reload();
    await readList;
    await expect(row.getByText(reference,{exact:true})).toBeVisible({timeout:30_000});
    await expect(row.getByText("Registrata",{exact:true})).toBeVisible();
    await expect(page.locator("#reference")).toHaveCount(0);
    await expect(page.getByRole("button",{name:"Crea",exact:true})).toHaveCount(0);
    await expect(page.getByRole("button",{name:"Carica documento"})).toHaveCount(0);
    await expect(page.locator("#file")).toHaveCount(0);
    await expect(page.locator("#upload-perizia")).toHaveCount(0);
    await expect(page.locator("#documents-perizia")).toBeVisible();
    await expect(row.getByRole("button",{name:"Assegna"})).toHaveCount(0);
    await expect(row.getByRole("button",{name:"Avanza"})).toHaveCount(0);
    await expect(row.getByRole("button",{name:"Annulla"})).toHaveCount(0);
    await page.getByRole("button",{name:"Apri catalogo sinistri"}).click();
    await expect(page.getByRole("button",{name:"Crea sinistro"})).toHaveCount(0);

    // The backend remains the actual authorization boundary.
    const denied=await request.post("http://localhost:8080/api/perizie",{
      headers:{Authorization:"Bearer "+reader.access_token},
      data:{reference:reference+"-DENIED"}
    });
    expect(denied.status(),await denied.text()).toBe(403);

    // Rotate to an authorized PKCE writer without remounting another
    // tenant. The controls must reappear after the signed GET completes.
    await page.evaluate(({key,raw})=>sessionStorage.setItem(key,raw),oldStorage);
    await page.reload();
    await expect(row.getByText(reference,{exact:true})).toBeVisible({timeout:30_000});
    await expect(page.locator("#reference")).toBeVisible();
    await expect(page.getByRole("button",{name:"Crea",exact:true})).toBeEnabled();
    await expect(page.getByRole("button",{name:"Carica documento"})).toBeVisible();
    await expect(row.getByRole("button",{name:"Assegna"})).toBeVisible();
    await expect(row.getByRole("button",{name:"Annulla"})).toBeVisible();
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

  test("Perizia–Sinistro reparent CAS replaces only signed expected link via REST and MCP", async ({page, request}) => {
    test.setTimeout(120_000);
    await page.goto("/");
    await page.getByRole("button", {name:"Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout:30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name:"Esci"})).toBeVisible({timeout:30_000});
    const token = await page.evaluate(() => {
      const key = Object.keys(sessionStorage).find(x => x.startsWith("oidc.user:"));
      return key ? (JSON.parse(sessionStorage.getItem(key) || "{}") as {access_token?:string}).access_token : undefined;
    });
    expect(token).toBeTruthy();
    const headers = {Authorization:"Bearer " + token};
    const root = "http://localhost:8080/api";
    const stamp = Date.now() + "-" + Math.floor(Math.random()*100000);
    const created = await request.post(root + "/perizie", {headers,
      data:{reference:"CAS-REP-P-" + stamp}});
    expect(created.status(), await created.text()).toBe(201);
    const id = (await created.json() as {id:string}).id;
    const sinistri: string[] = [];
    for (const suffix of ["OLD","NEW"]) {
      const item = await request.post(root + "/sinistri", {headers,
        data:{externalReference:"CAS-REP-S-" + stamp + "-" + suffix,
          occurredOn:"2026-11-18"}});
      expect(item.status(), await item.text()).toBe(201);
      sinistri.push((await item.json() as {id:string}).id);
    }
    const linked = await request.post(root + "/perizie/" + id + "/sinistro", {
      headers, data:{sinistroId:sinistri[0],expectedLinkVersion:1}});
    expect(linked.status(), await linked.text()).toBe(201);

    const url = root + "/perizie/" + id + "/sinistro";
    const reparent = (expectedSinistroId:string, newSinistroId:string,
      expectedLinkVersion:number, h?:Record<string,string>) => request.put(url, {
        ...(h ? {headers:h} : {}),
        data:{expectedSinistroId,newSinistroId,expectedLinkVersion}
      });
    expect((await reparent(sinistri[0],sinistri[1],2)).status()).toBe(401);
    const readGrant = await request.post(
      "http://localhost:8081/realms/peritial/protocol/openid-connect/token", {
        form:{grant_type:"password",client_id:"peritial-test-readonly",
          username:"localuser",password:"local-only-user"}
      });
    expect(readGrant.status(),await readGrant.text()).toBe(200);
    const readToken = (await readGrant.json() as {access_token:string}).access_token;
    expect((await reparent(sinistri[0],sinistri[1],2,
      {Authorization:"Bearer " + readToken})).status()).toBe(403);
    expect((await reparent(sinistri[0],sinistri[0],2,headers)).status()).toBe(400);
    expect((await reparent(sinistri[1],sinistri[0],2,headers)).status()).toBe(409);
    expect((await reparent(sinistri[0],"00000000-0000-0000-0000-000000000099",2,headers)).status()).toBe(404);
    const before = await request.get(url,{headers});
    expect((await before.json() as {sinistroId:string}).sinistroId).toBe(sinistri[0]);

    const moved = await reparent(sinistri[0],sinistri[1],2,headers);
    expect(moved.status(),await moved.text()).toBe(200);
    expect(await moved.json()).toEqual({periziaId:id,sinistroId:sinistri[1],linkVersion:3});
    expect((await request.get(url,{headers}).then(r=>r.json()) as {sinistroId:string}).sinistroId)
      .toBe(sinistri[1]);
    expect((await request.get(root + "/sinistri/" + sinistri[0],{headers})).status()).toBe(200);
    expect((await request.get(root + "/sinistri/" + sinistri[1],{headers})).status()).toBe(200);
    expect((await reparent(sinistri[0],sinistri[1],2,headers)).status()).toBe(409);

    const mcp = await request.post("http://localhost:8080/mcp", {
      headers:{...headers,Accept:"application/json,text/event-stream",
        "Content-Type":"application/json"},
      data:{jsonrpc:"2.0",id:944,method:"tools/call",params:{
        name:"reparent_perizia_sinistro",arguments:{
          periziaId:id, expectedSinistroId:sinistri[1],newSinistroId:sinistri[0],
          expectedLinkVersion:3
        }
      }}
    });
    expect(mcp.ok(),await mcp.text()).toBeTruthy();
    expect(await mcp.text()).not.toContain('"isError":true');
    const returned = await request.get(url,{headers}).then(r=>r.json()) as
      {sinistroId:string;linkVersion:number};
    expect(returned.sinistroId).toBe(sinistri[0]);
    expect(returned.linkVersion).toBe(4);
    // ABA: the first reader observed A at revision 2; after A->B->A
    // its old revision MUST be rejected even though the UUID matches.
    const aba = await reparent(sinistri[0],sinistri[1],2,headers);
    expect(aba.status(),await aba.text()).toBe(409);
    const afterAba = await request.get(url,{headers}).then(r=>r.json()) as
      {sinistroId:string;linkVersion:number};
    expect(afterAba).toMatchObject({sinistroId:sinistri[0],linkVersion:4});
  });

  test("PWA reparent requires two confirmations, rejects stale link and refetches committed target", async ({page, request}) => {
    test.setTimeout(120_000);
    await page.goto("/");
    await page.getByRole("button", {name:"Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout:30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name:"Esci"})).toBeVisible({timeout:30_000});
    const token = await page.evaluate(() => {
      const key = Object.keys(sessionStorage).find(x => x.startsWith("oidc.user:"));
      return key ? (JSON.parse(sessionStorage.getItem(key) || "{}") as {access_token?:string}).access_token : undefined;
    });
    expect(token).toBeTruthy();
    const headers = {Authorization:"Bearer " + token};
    const root = "http://localhost:8080/api";
    const stamp = Date.now() + "-" + Math.floor(Math.random()*100000);
    const sourceRef = "UI-REP-" + stamp + "-OLD";
    const targetRef = "UI-REP-" + stamp + "-NEW";
    const ids: string[] = [];
    for (const externalReference of [sourceRef,targetRef]) {
      const created = await request.post(root + "/sinistri", {headers,
        data:{externalReference,occurredOn:"2026-11-18"}});
      expect(created.status(),await created.text()).toBe(201);
      ids.push((await created.json() as {id:string}).id);
    }
    const periziaRef = "UI-REP-P-" + stamp;
    const p = await request.post(root + "/perizie", {headers,data:{reference:periziaRef}});
    expect(p.status(),await p.text()).toBe(201);
    const periziaId = (await p.json() as {id:string}).id;
    const linked = await request.post(root + "/perizie/" + periziaId + "/sinistro",
      {headers,data:{sinistroId:ids[0],expectedLinkVersion:1}});
    expect(linked.status(),await linked.text()).toBe(201);

    await page.getByRole("button", {name:"Aggiorna elenco"}).click();
    const row = page.getByRole("list", {name:/^(Perizie|Appraisals)$/}).locator("li")
      .filter({hasText:periziaRef});
    await expect(row).toBeVisible();
    await row.getByRole("button", {name:"Sinistro della perizia"}).click();
    await expect(row.getByText(sourceRef,{exact:true})).toBeVisible();
    await row.getByRole("button", {name:"Cambia sinistro collegato"}).click();
    await expect(row.getByRole("button",{name:"Annulla riassegnazione"})).toBeVisible();
    await expect(row.getByText(sourceRef,{exact:true})).toBeVisible();
    await row.getByRole("textbox",{name:"Cerca per riferimento sinistro"}).fill("UI-REP-" + stamp);
    await row.getByRole("button",{name:"Cerca sinistri"}).click();
    const choice = row.getByRole("combobox",{name:"Seleziona sinistro"});
    await expect(choice).toContainText(targetRef);
    await choice.selectOption(ids[1]);
    await row.getByRole("button",{name:"Rivedi riassegnazione"}).click();
    await expect(row.getByRole("button",{name:"Conferma riassegnazione"})).toBeVisible();
    await row.getByRole("button",{name:"Annulla riassegnazione"}).click();
    expect((await request.get(root + "/perizie/" + periziaId + "/sinistro",{headers})
      .then(r=>r.json()) as {sinistroId:string}).sinistroId).toBe(ids[0]);

    await row.getByRole("button",{name:"Cambia sinistro collegato"}).click();
    await row.getByRole("textbox",{name:"Cerca per riferimento sinistro"}).fill("UI-REP-" + stamp);
    await row.getByRole("button",{name:"Cerca sinistri"}).click();
    await row.getByRole("combobox",{name:"Seleziona sinistro"}).selectOption(ids[1]);
    await row.getByRole("button",{name:"Rivedi riassegnazione"}).click();
    const reparentPattern = "**/api/perizie/*/sinistro";
    await page.route(reparentPattern, async route => {
      if (route.request().method() === "PUT")
        await route.fulfill({status:409,contentType:"application/json",body:'{"error":"stale"}'});
      else await route.continue();
    });
    await row.getByRole("button",{name:"Conferma riassegnazione"}).click();
    await expect(row.getByRole("alert")).toContainText("Ricarica il collegamento");
    await expect(row.getByRole("button",{name:"Conferma riassegnazione"})).toHaveCount(0);
    await page.unroute(reparentPattern);
    await row.getByRole("button",{name:"Ricarica collegamento"}).click();
    await expect(row.getByText(sourceRef,{exact:true})).toBeVisible();

    await row.getByRole("button",{name:"Cambia sinistro collegato"}).click();
    await row.getByRole("textbox",{name:"Cerca per riferimento sinistro"}).fill("UI-REP-" + stamp);
    await row.getByRole("button",{name:"Cerca sinistri"}).click();
    await row.getByRole("combobox",{name:"Seleziona sinistro"}).selectOption(ids[1]);
    await row.getByRole("button",{name:"Rivedi riassegnazione"}).click();
    const response = page.waitForResponse(r =>
      r.url().includes("/api/perizie/" + periziaId + "/sinistro") &&
      r.request().method() === "PUT");
    await row.getByRole("button",{name:"Conferma riassegnazione"}).click();
    expect((await response).status()).toBe(200);
    await expect(row.getByText(targetRef,{exact:true})).toBeVisible({timeout:30_000});
    await expect(row.getByRole("status")).toContainText("Riassegnazione confermata");
    expect((await request.get(root + "/perizie/" + periziaId + "/sinistro",{headers})
      .then(r=>r.json()) as {sinistroId:string}).sinistroId).toBe(ids[1]);
    expect((await request.get(root + "/sinistri/" + ids[0],{headers})).status()).toBe(200);

    // Terminal status is an explicit server-side workflow gate. The PWA
    // should not offer an operation the signed backend must reject.
    const cancelled = await request.post(root + "/perizie/" + periziaId + "/cancel",{headers});
    expect(cancelled.status(),await cancelled.text()).toBe(200);
    await page.getByRole("button",{name:"Aggiorna elenco"}).click();
    await expect(row.getByText("Annullata",{exact:true})).toBeVisible();
    // Re-open if refresh remounted the picker. Assert the linked target
    // remains visibly loaded, so the absent button is not a vacuous pass
    // caused by a closed/collapsed section.
    const toggleLink = row.getByRole("button",{name:"Sinistro della perizia"});
    if (await toggleLink.getAttribute("aria-expanded") !== "true")
      await toggleLink.click();
    await expect(row.getByText(targetRef,{exact:true})).toBeVisible();
    await expect(row.getByRole("button",{name:"Cambia sinistro collegato"})).toHaveCount(0);
    const terminalAttempt = await request.put(root + "/perizie/" + periziaId + "/sinistro",{
      headers,data:{expectedSinistroId:ids[1],newSinistroId:ids[0],expectedLinkVersion:3}
    });
    expect(terminalAttempt.status(),await terminalAttempt.text()).toBe(409);
    expect((await request.get(root + "/perizie/" + periziaId + "/sinistro",{headers})
      .then(r=>r.json()) as {sinistroId:string}).sinistroId).toBe(ids[1]);
  });

  test("Perizia–Sinistro unlink requires signed expected association and commits via REST/MCP", async ({page, request}) => {
    test.setTimeout(120_000);
    await page.goto("/");
    await page.getByRole("button", {name:"Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout:30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name:"Esci"})).toBeVisible({timeout:30_000});
    const token = await page.evaluate(() => {
      const key = Object.keys(sessionStorage).find(x => x.startsWith("oidc.user:"));
      return key ? (JSON.parse(sessionStorage.getItem(key) || "{}") as {access_token?:string}).access_token : undefined;
    });
    expect(token).toBeTruthy();
    const headers = {Authorization:"Bearer " + token};
    const root = "http://localhost:8080/api";
    const stamp = Date.now() + "-" + Math.floor(Math.random() * 100000);
    const perizia = await request.post(root + "/perizie", {headers,
      data:{reference:"E2E-UNLINK-P-" + stamp}});
    expect(perizia.status(), await perizia.text()).toBe(201);
    const periziaId = (await perizia.json() as {id:string}).id;
    const sinistri: string[] = [];
    for(const suffix of ["ORIGINAL","NEW"]) {
      const c = await request.post(root + "/sinistri", {headers,
        data:{externalReference:"E2E-UNLINK-S-" + stamp + "-" + suffix,
          occurredOn:"2026-11-18"}});
      expect(c.status(), await c.text()).toBe(201);
      sinistri.push((await c.json() as {id:string}).id);
    }
    const link = await request.post(root + "/perizie/" + periziaId + "/sinistro", {
      headers, data:{sinistroId:sinistri[0],expectedLinkVersion:1}
    });
    expect(link.status(), await link.text()).toBe(201);
    const url = (expected:string) => root + "/perizie/" + periziaId +
      "/sinistro?expectedSinistroId=" + expected + "&expectedLinkVersion=2";

    expect((await request.delete(url(sinistri[0]))).status()).toBe(401);
    expect((await request.delete(root + "/perizie/" + periziaId + "/sinistro", {headers})).status()).toBe(400);
    expect((await request.delete(url(sinistri[1]), {headers})).status()).toBe(409);
    const staleState = await request.get(root + "/perizie/" + periziaId + "/sinistro", {headers});
    expect((await staleState.json() as {sinistroId:string}).sinistroId).toBe(sinistri[0]);

    const ok = await request.delete(url(sinistri[0]), {headers});
    expect(ok.status(), await ok.text()).toBe(204);
    const unlinked = await request.get(root + "/perizie/" + periziaId + "/sinistro", {headers});
    expect((await unlinked.json() as {periziaId:string;sinistroId:null;linkVersion:number}))
      .toEqual({periziaId, sinistroId:null, linkVersion:3});
    expect(await unlinked.text()).not.toContain("tenantId");
    expect((await request.delete(url(sinistri[0]), {headers})).status()).toBe(409);
    const relink = await request.post(root + "/perizie/" + periziaId + "/sinistro", {
      headers, data:{sinistroId:sinistri[1],expectedLinkVersion:3}
    });
    expect(relink.status(), await relink.text()).toBe(201);
    const mcp = await request.post("http://localhost:8080/mcp", {
      headers:{...headers,Accept:"application/json,text/event-stream",
        "Content-Type":"application/json"},
      data:{jsonrpc:"2.0",id:941,method:"tools/call",params:{
        name:"unlink_perizia_sinistro",
        arguments:{periziaId,expectedSinistroId:sinistri[1],expectedLinkVersion:4}
      }}
    });
    expect(mcp.ok(), await mcp.text()).toBeTruthy();
    expect(await mcp.text()).not.toContain('"isError":true');
    expect((await request.get(root + "/perizie/" + periziaId + "/sinistro", {headers}).then(r=>r.json()) as {sinistroId:null;linkVersion:number})).toMatchObject({sinistroId:null,linkVersion:5});
  });

  test("Sinistri V005 deletes only unlinked cases with signed CAS and MCP, rejects FK and stale versions", async ({page, request}) => {
    test.setTimeout(120_000);
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});
    const token = await page.evaluate(() => {
      const key = Object.keys(sessionStorage).find(x => x.startsWith("oidc.user:"));
      return key ? (JSON.parse(sessionStorage.getItem(key) || "{}") as {access_token?:string}).access_token : undefined;
    });
    expect(token).toBeTruthy();
    const headers = {Authorization: "Bearer " + token};
    // A normal web PKCE token carries peritial.write but NEVER the
    // destructive peritial.sinistri.delete scope. Only the explicit,
    // development-only test client receives it.
    const privileged = await request.post(
      "http://localhost:8081/realms/peritial/protocol/openid-connect/token", {
        form:{grant_type:"password", client_id:"peritial-test-delete",
          username:"localuser", password:"local-only-user"}
      });
    expect(privileged.status(), await privileged.text()).toBe(200);
    const scoped = await privileged.json() as {access_token:string};
    const privilegedHeaders = {Authorization:"Bearer " + scoped.access_token};
    expect(JSON.parse(Buffer.from(scoped.access_token.split(".")[1], "base64url")
      .toString("utf-8")).scope).toContain("peritial.sinistri.delete");
    const base = "http://localhost:8080/api";
    const stamp = Date.now() + "-" + Math.round(Math.random() * 100000);
    async function createSinistro(suffix: string) {
      const result = await request.post(base + "/sinistri", {headers,
        data:{externalReference:"DELETE-E2E-" + stamp + "-" + suffix,
          occurredOn:"2026-11-18"}});
      expect(result.status(), await result.text()).toBe(201);
      return await result.json() as {id:string; version:number};
    }
    const free = await createSinistro("FREE");
    const forMcp = await createSinistro("MCP");
    const linked = await createSinistro("LINKED");
    const perizia = await request.post(base + "/perizie", {headers,
      data:{reference:"DELETE-PERIZIA-" + stamp}});
    expect(perizia.status(), await perizia.text()).toBe(201);
    const periziaId = (await perizia.json() as {id:string}).id;
    const link = await request.post(base + "/perizie/" + periziaId + "/sinistro", {
      headers, data:{sinistroId:linked.id,expectedLinkVersion:1}
    });
    expect(link.status(), await link.text()).toBe(201);

    const url = (id:string, version?:number) => base + "/sinistri/" + id +
      (version === undefined ? "" : "?expectedVersion=" + version);
    expect((await request.delete(url(free.id, 1))).status()).toBe(401);
    expect((await request.delete(url(free.id, 1), {headers})).status()).toBe(403);
    expect((await request.delete(url(free.id), {headers:privilegedHeaders})).status()).toBe(400);
    expect((await request.delete(url(free.id, 0), {headers:privilegedHeaders})).status()).toBe(400);
    expect((await request.delete(url(free.id, 2), {headers:privilegedHeaders})).status()).toBe(409);
    expect((await request.delete(url(linked.id, linked.version), {headers:privilegedHeaders})).status()).toBe(409);
    expect((await request.get(url(linked.id), {headers})).status()).toBe(200);
    expect((await request.get(base + "/perizie/" + periziaId + "/sinistro", {headers})).status()).toBe(200);

    const deleted = await request.delete(url(free.id, free.version), {headers:privilegedHeaders});
    expect(deleted.status(), await deleted.text()).toBe(204);
    expect((await request.get(url(free.id), {headers})).status()).toBe(404);
    expect((await request.delete(url(free.id, 1), {headers:privilegedHeaders})).status()).toBe(404);

    const tool = await request.post("http://localhost:8080/mcp", {
      headers:{...privilegedHeaders, Accept:"application/json,text/event-stream",
        "Content-Type":"application/json"},
      data:{jsonrpc:"2.0", id:809, method:"tools/call",
        params:{name:"delete_sinistro", arguments:{id:forMcp.id,expectedVersion:1}}}
    });
    expect(tool.ok(), await tool.text()).toBeTruthy();
    expect(await tool.text()).not.toContain('"isError":true');
    expect((await request.get(url(forMcp.id), {headers})).status()).toBe(404);
    const deniedMcp = await request.post("http://localhost:8080/mcp", {
      headers:{...headers, Accept:"application/json,text/event-stream",
        "Content-Type":"application/json"},
      data:{jsonrpc:"2.0", id:810, method:"tools/call",
        params:{name:"delete_sinistro", arguments:{id:linked.id,expectedVersion:1}}}
    });
    expect(deniedMcp.ok(), await deniedMcp.text()).toBeTruthy();
    expect(await deniedMcp.text()).toContain('"isError":true');
    expect((await request.get(url(linked.id), {headers})).status()).toBe(200);
  });

  test("sinistri REST e MCP condividono JWT, tenant, audit e conflitto CAS", async ({page, request}) => {
    await page.goto("/");
    await page.getByRole("button", {name: "Accedi"}).click();
    await expect(page.locator("#username")).toBeVisible({timeout: 30_000});
    await page.locator("#username").fill("localuser");
    await page.locator("#password").fill("local-only-user");
    await page.locator("#kc-login").click();
    await expect(page.getByRole("button", {name: "Esci"})).toBeVisible({timeout: 30_000});
    const token = await page.evaluate(() => {
      const key = Object.keys(sessionStorage).find(k => k.startsWith("oidc.user:"));
      if (!key) return null;
      const data = JSON.parse(sessionStorage.getItem(key) || "{}") as {access_token?: string};
      return data.access_token || null;
    });
    expect(token).toBeTruthy();
    const headers = {Authorization: "Bearer " + token};
    const path = "http://localhost:8080/api/sinistri";
    const reference = "E2E-SIN-" + Date.now() + "-" + Math.round(Math.random() * 100000);
    const created = await request.post(path, {headers, data: {
      externalReference: reference, occurredOn: "2026-11-01", reportedOn: "2026-11-03"
    }});
    expect(created.status(), await created.text()).toBe(201);
    expect(created.headers()["cache-control"]).toContain("no-store");
    const initial = await created.json() as {id:string;externalReference:string;version:number;reportedOn:string};
    expect(initial.externalReference).toBe(reference);
    expect(initial.reportedOn).toBe("2026-11-03");
    expect(initial.version).toBe(1);
    expect(await created.text()).not.toContain("tenantId");

    const get = await request.get(path + "/" + initial.id, {headers});
    expect(get.status()).toBe(200);
    expect((await get.json() as {version: number}).version).toBe(1);
    // A bounded catalog is ordered by occurredOn, not by creation time;
    // an older case need not be among the first 10 as suites add cases.
    const list = await request.get(path + "?limit=10", {headers});
    expect(list.status()).toBe(200);
    expect((await list.json() as {id: string}[]).length).toBeLessThanOrEqual(10);
    const searched = await request.get(path + "/search?prefix=" +
      encodeURIComponent(reference) + "&limit=10", {headers});
    expect(searched.status(), await searched.text()).toBe(200);
    expect(await searched.text()).toContain(reference);
    const badLimit = await request.get(path + "?limit=101", {headers});
    expect(badLimit.status()).toBe(400);

    // The exact same signed token invokes MCP, and the tool sees only
    // the server-computed tenant context, never a tenant parameter.
    async function mcp(name: string, args: Record<string, unknown>, id: number) {
      const response = await request.post("http://localhost:8080/mcp", {
        headers: {
          ...headers, Accept: "application/json,text/event-stream",
          "Content-Type": "application/json"
        },
        data: {jsonrpc: "2.0", id, method: "tools/call",
          params: {name, arguments: args}}
      });
      expect(response.ok(), await response.text()).toBeTruthy();
      const body = await response.text();
      expect(body).not.toContain('"isError":true');
      return body;
    }
    expect(await mcp("get_sinistro", {id: initial.id}, 601)).toContain(reference);
    await mcp("list_sinistri", {limit: 10}, 602);
    expect(await mcp("search_sinistri", {prefix: reference, limit: 10}, 604))
      .toContain(reference);

    const updated = await request.put(path + "/" + initial.id, {headers, data: {
      externalReference: reference + "-EDIT", occurredOn: "2026-11-02",
      reportedOn: null, expectedVersion: initial.version
    }});
    expect(updated.status(), await updated.text()).toBe(200);
    const next = await updated.json() as {version:number;externalReference:string;reportedOn:null};
    expect(next.version).toBe(2);
    expect(next.reportedOn).toBeNull();
    const stale = await request.put(path + "/" + initial.id, {headers, data: {
      externalReference: "STALE-SIN", occurredOn: "2026-11-03",
      reportedOn: null, expectedVersion: 1
    }});
    expect(stale.status()).toBe(409);
    const persisted = await request.get(path + "/" + initial.id, {headers});
    expect((await persisted.json() as {externalReference:string}).externalReference).toBe(reference + "-EDIT");

    const mcpChanged = await mcp("update_sinistro", {
      id: initial.id, externalReference: reference + "-MCP", occurredOn: "2026-11-04",
      expectedVersion: 2, reportedOn: null
    }, 603);
    expect(mcpChanged).toContain(reference + "-MCP");
    const final = await request.get(path + "/" + initial.id, {headers});
    expect((await final.json() as {version:number}).version).toBe(3);

    const anonymous = await request.get(path + "/" + initial.id);
    expect(anonymous.status()).toBe(401);
    const missing = await request.get(path + "/00000000-0000-0000-0000-000000000001", {headers});
    expect(missing.status()).toBe(404);
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
