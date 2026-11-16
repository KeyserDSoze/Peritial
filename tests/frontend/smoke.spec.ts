import { test, expect } from "@playwright/test";
test("PWA protetta con switch lingua",async({page})=>{
  await page.goto("/");
  await expect(page.getByRole("heading",{name:"Gestione perizie"})).toBeVisible();
  await page.getByRole("button",{name:"Lingua"}).click();
  await expect(page.getByRole("heading",{name:"Appraisal management"})).toBeVisible();
});
test("tema scuro accessibile via tastiera",async({page})=>{
  await page.goto("/");
  const toggle=page.getByRole("button",{name:"Cambia tema"});
  await toggle.focus();await page.keyboard.press("Enter");
  await expect(page.locator("html")).toHaveClass(/dark/);
});

test("preferenze lingua e tema persistono dopo refresh su desktop e mobile", async ({page}) => {
  await page.goto("/");
  await expect(page.locator("html")).toHaveAttribute("lang", "it");
  await page.getByRole("button", {name: "Lingua"}).click();
  await expect(page.getByRole("heading", {name: "Appraisal management"})).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("lang", "en");

  const themeToggle = page.getByRole("button", {name: "Toggle theme"});
  await themeToggle.focus();
  await page.keyboard.press("Enter");
  await expect(page.locator("html")).toHaveClass(/dark/);
  await page.reload();
  await expect(page.getByRole("heading", {name: "Appraisal management"})).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("lang", "en");
  await expect(page.locator("html")).toHaveClass(/dark/);
  expect(await page.evaluate(() => ({
    language: localStorage.getItem("peritial.ui.language"),
    theme: localStorage.getItem("peritial.ui.theme")
  }))).toEqual({language: "en", theme: "dark"});
});

test("preferenze sconosciute vengono ignorate senza eseguire contenuti memorizzati", async ({page}) => {
  await page.addInitScript(() => {
    localStorage.setItem("peritial.ui.language", "fr");
    localStorage.setItem("peritial.ui.theme", "<script>alert(1)</script>");
  });
  await page.goto("/");
  await expect(page.getByRole("heading", {name: "Gestione perizie"})).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("lang", "it");
  await expect(page.locator("html")).not.toHaveClass(/dark/);
  // The toggles must recover cleanly from invalid stored values.
  await page.getByRole("button", {name: "Cambia tema"}).click();
  await page.getByRole("button", {name: "Lingua"}).click();
  await expect(page.locator("html")).toHaveAttribute("lang", "en");
  await expect(page.locator("html")).toHaveClass(/dark/);
});


test("preferenze in sessione funzionano con accesso alle chiavi UI negato", async ({page}) => {
  await page.addInitScript(() => {
    // Simulate a storage policy that denies the UI preference keys only.
    // Do not block the auth library's unrelated session/browser storage.
    const originalRead = Storage.prototype.getItem;
    const originalWrite = Storage.prototype.setItem;
    Storage.prototype.getItem = function (key: string) {
      if (key.startsWith("peritial.ui.")) throw new DOMException("Storage denied", "SecurityError");
      return originalRead.call(this, key);
    };
    Storage.prototype.setItem = function (key: string, value: string) {
      if (key.startsWith("peritial.ui.")) throw new DOMException("Storage denied", "SecurityError");
      return originalWrite.call(this, key, value);
    };
  });
  await page.goto("/");
  await expect(page.getByRole("heading", {name: "Gestione perizie"})).toBeVisible();
  const theme = page.getByRole("button", {name: "Cambia tema"});
  await expect(theme).toHaveAttribute("aria-pressed", "false");
  await theme.click();
  await expect(theme).toHaveAttribute("aria-pressed", "true");
  await page.getByRole("button", {name: "Lingua"}).click();
  await expect(page.locator("html")).toHaveAttribute("lang", "en");
  await expect(page.locator("html")).toHaveClass(/dark/);
  await page.reload();
  // Preferences cannot persist in a blocked browser; secure defaults apply.
  await expect(page.getByRole("heading", {name: "Gestione perizie"})).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("lang", "it");
  await expect(page.locator("html")).not.toHaveClass(/dark/);
});


test("modalità offline dichiara il limite, nasconde il login e recupera online", async ({page, context}) => {
  await page.goto("/");
  await page.getByRole("button", {name: "Lingua"}).click();
  await expect(page.getByRole("heading", {name: "Appraisal management"})).toBeVisible();
  await context.setOffline(true);
  await expect(page.getByRole("status")).toContainText("offline");
  await expect(page.getByRole("status")).toContainText("No changes");
  await expect(page.getByRole("button", {name: "Sign in"})).toHaveCount(0);
  await expect(page.getByRole("button", {name: "Upload document"})).toHaveCount(0);
  await context.setOffline(false);
  await expect(page.getByRole("status")).toHaveCount(0);
  await expect(page.getByRole("button", {name: "Sign in"})).toBeVisible();
});
