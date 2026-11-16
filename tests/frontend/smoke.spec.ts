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


test("preferenze in sessione funzionano anche quando il browser blocca localStorage", async ({page}) => {
  await page.addInitScript(() => {
    Object.defineProperty(window, "localStorage", {
      configurable: true,
      get() { throw new DOMException("Storage denied", "SecurityError"); }
    });
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
