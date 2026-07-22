import type { Stack, Node, Edge, AppSettings, AuthStatus, UserDto, EnvHealth, PublishResult, DeployResult } from "./model";
const base = "";

export interface TemplateInfo { id: string; name: string; description: string }
export interface PackageInfo { id: string; version: string; resources: string[] }

// Called whenever an app call comes back 401 (expired/missing session) so the
// app can bounce to /login. Wired once by AuthGate; a no-op until then.
let onUnauthorized: () => void = () => {};
export const setOnUnauthorized = (fn: () => void) => { onUnauthorized = fn; };

async function ok(r: Response) {
  if (r.status === 401) onUnauthorized();
  if (!r.ok) throw new Error(`${r.status}: ${await r.text()}`);
  return r.json();
}

// /auth/* calls report their own 401s (e.g. bad login) as regular errors —
// they must NOT trigger the onUnauthorized redirect, or a failed login would
// immediately bounce back to /login masking the error message.
async function okAuth(r: Response) {
  if (!r.ok) throw new Error(`${r.status}: ${await r.text()}`);
  return r.json();
}

// Like ok(), but for endpoints that return 204 No Content on success (nothing to parse).
async function okVoid(r: Response) {
  if (r.status === 401) onUnauthorized();
  if (!r.ok) throw new Error(`${r.status}: ${await r.text()}`);
}

export const authStatus = (): Promise<AuthStatus> => fetch(`${base}/auth/status`).then(okAuth);
export const setup = (username: string, password: string): Promise<UserDto> =>
  fetch(`${base}/auth/setup`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ username, password }) }).then(okAuth);
export const login = (username: string, password: string): Promise<UserDto> =>
  fetch(`${base}/auth/login`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ username, password }) }).then(okAuth);
export const logout = (): Promise<void> => fetch(`${base}/auth/logout`, { method: "POST" }).then(() => undefined);
export const envHealth = (): Promise<EnvHealth> => fetch(`${base}/env/health`).then(okAuth);

export const listUsers = (): Promise<UserDto[]> => fetch(`${base}/users`).then(ok);
export const createUser = (username: string, password: string, isAdmin: boolean): Promise<UserDto> =>
  fetch(`${base}/users`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ username, password, isAdmin }) }).then(ok);
export const deleteUser = (id: string): Promise<void> => fetch(`${base}/users/${id}`, { method: "DELETE" }).then(okVoid);

export const getCatalog = () => fetch(`${base}/catalog`).then(ok);
export const getPresets = (): Promise<import("./model").ContainerPreset[]> => fetch(`${base}/catalog/presets`).then(ok);

export interface FsEntry { name: string; path: string; isDir: boolean }
export interface FsListing { path: string | null; parent: string | null; entries: FsEntry[] }
export const browseFs = (path?: string): Promise<FsListing> =>
  fetch(`${base}/fs${path ? `?path=${encodeURIComponent(path)}` : ""}`).then(ok);
export const listStacks = () => fetch(`${base}/stacks`).then(ok);
export const getStack = (id: string): Promise<Stack> => fetch(`${base}/stacks/${id}`).then(ok);
export const createStack = (s: Partial<Stack>): Promise<Stack> =>
  fetch(`${base}/stacks`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(s) }).then(ok);
export const saveStack = (s: Stack): Promise<Stack> =>
  fetch(`${base}/stacks/${s.id}`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(s) }).then(ok);
export const patchNode = (id: string, node: Node): Promise<Stack> =>
  fetch(`${base}/stacks/${id}/nodes/${node.id}`, { method: "PATCH", headers: { "content-type": "application/json" }, body: JSON.stringify(node) }).then(ok);
export const addEdge = (id: string, edge: Partial<Edge>): Promise<Stack> =>
  fetch(`${base}/stacks/${id}/edges`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(edge) }).then(ok);

export const previewStack = (id: string): Promise<string> => fetch(`${base}/stacks/${id}/preview`).then(r => r.text());
export const deleteStack = (id: string): Promise<void> => fetch(`${base}/stacks/${id}`, { method: "DELETE" }).then(() => undefined);
export const duplicateStack = (id: string): Promise<Stack> => fetch(`${base}/stacks/${id}/duplicate`, { method: "POST" }).then(ok);
export const openInIde = (id: string, ide: "vscode" | "rider" | "vs"): Promise<{ ok: boolean; error?: string }> =>
  fetch(`${base}/stacks/${id}/open`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ ide }) }).then(ok);
export const runStack = (id: string) => fetch(`${base}/stacks/${id}/run`, { method: "POST" }).then(ok);
export const stopStack = (id: string) => fetch(`${base}/stacks/${id}/stop`, { method: "POST" }).then(ok);
export const statusStack = (id: string) => fetch(`${base}/stacks/${id}/status`).then(ok);
export const stackResources = (id: string): Promise<import("./model").LiveResource[]> =>
  fetch(`${base}/stacks/${id}/resources`).then(ok);
export interface ContainerStat { name: string; cpu: number; memMb: number }
export const stackStats = (id: string): Promise<ContainerStat[]> => fetch(`${base}/stacks/${id}/stats`).then(ok);
export const runResourceCommand = (id: string, name: string, command: string, resourceType: string): Promise<{ ok: boolean; message?: string }> =>
  fetch(`${base}/stacks/${id}/resources/${encodeURIComponent(name)}/command`, {
    method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ command, resourceType }),
  }).then(ok);
export const getPackages = (id: string): Promise<PackageInfo[]> => fetch(`${base}/stacks/${id}/packages`).then(ok);
export interface CompletionItemDto { label: string; kind: string; insertText: string; detail?: string | null }
export interface CodeDiagnostic { message: string; severity: string; start: number; end: number }
export interface SignatureInfo { label: string; parameters: string[] }
const codePost = (id: string, path: string, body: unknown) =>
  fetch(`${base}/stacks/${id}/code/${path}`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) }).then(ok);
export const codeComplete = (id: string, code: string, offset: number): Promise<CompletionItemDto[]> => codePost(id, "complete", { code, offset });
export const codeHover = (id: string, code: string, offset: number): Promise<{ contents: string | null }> => codePost(id, "hover", { code, offset });
export const codeSignature = (id: string, code: string, offset: number): Promise<SignatureInfo | null> => codePost(id, "signature", { code, offset });
export const codeDiagnostics = (id: string, code: string): Promise<CodeDiagnostic[]> => codePost(id, "diagnostics", { code, offset: 0 });
export const codeSave = (id: string, name: string, code: string): Promise<Stack> => codePost(id, "save", { name, code });
export const validateStack = (id: string): Promise<CodeDiagnostic[]> => fetch(`${base}/stacks/${id}/validate`).then(ok);

export type PublishTarget = "compose" | "manifest" | "kubernetes" | "bicep";
export const publishStack = (id: string, target: PublishTarget = "compose"): Promise<PublishResult> =>
  fetch(`${base}/stacks/${id}/publish?target=${target}`, { method: "POST" }).then(ok);
export const deployStack = (id: string): Promise<DeployResult> => fetch(`${base}/stacks/${id}/deploy`, { method: "POST" }).then(ok);
export const deployDown = (id: string): Promise<DeployResult> => fetch(`${base}/stacks/${id}/deploy/down`, { method: "POST" }).then(ok);
export const deleteEdge = (id: string, edgeId: string): Promise<void> =>
  fetch(`${base}/stacks/${id}/edges/${edgeId}`, { method: "DELETE" }).then(() => undefined);

export const explainStack = (id: string): Promise<{ reply: string }> =>
  fetch(`${base}/stacks/${id}/explain`, { method: "POST" }).then(ok);
export const importCompose = (name: string, yaml: string): Promise<Stack> =>
  fetch(`${base}/stacks/import-compose`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ name, yaml }) }).then(ok);
export const getTemplates = (): Promise<TemplateInfo[]> => fetch(`${base}/templates`).then(ok);
export const saveTemplate = (stackId: string, name: string, description: string): Promise<TemplateInfo> =>
  fetch(`${base}/templates`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ stackId, name, description }) }).then(ok);
export const deleteUserTemplate = (id: string): Promise<void> =>
  fetch(`${base}/templates/user/${id}`, { method: "DELETE" }).then(() => undefined);
export const createFromTemplate = (id: string): Promise<Stack> =>
  fetch(`${base}/stacks/from-template/${id}`, { method: "POST" }).then(ok);

export interface BundleFile { path: string; content: string }
export const importBundle = (name: string, files: BundleFile[], programPath?: string): Promise<Stack> =>
  fetch(`${base}/stacks/import-bundle`, {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ name, files, programPath }),
  }).then(ok);

export const getSettings = (): Promise<AppSettings> => fetch(`${base}/settings`).then(ok);
export const saveSettings = (s: AppSettings): Promise<void> =>
  fetch(`${base}/settings`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(s) }).then(() => undefined);
export const testAi = (s: AppSettings): Promise<{ ok: boolean; model?: string; ms?: number; reply?: string; error?: string }> =>
  fetch(`${base}/settings/test-ai`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(s) }).then(ok);

export const assistStack = (id: string, prompt: string): Promise<{ reply: string; stack: Stack }> =>
  fetch(`${base}/stacks/${id}/assist`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ prompt }) }).then(ok);
