import test from "node:test";
import assert from "node:assert/strict";
import {readFileSync} from "node:fs";

const source = path => readFileSync(new URL(path, import.meta.url), "utf8");

test("the production PWA never forces a service-worker update", () => {
  const config = source("../vite.config.ts");
  assert.match(config, /registerType:\s*["']prompt["']/);
  assert.match(config, /injectRegister:\s*false/);
  assert.match(config, /skipWaiting:\s*false/);
  assert.match(config, /clientsClaim:\s*false/);
  assert.doesNotMatch(config, /registerType:\s*["']autoUpdate["']/);
  assert.match(config, /runtimeCaching:\s*\[\s*\]/);
});

test("update availability is visible, deferable and activation requires a click", () => {
  const ui = source("../src/components/PwaUpdatePrompt.tsx");
  const app = source("../src/App.tsx");
  assert.match(ui, /registerSW\(/);
  assert.match(ui, /onNeedRefresh\s*:/);
  assert.match(ui, /onClick=\{applyUpdate\}/);
  assert.match(ui, /onClick=\{deferUpdate\}/);
  assert.match(ui, /onClick=\{reopenUpdate\}/);
  assert.match(ui, /role="status"/);
  assert.match(ui, /updateServiceWorker\(/);
  assert.match(app, /<PwaUpdatePrompt\s*\/>/);
});

test("production HTML does not inject an automatic service-worker registrar", () => {
  const html = source("../dist/index.html");
  assert.doesNotMatch(html, /registerSW\.js/i);
  assert.doesNotMatch(html, /navigator\.serviceWorker\.register/i);
  const serviceWorker = source("../dist/sw.js");
  assert.ok(serviceWorker.length > 0, "production service worker must be generated");
});
