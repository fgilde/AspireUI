import type { Stack, Node, Edge, AppSettings, AuthStatus, UserDto, EnvHealth, PublishResult, DeployResult } from "./model";
const base = "/api";

export interface TemplateInfo { id: string; name: string; description: string }
export interface PackageInfo { id: string; version: string; resources: string[] }

let onUnauthorized: () => void = () => {};
export const setOnUnauthorized = (fn: () => void) => { onUnauthorized = fn; };

async function ok(r: Response) {
  if (r.status === 401) onUnauthorized();
  if (!r.ok) throw new Error(`${r.status}: ${await r.text()}`);
  return r.json();
}

async function okAuth(r: Response) {
  if (!r.ok) throw new Error(`${r.status}: ${await r.text()}`);
  return r.json();
}

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
export const adminSetPassword = (id: string, password: string, mustChange: boolean): Promise<void> =>
  fetch(`${base}/users/${id}/password`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify({ password, mustChange }) }).then(okVoid);
export const adminSetAdmin = (id: string, isAdmin: boolean): Promise<void> =>
  fetch(`${base}/users/${id}/admin`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify({ isAdmin }) }).then(okVoid);
export const adminSetViewModes = (id: string, modes: string[]): Promise<void> =>
  fetch(`${base}/users/${id}/view-modes`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify({ modes }) }).then(okVoid);
export const adminSetPermissions = (id: string, permissions: string[]): Promise<void> =>
  fetch(`${base}/users/${id}/permissions`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify({ permissions }) }).then(okVoid);
export const adminSetDisabled = (id: string, disabled: boolean): Promise<void> =>
  fetch(`${base}/users/${id}/disabled`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify({ disabled }) }).then(okVoid);
export const changePassword = (oldPassword: string, newPassword: string): Promise<void> =>
  fetch(`${base}/auth/change-password`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ oldPassword, newPassword }) }).then(okVoid);

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
// A guard the editor registers to intercept graph mutations (used for run-as-is imports: confirm before changing).
export class MutationCancelled extends Error { constructor() { super("cancelled"); this.name = "MutationCancelled"; } }
let _mutationGuard: (() => Promise<boolean>) | null = null;
export const setMutationGuard = (g: (() => Promise<boolean>) | null) => { _mutationGuard = g; };
const guardMutation = async () => { if (_mutationGuard && !(await _mutationGuard())) throw new MutationCancelled(); };

export const saveStack = async (s: Stack): Promise<Stack> => {
  await guardMutation();
  return fetch(`${base}/stacks/${s.id}`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(s) }).then(ok);
};
export const patchNode = async (id: string, node: Node): Promise<Stack> => {
  await guardMutation();
  return fetch(`${base}/stacks/${id}/nodes/${node.id}`, { method: "PATCH", headers: { "content-type": "application/json" }, body: JSON.stringify(node) }).then(ok);
};
export const addEdge = async (id: string, edge: Partial<Edge>): Promise<Stack> => {
  await guardMutation();
  return fetch(`${base}/stacks/${id}/edges`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(edge) }).then(ok);
};

export const previewStack = (id: string): Promise<string> => fetch(`${base}/stacks/${id}/preview`).then(r => r.text());
export const exportStackZip = (id: string): Promise<Blob> => fetch(`${base}/stacks/${id}/export`).then(r => r.blob());
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
export const hostingStats = (): Promise<ContainerStat[]> => fetch(`${base}/hosting/stats`).then(ok);
export const hostingSummary = (): Promise<{ diskFreeGb: number; diskTotalGb: number }> => fetch(`${base}/hosting/summary`).then(ok);
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
export const codeSave = async (id: string, name: string, code: string): Promise<Stack> => { await guardMutation(); return codePost(id, "save", { name, code }); };
export const validateStack = (id: string): Promise<CodeDiagnostic[]> => fetch(`${base}/stacks/${id}/validate`).then(ok);

export type PublishTarget = "compose" | "manifest" | "kubernetes" | "bicep";
export const publishStack = (id: string, target: PublishTarget = "compose"): Promise<PublishResult> =>
  fetch(`${base}/stacks/${id}/publish?target=${target}`, { method: "POST" }).then(ok);
export const deployStack = (id: string): Promise<DeployResult> => fetch(`${base}/stacks/${id}/deploy`, { method: "POST" }).then(ok);
export const deployDown = (id: string): Promise<DeployResult> => fetch(`${base}/stacks/${id}/deploy/down`, { method: "POST" }).then(ok);
export const deleteEdge = async (id: string, edgeId: string): Promise<void> => {
  await guardMutation();
  await fetch(`${base}/stacks/${id}/edges/${edgeId}`, { method: "DELETE" });
};

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

export interface SourceFile { path: string; content: string }  // content = base64
export const localImport = (b: { name?: string; mode?: string; sources: SourceFile[]; files?: string[]; services?: string[]; env?: Record<string, string> }): Promise<Stack> =>
  fetch(`${base}/import/local`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(b) }).then(ok);

export const getSettings = (): Promise<AppSettings> => fetch(`${base}/settings`).then(ok);
export const saveSettings = (s: AppSettings): Promise<void> =>
  fetch(`${base}/settings`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(s) }).then(() => undefined);
export const testAi = (s: AppSettings): Promise<{ ok: boolean; model?: string; ms?: number; reply?: string; error?: string }> =>
  fetch(`${base}/settings/test-ai`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(s) }).then(ok);
export const getAiCliTools = (): Promise<string[]> => fetch(`${base}/settings/ai-cli-tools`).then(ok);
export const detectAiModels = (s: AppSettings): Promise<{ models: string[]; error?: string | null }> =>
  fetch(`${base}/settings/ai-models`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(s) }).then(ok);

export const getDashboardSettings = (): Promise<{ hostDashboard: boolean; dashboardToken: string; publicHost?: string; publicHostSetting?: string; requestHost?: string }> =>
  fetch(`${base}/hosting/dashboard-settings`).then(ok);
export interface CloneHook { token: string; expireDays: number; bindDomain: boolean; domainFormat?: string | null; webhookPath: string }
export const listCloneHooks = (id: string): Promise<{ npmConfigured: boolean; hooks: CloneHook[] }> =>
  fetch(`${base}/stacks/${id}/clone-hooks`).then(ok);
export const createCloneHook = (id: string, b: { expireDays: number; bindDomain: boolean; domainFormat?: string }): Promise<{ token: string; webhookPath: string }> =>
  fetch(`${base}/stacks/${id}/clone-hooks`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(b) }).then(ok);
export const deleteCloneHook = (id: string, token: string): Promise<void> =>
  fetch(`${base}/stacks/${id}/clone-hooks/${token}`, { method: "DELETE" }).then(() => undefined);
export const getImportSettings = (): Promise<{ maxFileMb: number; respectGitignore: boolean }> =>
  fetch(`${base}/import/settings`).then(ok);
export const setImportSettings = (b: { maxFileMb?: number; respectGitignore?: boolean }): Promise<void> =>
  fetch(`${base}/import/settings`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(b) }).then(okVoid);
export const detectIps = (): Promise<string[]> => fetch(`${base}/hosting/detect-ip`).then(ok);
export const setDashboardSettings = (hostDashboard: boolean, dashboardToken: string, publicHost?: string): Promise<void> =>
  fetch(`${base}/hosting/dashboard-settings`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify({ hostDashboard, dashboardToken, publicHost }) }).then(okVoid);
export const localImportProgress = (b: { name?: string; mode?: string; sources: SourceFile[]; files?: string[]; services?: string[]; env?: Record<string, string> }, onProgress: (pct: number) => void): Promise<Stack> =>
  new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", `${base}/import/local`);
    xhr.setRequestHeader("content-type", "application/json");
    xhr.upload.onprogress = e => { if (e.lengthComputable) onProgress(Math.round((e.loaded / e.total) * 100)); };
    xhr.onload = () => xhr.status >= 200 && xhr.status < 300 ? resolve(JSON.parse(xhr.responseText)) : reject(new Error(xhr.responseText || `HTTP ${xhr.status}`));
    xhr.onerror = () => reject(new Error("upload failed"));
    xhr.send(JSON.stringify(b));
  });
export const getStoreExclusions = (): Promise<string[]> => fetch(`${base}/store/exclusions`).then(ok);
export const setStoreExclusions = (ids: string[]): Promise<void> =>
  fetch(`${base}/store/exclusions`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify({ ids }) }).then(okVoid);
export const getSnippets = (): Promise<import("./model").Snippet[]> => fetch(`${base}/snippets`).then(ok);
export const saveSnippet = (s: import("./model").Snippet): Promise<{ id: string }> =>
  fetch(`${base}/snippets`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(s) }).then(ok);
export const deleteSnippet = (id: string): Promise<void> =>
  fetch(`${base}/snippets/${id}`, { method: "DELETE" }).then(() => undefined);
export const autoAdd = (url: string): Promise<{ ok: boolean; reason?: string; code?: string; nodes?: Node[]; edges?: Edge[] }> =>
  fetch(`${base}/catalog/auto-preset`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ url }) }).then(ok);

export const assistStack = (id: string, prompt: string): Promise<{ reply: string; stack: Stack }> =>
  fetch(`${base}/stacks/${id}/assist`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ prompt }) }).then(ok);
export const assistStackCode = (id: string, prompt: string): Promise<{ reply: string; stack: Stack }> =>
  fetch(`${base}/stacks/${id}/assist-code`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ prompt }) }).then(ok);

export const listHosting = (): Promise<import("./model").Deployment[]> => fetch(`${base}/hosting`).then(ok);
export const hostingDeploy = (id: string): Promise<import("./model").Deployment> =>
  fetch(`${base}/stacks/${id}/hosting/deploy`, { method: "POST" }).then(ok);
export const stopHosting = (id: string): Promise<import("./model").Deployment> =>
  fetch(`${base}/stacks/${id}/hosting/stop`, { method: "POST" }).then(ok);
export const startHosting = (id: string): Promise<import("./model").Deployment> =>
  fetch(`${base}/stacks/${id}/hosting/start`, { method: "POST" }).then(ok);
export const restartHosting = (id: string): Promise<import("./model").Deployment> =>
  fetch(`${base}/stacks/${id}/hosting/restart`, { method: "POST" }).then(ok);
export const undeployHosting = (id: string, wipe = false): Promise<void> =>
  fetch(`${base}/stacks/${id}/hosting/undeploy${wipe ? "?wipe=true" : ""}`, { method: "POST" }).then(() => undefined);
export const updateHosting = (id: string): Promise<import("./model").Deployment> =>
  fetch(`${base}/stacks/${id}/hosting/update`, { method: "POST" }).then(ok);
export const checkUpdates = (id: string): Promise<{ images: { image: string; updateAvailable: boolean }[]; anyUpdate: boolean }> =>
  fetch(`${base}/stacks/${id}/hosting/check-updates`, { method: "POST" }).then(ok);
export const execInContainer = (depId: string, container: string, cmd: string): Promise<{ ok: boolean; output: string }> =>
  fetch(`${base}/hosting/${depId}/exec`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ container, cmd }) }).then(ok);
export const gitInspect = (b: { url: string; branch?: string; subdir?: string; authToken?: string }): Promise<{ hasCompose: boolean; hasAppHost: boolean; name?: string; composeFiles: { path: string; content: string }[]; manifest?: { file: string; app: string; image: string; port: number } | null }> =>
  fetch(`${base}/git/inspect`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(b) }).then(ok);
export const gitImport = (b: { url: string; branch?: string; subdir?: string; name?: string; mode?: string; authToken?: string; files?: string[]; env?: Record<string, string>; services?: string[] }): Promise<import("./model").Stack> =>
  fetch(`${base}/git/import`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(b) }).then(ok);
export const gitBranches = (url: string, authToken?: string): Promise<{ branches: string[] }> =>
  fetch(`${base}/git/branches`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ url, authToken }) }).then(ok);
export const gitInfo = (stackId: string): Promise<{ url: string; branch?: string | null; subdir?: string | null; webhookPath: string } | null> =>
  fetch(`${base}/stacks/${stackId}/git`).then(r => r.ok ? r.json() : null);
export const gitPull = (stackId: string): Promise<{ stackId: string; redeployed: boolean }> =>
  fetch(`${base}/stacks/${stackId}/git/pull`, { method: "POST" }).then(ok);
export const listVolumes = (depId: string): Promise<{ name: string; sizeMb: number }[]> =>
  fetch(`${base}/hosting/${depId}/volumes`).then(ok);
export const lsVolume = (depId: string, vol: string, path: string): Promise<{ name: string; dir: boolean; size: number }[]> =>
  fetch(`${base}/hosting/${depId}/volumes/${encodeURIComponent(vol)}/ls?path=${encodeURIComponent(path)}`).then(ok);
export const volumeFileUrl = (depId: string, vol: string, path: string): string =>
  `${base}/hosting/${depId}/volumes/${encodeURIComponent(vol)}/file?path=${encodeURIComponent(path)}`;
export const getBackupSettings = (): Promise<{ intervalHours: number; retain: number; lastRun?: string | null }> =>
  fetch(`${base}/hosting/backup-settings`).then(ok);
export const setBackupSettings = (intervalHours: number, retain: number): Promise<void> =>
  fetch(`${base}/hosting/backup-settings`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify({ intervalHours, retain }) }).then(okVoid);
export const backupHosting = (id: string): Promise<{ dir: string | null }> =>
  fetch(`${base}/stacks/${id}/hosting/backup`, { method: "POST" }).then(ok);
export const listBackups = (stackId: string): Promise<import("./model").BackupInfo[]> =>
  fetch(`${base}/stacks/${stackId}/hosting/backups`).then(ok);
export const restoreBackup = (stackId: string, stamp: string): Promise<import("./model").Deployment> =>
  fetch(`${base}/stacks/${stackId}/hosting/backups/${stamp}/restore`, { method: "POST" }).then(ok);
export const deleteBackup = (stackId: string, stamp: string): Promise<void> =>
  fetch(`${base}/stacks/${stackId}/hosting/backups/${stamp}`, { method: "DELETE" }).then(() => undefined);
export const backupDownloadUrl = (stackId: string, stamp: string) => `${base}/stacks/${stackId}/hosting/backups/${stamp}/download`;
export const hostingServices = (depId: string): Promise<import("./model").ServiceStatus[]> =>
  fetch(`${base}/hosting/${depId}/services`).then(ok);
export const hostingLogsUrl = (depId: string) => `${base}/hosting/${depId}/logs`;
export const hostingConfig = (stackId: string): Promise<import("./model").NodeConfig[]> =>
  fetch(`${base}/stacks/${stackId}/hosting/config`).then(ok);
export const reconfigureHosting = (stackId: string, env: Record<string, string[][]>, ports?: import("./model").PortMapping[]): Promise<import("./model").Deployment> =>
  fetch(`${base}/stacks/${stackId}/hosting/reconfigure`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ env, ports }) }).then(ok);

export const listApiTokens = (): Promise<import("./model").ApiToken[]> => fetch(`${base}/api-tokens`).then(ok);
export const createApiToken = (name: string): Promise<{ token: string; record: import("./model").ApiToken }> =>
  fetch(`${base}/api-tokens`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ name }) }).then(ok);
export const deleteApiToken = (id: string): Promise<void> =>
  fetch(`${base}/api-tokens/${id}`, { method: "DELETE" }).then(() => undefined);

export const dockerImages = (): Promise<import("./model").DockerImage[]> => fetch(`${base}/docker/images`).then(ok);
export const dockerVolumes = (): Promise<import("./model").DockerVolume[]> => fetch(`${base}/docker/volumes`).then(ok);
export const dockerContainers = (): Promise<import("./model").DockerContainer[]> => fetch(`${base}/docker/containers`).then(ok);
export const dockerRemove = (kind: "images" | "containers" | "volumes", id: string): Promise<void> =>
  fetch(`${base}/docker/${kind}/${encodeURIComponent(id)}`, { method: "DELETE" }).then(r => { if (!r.ok) return r.text().then(t => { throw new Error(t || r.statusText); }); });
export const dockerPrune = (kind: "images" | "containers"): Promise<{ log: string }> =>
  fetch(`${base}/docker/prune`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ kind }) }).then(ok);

type NpmSettingsBody = { enabled: boolean; baseUrl: string; email: string; password?: string | null; forwardHost: string };
export const getNpmSettings = (): Promise<import("./model").NpmSettings> => fetch(`${base}/hosting/npm-settings`).then(ok);
export const setNpmSettings = (b: NpmSettingsBody): Promise<void> =>
  fetch(`${base}/hosting/npm-settings`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(b) }).then(() => undefined);
export const testNpm = (b: NpmSettingsBody): Promise<{ ok: boolean; error?: string | null }> =>
  fetch(`${base}/hosting/npm/test`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(b) }).then(ok);
export interface NotifySettings { webhookUrl: string; telegramToken: string; telegramChat: string }
export const getNotifySettings = (): Promise<NotifySettings> => fetch(`${base}/hosting/notify-settings`).then(ok);
export const setNotifySettings = (b: NotifySettings): Promise<void> =>
  fetch(`${base}/hosting/notify-settings`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(b) }).then(okVoid);
export const testNotify = (b: NotifySettings): Promise<{ ok: boolean; error?: string }> =>
  fetch(`${base}/hosting/notify/test`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(b) }).then(ok);
export const npmConfigured = (): Promise<{ configured: boolean }> => fetch(`${base}/hosting/npm-configured`).then(ok);
export const getDomain = (stackId: string): Promise<import("./model").DomainInfo> =>
  fetch(`${base}/stacks/${stackId}/hosting/domain`).then(ok);
export const setDomain = (stackId: string, b: { id?: number | null; domainNames: string[]; scheme: string; forwardHost: string; forwardPort: number; websockets: boolean; ssl?: boolean; certificateId?: number }): Promise<import("./model").NpmProxyHost> =>
  fetch(`${base}/stacks/${stackId}/hosting/domain`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(b) }).then(ok);
export const deleteDomain = (stackId: string, proxyId: number): Promise<void> =>
  fetch(`${base}/stacks/${stackId}/hosting/domain/${proxyId}`, { method: "DELETE" }).then(r => { if (!r.ok) return r.text().then(t => { throw new Error(t || r.statusText); }); });
export const setDomainEnabled = (stackId: string, proxyId: number, enabled: boolean): Promise<void> =>
  fetch(`${base}/stacks/${stackId}/hosting/domain/${proxyId}/enabled`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ enabled }) }).then(r => { if (!r.ok) return r.text().then(t => { throw new Error(t || r.statusText); }); });
