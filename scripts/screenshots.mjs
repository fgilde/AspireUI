// Capture docs screenshots of AspireUI in two themes (GitHub Dark + Blazor) for the before/after
// sliders. Drives an installed Chrome via playwright-core against a throwaway seeded instance.
//
// Run against a seeded instance (admin creds via env below) e.g.:
//   ASPNETCORE_URLS=http://localhost:5199 ASPIREUI_ADMIN_USERNAME=demo ASPIREUI_ADMIN_PASSWORD=demopass1 \
//     dotnet run -c Release --project src/AspireUI.Server
//   node scripts/screenshots.mjs
import { chromium } from "playwright-core";
import { fileURLToPath } from "url";
import path from "path";

const BASE = process.env.SHOT_BASE || "http://localhost:5199";
const USER = process.env.SHOT_USER || "demo";
const PASS = process.env.SHOT_PASS || "demopass1";
const CHROME = process.env.SHOT_CHROME || "C:/Program Files/Google/Chrome/Application/chrome.exe";
const OUT = process.env.SHOT_OUT || path.resolve(fileURLToPath(import.meta.url), "../../docs/screenshots");
const THEMES = [{ id: "github-dark", tag: "github-dark" }, { id: "blazor", tag: "blazor" }];

// A rich-but-simple demo stack so the editor screenshot has something to show.
const demoStack = {
  id: "", name: "Demo Shop", targetFramework: "net10.0", rawStatements: [], extraFiles: [], extraPackages: [],
  nodes: [
    { id: "n_web", varName: "web", addMethod: "AddContainer", resourceName: "web", addArgs: ["\"ghcr.io/acme/shop-web:latest\""], withCalls: [{ method: "WithHttpEndpoint", args: ["targetPort: 8080"] }, { method: "WithExternalHttpEndpoints", args: [] }], x: 80, y: 80, icon: null },
    { id: "n_api", varName: "api", addMethod: "AddContainer", resourceName: "api", addArgs: ["\"ghcr.io/acme/shop-api:latest\""], withCalls: [{ method: "WithHttpEndpoint", args: ["targetPort: 8080"] }], x: 80, y: 300, icon: null },
    { id: "n_db", varName: "postgres", addMethod: "AddPostgres", resourceName: "postgres", addArgs: [], withCalls: [], x: 460, y: 240, icon: null },
    { id: "n_cache", varName: "cache", addMethod: "AddRedis", resourceName: "cache", addArgs: [], withCalls: [], x: 460, y: 60, icon: null },
    { id: "n_pw", varName: "apisecret", addMethod: "AddParameter", resourceName: "api-secret", addArgs: ["\"change-me\"", "false", "true"], withCalls: [{ method: "WithEnvironment", args: ["\"API_SECRET\"", "apisecret"] }], x: 460, y: 420, icon: null, spawnedBy: "n_api" },
  ],
  edges: [
    { id: "e1", fromNodeId: "n_web", toNodeId: "n_api", kind: "reference" },
    { id: "e2", fromNodeId: "n_api", toNodeId: "n_db", kind: "reference" },
    { id: "e3", fromNodeId: "n_api", toNodeId: "n_db", kind: "waitFor" },
    { id: "e4", fromNodeId: "n_api", toNodeId: "n_cache", kind: "reference" },
  ],
};

const shot = (page, name) => page.screenshot({ path: path.join(OUT, name), animations: "disabled" });
const wait = ms => new Promise(r => setTimeout(r, ms));

const browser = await chromium.launch({ executablePath: CHROME, headless: true });
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 });
const page = await ctx.newPage();

// Login. Go to the SPA root (server only serves index.html at "/"); the client routes to /login.
await page.goto(`${BASE}/`, { waitUntil: "domcontentloaded" });
await page.getByLabel("Username").waitFor({ timeout: 15000 });
await page.getByLabel("Username").fill(USER);
await page.getByLabel("Password").fill(PASS);
await page.getByRole("button", { name: "Sign in" }).click();
await page.waitForURL(u => !u.pathname.endsWith("/login"), { timeout: 15000 }).catch(() => {});
await wait(800);

// Create the demo stack via the session cookie, get its id.
const stackId = await page.evaluate(async (body) => {
  const r = await fetch("/stacks", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
  const s = await r.json(); return s.id;
}, demoStack);

for (const t of THEMES) {
  await page.evaluate(id => localStorage.setItem("aspireui.theme", id), t.id);

  // Overview (server serves index.html at "/"; client routes handle the rest).
  await page.goto(`${BASE}/`, { waitUntil: "domcontentloaded" }); await wait(1200);
  await shot(page, `overview-${t.tag}.png`);

  // Editor — reach it by clicking the demo stack card (direct nav to a client route 404s server-side).
  try {
    await page.getByText("Demo Shop", { exact: false }).first().click();
    await page.waitForSelector(".react-flow", { timeout: 15000 });
    await wait(2000);
    await shot(page, `editor-${t.tag}.png`);
  } catch (e) { console.log("editor capture failed:", e.message); }

  // Command palette (Ctrl+K) over the editor.
  try { await page.keyboard.press("Control+K"); await wait(600); await shot(page, `command-palette-${t.tag}.png`); await page.keyboard.press("Escape"); } catch {}
}

await browser.close();
console.log("screenshots written to", OUT);
