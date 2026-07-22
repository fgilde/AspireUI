// Baked in by vite (see vite.config.ts): version = 0.1.<commit-count>, build = "<sha> · <date>".
declare const __APP_VERSION__: string;
declare const __BUILD__: string;
export const APP_VERSION: string = typeof __APP_VERSION__ !== "undefined" ? __APP_VERSION__ : "0.1.0";
export const BUILD_INFO: string = typeof __BUILD__ !== "undefined" ? __BUILD__ : "dev";

export interface WithCall { method: string; args: string[] }
export interface Node { id: string; varName: string; addMethod: string; resourceName: string; withCalls: WithCall[]; x: number; y: number; addArgs: string[]; composite?: boolean; usings?: string[]; spawnedBy?: string | null; icon?: string | null }
export interface Edge { id: string; fromNodeId: string; toNodeId: string; kind: string }
export interface ExtraFile { name: string; content: string }
export interface PackageRef { id: string; version: string }
export interface StackNote { id: string; text: string; x: number; y: number }
export interface StackGroup { id: string; label: string; x: number; y: number; width: number; height: number; color?: string | null }
export interface Stack {
  id: string; name: string; targetFramework: string; nodes: Node[]; edges: Edge[]; rawStatements: string[];
  extraFiles: ExtraFile[]; extraPackages: PackageRef[];
  notes?: StackNote[]; groups?: StackGroup[];
  createdAt?: string | null; createdBy?: string | null;
}

export interface CatalogParam { name: string; type: "string" | "int" | "number" | "bool" | "enum" | "configure" | "resourceRef"; required: boolean; default?: string | null; options?: string[] | null; enumTypeName?: string | null; label: string; fields?: CatalogParam[] | null }
export interface CatalogOverload { params: CatalogParam[] }
export interface CatalogMethod { method: string; label: string; overloads: CatalogOverload[] }
export interface ResourceType { addMethod: string; label: string; icon?: string | null; group?: string | null; description?: string | null; addOverloads: CatalogOverload[]; withs: CatalogMethod[]; composite?: boolean; usings?: string[] | null; package?: string | null; packageVersion?: string | null; resourceTypeName?: string | null }
// A curated one-click app preset → a preconfigured AddContainer node (image + HTTP endpoint + env).
export interface PresetCompanion { key: string; addMethod: string; resourceName: string; image?: string | null; port?: number | null; env?: string[][] | null; role?: string | null }
// A configurable value the app needs (password, key, or plain setting). On drop the user picks per
// param: a new Aspire parameter resource (AddParameter), reuse an existing parameter, or just write a
// literal env value. `secret` marks it sensitive (AddParameter secret:true, masked). `default` seeds
// the value; `name` overrides the generated parameter resource name.
export interface PresetParam { key: string; env: string; default?: string | null; secret?: boolean; name?: string | null }
export interface ContainerPreset { id: string; label: string; group: string; image: string; port: number; icon?: string | null; description?: string | null; env?: string[][] | null; params?: PresetParam[] | null; companions?: PresetCompanion[] | null; volumes?: string[][] | null; bindMounts?: string[][] | null; files?: ExtraFile[] | null; args?: string[] | null; gpu?: boolean; hostNetwork?: boolean; fixedPort?: boolean }

// What existing resources satisfy a companion role (reuse), and which Aspire resources can stand in
// as alternatives to the default container. Drives the "reuse / new container / Aspire alternative"
// choice on drop, so a stack doesn't end up with duplicate Postgres/LLM/etc.
export const ROLE_MATCHERS: Record<string, { addMethods: string[]; images: string[] }> = {
  postgres: { addMethods: ["AddPostgres"], images: ["postgres", "pgvecto", "vectorchord"] },
  redis: { addMethods: ["AddRedis", "AddValkey", "AddGarnet"], images: ["redis", "valkey"] },
  mongo: { addMethods: ["AddMongoDB"], images: ["mongo"] },
  meilisearch: { addMethods: ["AddMeilisearch"], images: ["meilisearch"] },
  llm: { addMethods: ["AddLocalAI", "AddOllama"], images: ["localai", "ollama"] },
};
export const ROLE_ALTERNATIVES: Record<string, { addMethod: string; label: string }[]> = {
  postgres: [{ addMethod: "AddPostgres", label: "Aspire Postgres" }],
  redis: [{ addMethod: "AddRedis", label: "Aspire Redis" }, { addMethod: "AddValkey", label: "Aspire Valkey" }],
  mongo: [{ addMethod: "AddMongoDB", label: "Aspire MongoDB" }],
  meilisearch: [{ addMethod: "AddMeilisearch", label: "Aspire Meilisearch" }],
  llm: [{ addMethod: "AddLocalAI", label: "LocalAI (Nextended)" }, { addMethod: "AddOllama", label: "Ollama" }],
};
// Existing parameter resources on the stack — candidates to reuse for a preset secret.
export function parameterCandidates(nodes: Node[]): Node[] {
  return nodes.filter(n => n.addMethod === "AddParameter");
}
// Existing nodes that satisfy a companion role — candidates to reuse instead of dropping a new one.
export function reuseCandidates(nodes: Node[], role?: string | null): Node[] {
  if (!role || !ROLE_MATCHERS[role]) return [];
  const m = ROLE_MATCHERS[role];
  return nodes.filter(n =>
    m.addMethods.includes(n.addMethod) ||
    (n.addMethod === "AddContainer" && n.addArgs.some(a => m.images.some(img => a.toLowerCase().includes(img)))));
}
// Per-companion choice on drop: reuse an existing node, drop the default container, or add an Aspire
// resource as the backend.
export type CompanionChoice =
  | { mode: "reuse"; nodeId: string }
  | { mode: "new" }
  | { mode: "add"; addMethod: string }
  | { mode: "value"; value: string };   // params only: a literal env value instead of a parameter resource

// Companion icon from an image so DB/cache containers show a real brand icon too.
function iconForImage(img?: string | null): string | undefined {
  const i = (img ?? "").toLowerCase();
  if (/postgres|pgvecto|vectorchord/.test(i)) return "AddPostgres";
  if (/redis/.test(i)) return "AddRedis";
  if (/mongo/.test(i)) return "AddMongoDB";
  if (/meilisearch/.test(i)) return "AddMeilisearch";
  if (/localai/.test(i)) return "AddLocalAI";
  if (/ollama/.test(i)) return "AddOllama";
  return undefined;
}

// Build the node(s) + edges a preset drops onto the canvas.
// - `existing` = nodes already on the stack (for reuse + name dedup).
// - `choices` = per-companion-key decision (reuse existing / new container / add an Aspire resource).
//   Omitted → default per companion: reuse the first matching existing resource, else a new container.
//   Pass "none" to drop just the app (no companions).
// An env value's `${key}` token expands to the chosen backend's resource NAME (its on-network
// hostname) as a quoted string. Companions/backends are wired with a waitFor edge from the app.
export function buildPresetNodes(
  preset: ContainerPreset,
  existing: Node[] = [],
  choices?: Record<string, CompanionChoice> | "none",
): { nodes: Node[]; edges: Edge[] } {
  const taken = new Set(existing.map(n => n.resourceName));
  const uniq = (base: string) => { let n = base, i = 2; while (taken.has(n)) n = `${base}${i++}`; taken.add(n); return n; };
  const nid = () => "n" + crypto.randomUUID().slice(0, 8);
  const eid = () => "e" + crypto.randomUUID().slice(0, 8);

  const companions = choices === "none" ? [] : (preset.companions ?? []);
  const mainName = uniq(preset.id);
  const mainId = nid();

  const defaultChoice = (c: PresetCompanion): CompanionChoice => {
    const cand = reuseCandidates(existing, c.role)[0];
    return cand ? { mode: "reuse", nodeId: cand.id } : { mode: "new" };
  };

  // Pass 1: resolve each companion to its backend resource name + decide whether a node is needed.
  interface Plan { c: PresetCompanion; choice: CompanionChoice; name: string; targetId: string; create: boolean; }
  const keyName: Record<string, string> = { __main: mainName };
  const plans: Plan[] = companions.map(c => {
    const choice = (choices && choices !== "none" && choices[c.key]) || defaultChoice(c);
    if (choice.mode === "reuse") {
      const node = existing.find(n => n.id === choice.nodeId);
      if (node) { keyName[c.key] = node.resourceName; return { c, choice, name: node.resourceName, targetId: node.id, create: false }; }
    }
    const rn = uniq(c.resourceName || c.key);
    keyName[c.key] = rn;
    return { c, choice, name: rn, targetId: nid(), create: true };
  });

  // knownKeys ready → env token expansion is safe.
  const knownKeys = new Set(Object.keys(keyName));
  const expandEnv = (env?: string[][] | null) => (env ?? []).flatMap(([k, v]) => {
    const refs = [...v.matchAll(/\$\{([^}]+)\}/g)].map(m => m[1]);
    if (refs.some(r => !knownKeys.has(r))) return [];
    let val = v;
    for (const [key, name] of Object.entries(keyName)) val = val.split(`\${${key}}`).join(name);
    return [{ method: "WithEnvironment", args: [JSON.stringify(k), JSON.stringify(val)] }];
  });

  // Params → either an Aspire parameter resource (new / reused; referenced via WithEnvironment(env,
  // <paramVar>) with the varName emitted UNQUOTED so codegen treats it as a builder reference) or a
  // plain literal env value ("value" mode). Default (no choice) = a new parameter.
  interface ParamPlan { param: PresetParam; mode: "value" | "reuse" | "new"; value?: string; varName?: string; targetId?: string; name?: string; }
  const params = preset.params ?? [];
  const paramPlans: ParamPlan[] = params.map(param => {
    const choice = choices && choices !== "none" ? choices[`param:${param.key}`] : undefined;
    if (choice?.mode === "value") return { param, mode: "value", value: choice.value };
    if (choice?.mode === "reuse") {
      const node = existing.find(n => n.id === choice.nodeId);
      if (node) return { param, mode: "reuse", varName: node.varName, targetId: node.id };
    }
    const pname = uniq(param.name || `${preset.id}-${param.key}`);
    return { param, mode: "new", varName: sanitizeIdentifier(pname), targetId: nid(), name: pname };
  });
  const paramEnvCalls = paramPlans.map(p => p.mode === "value"
    ? { method: "WithEnvironment", args: [JSON.stringify(p.param.env), JSON.stringify(p.value ?? p.param.default ?? "")] }
    : { method: "WithEnvironment", args: [JSON.stringify(p.param.env), p.varName!] });

  // Pass 2: build nodes.
  const volumeCalls = (preset.volumes ?? []).map(([name, target]) =>
    ({ method: "WithVolume", args: [JSON.stringify(`${mainName}-${name}`), JSON.stringify(target)] }));
  // Bind mounts map a HOST path into the container (e.g. Dozzle needs /var/run/docker.sock) — a real
  // host path, unlike named volumes. Optional 3rd element "ro" mounts it read-only.
  const bindCalls = (preset.bindMounts ?? []).map(([source, target, ro]) =>
    ({ method: "WithBindMount", args: [JSON.stringify(source), JSON.stringify(target), ...(ro === "ro" ? ["isReadOnly: true"] : [])] }));
  const gpuCalls = preset.gpu ? [{ method: "WithContainerRuntimeArgs", args: ['"--gpus"', '"all"'] }] : [];
  // Container command args (e.g. ntfy needs the `serve` subcommand or it just prints help and exits).
  const argsCalls = preset.args?.length ? [{ method: "WithArgs", args: preset.args.map(a => JSON.stringify(a)) }] : [];
  const main: Node = {
    id: mainId, varName: sanitizeIdentifier(mainName), resourceName: mainName, addMethod: "AddContainer",
    addArgs: [JSON.stringify(preset.image)],
    // fixedPort pins the published host port to the container port (port: == targetPort:) — needed for
    // apps whose WebUI rejects a mismatched host port (qBittorrent's CSRF/host-header check → "Unauthorized").
    withCalls: [{ method: "WithHttpEndpoint", args: preset.fixedPort ? [`port: ${preset.port}`, `targetPort: ${preset.port}`] : [`targetPort: ${preset.port}`] }, ...gpuCalls, ...argsCalls, ...volumeCalls, ...bindCalls, ...expandEnv(preset.env), ...paramEnvCalls],
    x: 60, y: 60, icon: preset.icon ?? undefined,
  };
  const nodes: Node[] = [main];
  const edges: Edge[] = [];
  // New parameter nodes (reused ones already exist on the canvas; "value" mode adds no node).
  paramPlans.filter(p => p.mode === "new").forEach((p, i) => {
    nodes.push({
      // AddParameter has no 2-arg overload — only (name, bool secret) and
      // (name, string value, bool publishValueAsDefault, bool secret). Emit the 3-arg form positionally
      // → AddParameter("name", "value", false, <secret>) so the grid's arity-3 overload renders the
      // value field + both switches and round-trips (2 args would mis-match the 1-arg [secret] overload).
      id: p.targetId!, varName: p.varName!, resourceName: p.name!, addMethod: "AddParameter",
      addArgs: [JSON.stringify(p.param.default ?? ""), "false", p.param.secret ? "true" : "false"],
      withCalls: [], x: 380, y: 40 + (companions.length + i) * 130, spawnedBy: mainId, icon: undefined,
    });
  });
  // NB: no explicit param edge is pushed — the canvas derives a visual "param" line from each node's
  // WithEnvironment(key, <paramVar>) reference (see Canvas `edges`), so it also shows for pre-existing
  // nodes, not just freshly-dropped ones.
  plans.forEach((p, i) => {
    if (p.create) {
      const isAdd = p.choice.mode === "add";
      nodes.push({
        id: p.targetId, varName: sanitizeIdentifier(p.name), resourceName: p.name,
        addMethod: isAdd ? (p.choice as { addMethod: string }).addMethod : p.c.addMethod,
        addArgs: isAdd ? [] : (p.c.image ? [JSON.stringify(p.c.image)] : []),
        withCalls: isAdd ? [] : [...(p.c.port ? [{ method: "WithHttpEndpoint", args: [`targetPort: ${p.c.port}`] }] : []), ...expandEnv(p.c.env)],
        x: 380, y: 40 + i * 130, spawnedBy: mainId, icon: isAdd ? undefined : iconForImage(p.c.image),
      });
    }
    // waitFor from the app to each backend (new, added, or reused existing).
    edges.push({ id: eid(), fromNodeId: mainId, toNodeId: p.targetId, kind: "waitFor" });
  });
  return { nodes, edges };
}
export type RunState = "NotRunning" | "Starting" | "Running" | "Failed";
export interface RunStatus { state: RunState; dashboardUrl?: string | null; log: string[] }

// Live per-resource data from the running AppHost's Aspire resource service (see ResourceGraphService).
export interface LiveUrl { name?: string | null; url: string; isInternal: boolean; isInactive: boolean }
export interface LiveCommand { name: string; displayName: string; enabled: boolean; confirmationMessage?: string | null; iconName?: string | null }
export interface LiveResource { name: string; displayName: string; type: string; state?: string | null; stateStyle?: string | null; parent?: string | null; urls: LiveUrl[]; hidden: boolean; commands: LiveCommand[] }

export function liveStateColor(state?: string | null): string {
  if (!state) return "gray";
  const s = state.toLowerCase();
  if (s.includes("running") || s.includes("healthy")) return "green";
  if (s.includes("fail") || s.includes("error") || s.includes("exited") || s.includes("unhealthy")) return "red";
  if (s.includes("wait") || s.includes("start") || s.includes("pending")) return "yellow";
  return "gray";
}

// Overlay a live-resource snapshot onto the stack graph: which live resource annotates each builder
// node (top-level, matched by displayName == node.resourceName), and which are extra child/orphan
// resources to render (a builder like Supabase spawns supabase-db/-auth/… as children).
export interface LiveChild { live: LiveResource; ownerNodeId: string | null; parentElemId: string | null }
export interface LiveOverlay { statusByNodeId: Record<string, LiveResource>; children: LiveChild[] }
export function buildLiveOverlay(nodes: { id: string; resourceName: string }[], live: LiveResource[]): LiveOverlay {
  const visible = live.filter(r => !r.hidden);
  const byName = new Map(visible.map(r => [r.name, r]));
  const nodeByResName = new Map(nodes.map(n => [n.resourceName, n]));
  const topLevelNodeIdOf = (r: LiveResource): string | null => nodeByResName.get(r.displayName)?.id ?? null;

  // Walk the parent chain to the top-level ancestor that maps to a builder node (its "owner").
  const rootNodeId = (r: LiveResource): string | null => {
    let cur: LiveResource | undefined = r; const seen = new Set<string>();
    while (cur && !seen.has(cur.name)) {
      seen.add(cur.name);
      const nid = topLevelNodeIdOf(cur);
      if (nid) return nid;
      cur = cur.parent ? byName.get(cur.parent) : undefined;
    }
    return null;
  };

  const statusByNodeId: Record<string, LiveResource> = {};
  const children: LiveChild[] = [];
  for (const r of visible) {
    const nid = topLevelNodeIdOf(r);
    if (nid) { statusByNodeId[nid] = r; continue; } // annotates an existing node, not an extra node
    let parentElemId: string | null = null;
    if (r.parent) {
      const p = byName.get(r.parent);
      if (p) parentElemId = topLevelNodeIdOf(p) ?? "live:" + p.name;
    }
    children.push({ live: r, ownerNodeId: rootNodeId(r), parentElemId });
  }
  return { statusByNodeId, children };
}
export interface PublishFile { name: string; content: string }
export interface PublishResult { ok: boolean; log: string; artifactName: string | null; artifact: string | null; outputDir: string; files: PublishFile[] }
export interface DeployResult { ok: boolean; log: string }

export interface AppSettings {
  aiBaseUrl?: string | null;
  aiApiKey?: string | null;
  aiModel?: string | null;
  aiProviderLabel?: string | null;
}

export interface UserDto { id: string; username: string; isAdmin: boolean; createdAt: string }
export interface AuthStatus { needsSetup: boolean; authenticated: boolean; user: UserDto | null }
export interface EnvHealth {
  dotnet: { ok: boolean; version: string };
  docker: { ok: boolean; detail: string };
  git: { ok: boolean; detail: string };
}

// Pure mapper the AuthGate renders from: which route (if any) an unauthenticated
// or fresh-install status must be bounced to. null = the app itself is allowed.
export function routeForStatus(s: AuthStatus): "/setup" | "/login" | null {
  if (s.needsSetup) return "/setup";
  if (!s.authenticated) return "/login";
  return null;
}

export function toFlow(s: Stack) {
  return {
    nodes: s.nodes.map(n => ({
      id: n.id,
      position: { x: n.x, y: n.y },
      data: { label: `${n.resourceName} (${n.addMethod})`, node: n },
      type: "default",
    })),
    edges: s.edges.map(e => ({ id: e.id, source: e.fromNodeId, target: e.toNodeId })),
  };
}

export function applyNodePosition(s: Stack, id: string, x: number, y: number): Stack {
  return { ...s, nodes: s.nodes.map(n => n.id === id ? { ...n, x, y } : n) };
}

export function toLiteral(value: string, type: CatalogParam["type"], enumTypeName?: string | null): string {
  if (type === "int") return value === "" ? "0" : String(parseInt(value, 10));
  if (type === "number") return value === "" ? "0" : value;
  if (type === "bool") return value === "true" ? "true" : "false";
  if (type === "enum") return enumTypeName ? `${enumTypeName}.${value}` : value;
  if (type === "resourceRef") return value; // a bare varName, passed verbatim (not a string literal)
  return `"${value.replace(/\\/g, "\\\\").replace(/"/g, '\\"')}"`;
}
// Build a `o => { o.Field = literal; … }` lambda from the set sub-fields of a configure param.
// Returns "" when nothing is set, so the (optional, trailing) configure arg gets trimmed off.
export function configureLiteral(fields: CatalogParam[], get: (name: string) => string): string {
  const assigns = fields
    .filter(f => (get(f.name) ?? "") !== "")
    .map(f => `o.${f.name} = ${toLiteral(get(f.name), f.type, f.enumTypeName)};`);
  return assigns.length === 0 ? "" : `o => { ${assigns.join(" ")} }`;
}
export function fromLiteral(literal: string): string {
  const s = literal.trim();
  if (s.startsWith('"') && s.endsWith('"')) return s.slice(1, -1).replace(/\\"/g, '"').replace(/\\\\/g, "\\");
  if (s.includes(".") && !/^[0-9.]+$/.test(s)) return s.slice(s.lastIndexOf(".") + 1); // Enum.Member -> Member
  return s;
}
export function matchOverloadByArity(overloads: CatalogOverload[], argCount: number): CatalogOverload | undefined {
  if (overloads.length === 0) return undefined;
  const sorted = [...overloads].sort((a, b) => a.params.length - b.params.length);
  return sorted.find(o => o.params.length === argCount)
      ?? sorted.filter(o => o.params.length <= argCount).pop()
      ?? sorted[sorted.length - 1]; // clamp to richest
}
export function readWithRows(node: Node, method: string): string[][] {
  return node.withCalls.filter(w => w.method === method).map(w => w.args);
}
export function writeWithRows(node: Node, method: string, rows: string[][]): Node {
  const others = node.withCalls.filter(w => w.method !== method);
  const rebuilt = rows.map(args => ({ method, args }));
  return { ...node, withCalls: [...others, ...rebuilt] };
}
export function setAddArg(node: Node, index: number, literal: string): Node {
  const addArgs = [...node.addArgs];
  while (addArgs.length <= index) addArgs.push('""');
  addArgs[index] = literal;
  return { ...node, addArgs };
}
// A string param that names a filesystem location → offer the server-side path picker.
export function isPathParam(p: CatalogParam): boolean {
  return p.type === "string" && /path|dir|directory|root|file|entrypoint|script/i.test(p.name);
}
export function sanitizeIdentifier(name: string): string {
  const cleaned = name.replace(/[^A-Za-z0-9_]/g, "");
  const s = /^[0-9]/.test(cleaned) ? "_" + cleaned : cleaned;
  return s || "resource";
}
// Graph-level lint (instant, model-only) — catches issues before a run that Roslyn wouldn't flag:
// duplicate resource names, colliding fixed HTTP ports, and edges pointing at removed nodes.
// Shape matches CodeDiagnostic so it merges into the existing validation UI.
export interface LintIssue { severity: "error" | "warning"; message: string }
export function lintStack(stack: Stack): LintIssue[] {
  const issues: LintIssue[] = [];
  const nodes = stack.nodes;

  // Duplicate resource names — Aspire requires unique resource names; a dup fails at build/run.
  const byName = new Map<string, number>();
  for (const n of nodes) if (n.resourceName) byName.set(n.resourceName, (byName.get(n.resourceName) ?? 0) + 1);
  for (const [name, count] of byName) if (count > 1)
    issues.push({ severity: "error", message: `Duplicate resource name "${name}" (${count}×) — resource names must be unique.` });

  // Colliding fixed HTTP ports (WithHttpEndpoint(port: N)). Auto-assigned (no port) never collide.
  const byPort = new Map<string, string[]>();
  for (const n of nodes)
    for (const w of n.withCalls)
      if (w.method === "WithHttpEndpoint")
        for (const a of w.args) {
          const m = /port:\s*(\d+)/.exec(a);
          if (m) (byPort.get(m[1]) ?? byPort.set(m[1], []).get(m[1])!).push(n.resourceName);
        }
  for (const [port, users] of byPort) if (users.length > 1)
    issues.push({ severity: "warning", message: `Port ${port} is fixed on multiple resources (${users.join(", ")}) — they can't all bind it.` });

  // Dangling edges — a reference/waitFor pointing at a node that no longer exists.
  const ids = new Set(nodes.map(n => n.id));
  for (const e of stack.edges)
    if (!ids.has(e.fromNodeId) || !ids.has(e.toNodeId))
      issues.push({ severity: "warning", message: `Dangling ${e.kind} edge — one endpoint no longer exists.` });

  return issues;
}
// Parse a .env file into [key, value] pairs. Skips blanks/comments, strips an `export ` prefix and
// surrounding quotes. Keeps insertion order; last value wins per key.
export function parseDotenv(text: string): [string, string][] {
  const out = new Map<string, string>();
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) continue;
    const m = /^(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$/.exec(line);
    if (!m) continue;
    let v = m[2].trim();
    if ((v.startsWith('"') && v.endsWith('"')) || (v.startsWith("'") && v.endsWith("'"))) v = v.slice(1, -1);
    out.set(m[1], v);
  }
  return [...out.entries()];
}
export function isErrorLine(line: string): boolean {
  return /error|exception|fail/i.test(line);
}
// Mirrors the backend AppHost-selection heuristic (BundleImporter.Import):
// the file whose content contains the CreateBuilder call is the entrypoint.
export function pickAppHost(files: { path: string; content: string }[]): string | undefined {
  return files.find(f => f.content.includes("DistributedApplication.CreateBuilder"))?.path;
}
// Removing a node must also purge everything that referenced it, or the generated code keeps dangling
// identifiers (e.g. deleting `localai` from the AI demo left `var localAiOpenAiBase = ...localai...`
// and the n8n WithEnvironment(..., localAiOpenAiBase) calls → uncompilable). Cascade: drop raw
// statements referencing a removed var; if such a raw declared `var X`, X is now removed too, so
// re-scan (fixpoint); finally drop remaining nodes' WithCall args that reference any removed var.
export function removeNode(s: Stack, id: string): Stack {
  const node = s.nodes.find(n => n.id === id);
  const removed = new Set<string>();
  if (node) removed.add(node.varName);
  const esc = (v: string) => v.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const refs = (text: string) => [...removed].some(v => new RegExp(`\\b${esc(v)}\\b`).test(text));

  let raws = [...s.rawStatements];
  for (let changed = true; changed;) {
    changed = false;
    const kept: string[] = [];
    for (const r of raws) {
      if (refs(r)) {
        const m = r.match(/\bvar\s+([A-Za-z_]\w*)\s*=/);
        if (m && !removed.has(m[1])) { removed.add(m[1]); changed = true; }
      } else kept.push(r);
    }
    raws = kept;
  }

  const nodes = s.nodes.filter(n => n.id !== id).map(n => ({
    ...n,
    withCalls: n.withCalls.filter(w => !w.args.some(a => refs(a))),
  }));
  return {
    ...s,
    nodes,
    edges: s.edges.filter(e => e.fromNodeId !== id && e.toNodeId !== id),
    rawStatements: raws,
  };
}

// Deps of `id` (nodes it references/waits-for) that would be ORPHANED by deleting `id` — i.e. no
// other remaining node references/waits-for them. `owned` = this node spawned it as a companion.
// Powers the smart-delete prompt ("also remove these?"). Shared deps (used elsewhere) aren't returned.
// Resource-node ids whose center lies inside a boundary group's rectangle (approx node box 170×74).
// Used to move contained nodes with the group and to prompt on group deletion.
export function nodesInGroup(s: Stack, g: StackGroup): string[] {
  return s.nodes.filter(n => {
    const cx = n.x + 85, cy = n.y + 37;
    return cx >= g.x && cx <= g.x + g.width && cy >= g.y && cy <= g.y + g.height;
  }).map(n => n.id);
}

export interface OrphanDep { node: Node; owned: boolean }
export function orphanableDeps(s: Stack, id: string): OrphanDep[] {
  const targets = [...new Set(s.edges.filter(e => e.fromNodeId === id && e.toNodeId !== id).map(e => e.toNodeId))];
  const out: OrphanDep[] = [];
  for (const t of targets) {
    const node = s.nodes.find(n => n.id === t);
    if (!node) continue;
    const otherReferrer = s.edges.some(e => e.toNodeId === t && e.fromNodeId !== id && e.fromNodeId !== t);
    if (!otherReferrer) out.push({ node, owned: node.spawnedBy === id });
  }
  return out;
}
// Stack-level run state shown per node (Running/Starting/Failed dot on the
// canvas). NOT true per-resource Aspire health/URL — that needs the Aspire
// resource gRPC service and is a documented follow-up (see polish spec §4).
export function runStateColor(state: RunState): string | undefined {
  return { NotRunning: undefined, Starting: "yellow", Running: "green", Failed: "red" }[state];
}
