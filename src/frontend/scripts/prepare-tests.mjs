import { cpSync, mkdirSync } from "node:fs";
const source = new URL("../../../tests/frontend/", import.meta.url);
const target = new URL("../.playwright-specs/", import.meta.url);
mkdirSync(target, { recursive: true });
cpSync(source, target, { recursive: true, force: true });
// Source-of-truth remains tests/frontend; specs are copied into package scope
// so Node resolves @playwright/test from src/frontend/node_modules.
