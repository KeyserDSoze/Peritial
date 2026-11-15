import { defineConfig, devices } from "@playwright/test";
export default defineConfig({
  testDir:"./.playwright-specs",fullyParallel:true,
  use:{baseURL:process.env.E2E_BASE_URL || "http://127.0.0.1:4173",trace:"retain-on-failure"},
  projects:[{name:"chromium",use:{...devices["Desktop Chrome"]}},{name:"mobile",use:{...devices["Pixel 7"]}}],
  webServer: process.env.E2E_FULL_STACK ? undefined : {command:"npm run dev -- --host 127.0.0.1 --port 4173",port:4173,reuseExistingServer:!process.env.CI,timeout:60000}
});