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
      new URL(response.url()).pathname === "/api/files" &&
      new URL(response.url()).searchParams.get("periziaId") === firstId);
    await filter.selectOption(firstId);
    expect((await firstGet).status()).toBe(200);
    await expect(list.getByText(firstName, {exact: true})).toBeVisible();
    await expect(list.getByText(secondName, {exact: true})).toHaveCount(0);

    const secondGet = page.waitForResponse(response =>
      response.request().method() === "GET" &&
      new URL(response.url()).pathname === "/api/files" &&
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
    await row.getByRole("button", {name: "Collega sinistro"}).click();
    expect((await linked).status()).toBe(201);
    await expect(row.getByText("Sinistro collegato")).toBeVisible();
    await expect(row.getByText(sinRef)).toBeVisible();
    await expect(row.getByRole("button", {name: "Collega sinistro"})).toHaveCount(0);

    // Reload must not mistake a pre-existing link for an unlinked record.
    await page.reload();
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/})
      .getByText(reference)).toBeVisible({timeout: 30_000});
    const restored = page.getByRole("list", {name: /^(Perizie|Appraisals)$/})
      .locator("li").filter({hasText: reference});
    await restored.getByRole("button", {name: "Sinistro della perizia"}).click();
    await expect(restored.getByText(sinRef)).toBeVisible({timeout: 30_000});
    await expect(restored.getByRole("button", {name: "Collega sinistro"})).toHaveCount(0);

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
      {headers, data: {sinistroId: sinId}}
    );
    expect(externalLink.status(), await externalLink.text()).toBe(201);
    await raceRow.getByRole("button", {name: "Collega sinistro"}).click();
    await expect(raceRow.getByRole("alert")).toContainText("Ricarica il collegamento");
    await expect(raceRow.getByRole("button", {name: "Collega sinistro"})).toHaveCount(0);
    await raceRow.getByRole("button", {name: "Ricarica collegamento"}).click();
    await expect(raceRow.getByText(sinRef)).toBeVisible({timeout: 30_000});
    await expect(raceRow.getByRole("alert")).toHaveCount(0);

    // Offline does not expose retained tenant data or allow queued writes.
    await context.setOffline(true);
    await expect(page.getByRole("status")).toContainText("Sei offline");
    await expect(page.getByText(reference, {exact:true})).toHaveCount(0);
    await expect(page.getByRole("button", {name: "Collega sinistro"})).toHaveCount(0);
    await context.setOffline(false);
    await expect(page.getByRole("list", {name: /^(Perizie|Appraisals)$/})
      .getByText(reference, {exact:true})).toBeVisible({timeout: 30_000});
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
    const list = await request.get(path + "?limit=10", {headers});
    expect(list.status()).toBe(200);
    expect(await list.text()).toContain(reference);
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
    expect(await mcp("list_sinistri", {limit: 10}, 602)).toContain(reference);

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
