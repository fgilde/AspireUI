// Capture docs screenshots of AspireUI in two themes (GitHub Dark + Blazor) for the before/after
// sliders + panel/dialog figures. Drives an installed Chrome via playwright-core against a throwaway
// seeded instance.
//
// Seed + run (Debug is fine, avoids locking the Release binary of a running instance):
//   ASPNETCORE_URLS=http://localhost:5199 ASPNETCORE_ENVIRONMENT=Development \
//   DB_PATH=.../shots.db WORKSPACE_DIR=.../ws \
//   ASPIREUI_ADMIN_USERNAME=demo ASPIREUI_ADMIN_PASSWORD=demopass1 \
//   ASPIREUI_AI_BASE_URL=http://localhost:9 ASPIREUI_AI_MODEL=demo \
//     dotnet run --no-launch-profile --project src/AspireUI.Server
//   SHOT_BASE=http://localhost:5199 SHOT_OUT=.../docs/screenshots node scripts/screenshots.mjs
import { chromium } from "playwright-core";
import { fileURLToPath } from "url";
import path from "path";

const BASE = process.env.SHOT_BASE || "http://localhost:5199";
const USER = process.env.SHOT_USER || "demo";
const PASS = process.env.SHOT_PASS || "demopass1";
const CHROME = process.env.SHOT_CHROME || "C:/Program Files/Google/Chrome/Application/chrome.exe";
const OUT = process.env.SHOT_OUT || path.resolve(fileURLToPath(import.meta.url), "../../docs/screenshots");
const THEMES = [{ id: "github-dark", tag: "github-dark" }, { id: "blazor", tag: "blazor" }];
const wait = ms => new Promise(r => setTimeout(r, ms));

const demoStack = {
  id: "", name: "Demo Shop", targetFramework: "net10.0", rawStatements: [], extraFiles: [], extraPackages: [],
  nodes: [
    { id: "n_web", varName: "web", addMethod: "AddContainer", resourceName: "web", addArgs: ["\"ghcr.io/acme/shop-web:latest\""], withCalls: [{ method: "WithHttpEndpoint", args: ["targetPort: 8080"] }, { method: "WithExternalHttpEndpoints", args: [] }], x: 60, y: 80, icon: null },
    { id: "n_api", varName: "api", addMethod: "AddContainer", resourceName: "api", addArgs: ["\"ghcr.io/acme/shop-api:latest\""], withCalls: [{ method: "WithHttpEndpoint", args: ["targetPort: 8080"] }], x: 60, y: 320, icon: null },
    { id: "n_db", varName: "postgres", addMethod: "AddPostgres", resourceName: "postgres", addArgs: [], withCalls: [], x: 440, y: 250, icon: null },
    { id: "n_cache", varName: "cache", addMethod: "AddRedis", resourceName: "cache", addArgs: [], withCalls: [], x: 440, y: 70, icon: null },
    { id: "n_pw", varName: "apisecret", addMethod: "AddParameter", resourceName: "api-secret", addArgs: ["\"change-me\"", "false", "true"], withCalls: [{ method: "WithEnvironment", args: ["\"API_SECRET\"", "apisecret"] }], x: 440, y: 430, icon: null, spawnedBy: "n_api" },
  ],
  edges: [
    { id: "e1", fromNodeId: "n_web", toNodeId: "n_api", kind: "reference" },
    { id: "e2", fromNodeId: "n_api", toNodeId: "n_db", kind: "reference" },
    { id: "e3", fromNodeId: "n_api", toNodeId: "n_db", kind: "waitFor" },
    { id: "e4", fromNodeId: "n_api", toNodeId: "n_cache", kind: "reference" },
  ],
};

const browser = await chromium.launch({ executablePath: CHROME, headless: true });
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 });
const page = await ctx.newPage();
const full = name => page.screenshot({ path: path.join(OUT, name), animations: "disabled" });
async function el(selector, name) {
  const loc = page.locator(selector).first();
  await loc.waitFor({ timeout: 6000 });
  await wait(300);
  await loc.screenshot({ path: path.join(OUT, name), animations: "disabled" });
}
const step = async (label, fn) => { try { await fn(); } catch (e) { console.log("skip", label, "-", e.message.split("\n")[0]); } };

// Login.
await page.goto(`${BASE}/`, { waitUntil: "domcontentloaded" });
await page.getByLabel("Username").waitFor({ timeout: 20000 });
await page.getByLabel("Username").fill(USER);
await page.locator("input[type=password]").first().fill(PASS);
await page.getByRole("button", { name: "Sign in" }).click();
await page.waitForURL(u => !u.pathname.endsWith("/login"), { timeout: 15000 }).catch(() => {});
await wait(900);

const stackId = await page.evaluate(async (body) => {
  const r = await fetch("/api/stacks", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
  return (await r.json()).id;
}, demoStack);

const openEditor = async () => {
  await page.getByText("Demo Shop", { exact: false }).first().click();
  await page.waitForSelector(".react-flow", { timeout: 15000 });
  await wait(2000);
};
const bottomTab = async (label) => { await page.getByRole("tab", { name: label }).first().click().catch(async () => { await page.getByText(label, { exact: true }).first().click(); }); await wait(1200); };

for (const t of THEMES) {
  await page.evaluate(id => localStorage.setItem("aspireui.theme", id), t.id);

  // Overview.
  await step("overview", async () => { await page.goto(`${BASE}/`, { waitUntil: "domcontentloaded" }); await wait(1400); await full(`overview-${t.tag}.png`); });

  // Editor.
  await step("editor", async () => { await openEditor(); await full(`editor-${t.tag}.png`); });

  // Palette: Custom tab.
  await step("palette-custom", async () => { await page.getByRole("tab", { name: "Custom" }).first().click(); await wait(800); await full(`palette-custom-${t.tag}.png`); await page.getByRole("tab", { name: "Catalog" }).first().click(); await wait(400); });

  // Add-resource dialog (search redis, click the tile).
  await step("add-dialog", async () => {
    await page.getByPlaceholder("Search…").first().fill("redis"); await wait(600);
    await page.getByText("Redis", { exact: false }).first().click(); await wait(900);
    await el(".mantine-Modal-content", `add-dialog-${t.tag}.png`);
    await page.keyboard.press("Escape"); await wait(400);
    await page.getByPlaceholder("Search…").first().fill("");
  });

  // Companion picker (Immich → Postgres + Redis + Meilisearch dependencies).
  await step("companion-picker", async () => {
    await page.getByPlaceholder("Search…").first().fill("immich"); await wait(600);
    await page.getByText("Immich", { exact: false }).first().click(); await wait(900);
    await el(".mantine-Modal-content", `companion-picker-${t.tag}.png`);
    await page.keyboard.press("Escape"); await wait(400);
    await page.getByPlaceholder("Search…").first().fill("");
  });

  // Property grid — select a node.
  await step("property-grid", async () => {
    await page.locator(".react-flow__node").first().click(); await wait(900);
    await full(`property-grid-${t.tag}.png`);
  });

  // Code panel with the selected node's lines highlighted (the "learn Aspire" feature). Use the exact
  // "Code" tab (not "Code Preview") — it's the editable Monaco panel that draws the highlight.
  await step("code-highlight", async () => {
    await page.getByRole("tab", { name: "Code", exact: true }).first().click();
    await wait(2000);
    await full(`code-highlight-${t.tag}.png`);
  });

  // Assistant, Dashboard, Validation panels.
  await step("assistant", async () => { await bottomTab("Assistant"); await full(`assistant-${t.tag}.png`); });
  await step("validation", async () => { await bottomTab("Validation"); await full(`validation-${t.tag}.png`); });
  await step("dashboard", async () => { await bottomTab("Dashboard"); await full(`dashboard-${t.tag}.png`); });

  // Theme drawer (open the account menu, then Theme). Don't click a theme — it would switch.
  await step("theme-drawer", async () => {
    await page.getByLabel("Account menu").click(); await wait(500);
    await page.getByText("Theme", { exact: false }).first().click(); await wait(800);
    await el(".mantine-Drawer-content", `theme-drawer-${t.tag}.png`);
    await page.keyboard.press("Escape"); await wait(400);
  });

  // Command palette.
  await step("command-palette", async () => { await page.keyboard.press("Control+K"); await wait(600); await full(`command-palette-${t.tag}.png`); await page.keyboard.press("Escape"); });
}

await browser.close();
console.log("screenshots written to", OUT);
