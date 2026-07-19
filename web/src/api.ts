import type { Stack, Node, Edge, AppSettings, AuthStatus, UserDto, EnvHealth } from "./model";
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
export const runStack = (id: string) => fetch(`${base}/stacks/${id}/run`, { method: "POST" }).then(ok);
export const stopStack = (id: string) => fetch(`${base}/stacks/${id}/stop`, { method: "POST" }).then(ok);
export const statusStack = (id: string) => fetch(`${base}/stacks/${id}/status`).then(ok);
export const getPackages = (id: string): Promise<PackageInfo[]> => fetch(`${base}/stacks/${id}/packages`).then(ok);
export const deleteEdge = (id: string, edgeId: string): Promise<void> =>
  fetch(`${base}/stacks/${id}/edges/${edgeId}`, { method: "DELETE" }).then(() => undefined);

export const getTemplates = (): Promise<TemplateInfo[]> => fetch(`${base}/templates`).then(ok);
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

export const assistStack = (id: string, prompt: string): Promise<{ reply: string; stack: Stack }> =>
  fetch(`${base}/stacks/${id}/assist`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ prompt }) }).then(ok);
