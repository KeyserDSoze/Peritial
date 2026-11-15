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