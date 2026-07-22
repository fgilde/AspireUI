export const APP_VERSION = "0.1.0";

export interface WithCall { method: string; args: string[] }
export interface Node { id: string; varName: string; addMethod: string; resourceName: string; withCalls: WithCall[]; x: number; y: number; addArgs: string[]; composite?: boolean; usings?: string[] }
export interface Edge { id: string; fromNodeId: string; toNodeId: string; kind: string }
export interface ExtraFile { name: string; content: string }
export interface PackageRef { id: string; version: string }
export interface Stack {
  id: string; name: string; targetFramework: string; nodes: Node[]; edges: Edge[]; rawStatements: string[];
  extraFiles: ExtraFile[]; extraPackages: PackageRef[];
}

export interface CatalogParam { name: string; type: "string" | "int" | "number" | "bool" | "enum" | "configure" | "resourceRef"; required: boolean; default?: string | null; options?: string[] | null; enumTypeName?: string | null; label: string; fields?: CatalogParam[] | null }
export interface CatalogOverload { params: CatalogParam[] }
export interface CatalogMethod { method: string; label: string; overloads: CatalogOverload[] }
export interface ResourceType { addMethod: string; label: string; icon?: string | null; group?: string | null; description?: string | null; addOverloads: CatalogOverload[]; withs: CatalogMethod[]; composite?: boolean; usings?: string[] | null; package?: string | null; packageVersion?: string | null; resourceTypeName?: string | null }
// A curated one-click app preset → a preconfigured AddContainer node (image + HTTP endpoint + env).
export interface ContainerPreset { id: string; label: string; group: string; image: string; port: number; icon?: string | null; description?: string | null; env?: string[][] | null }
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
// Stack-level run state shown per node (Running/Starting/Failed dot on the
// canvas). NOT true per-resource Aspire health/URL — that needs the Aspire
// resource gRPC service and is a documented follow-up (see polish spec §4).
export function runStateColor(state: RunState): string | undefined {
  return { NotRunning: undefined, Starting: "yellow", Running: "green", Failed: "red" }[state];
}
