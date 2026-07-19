import type { Stack, Node, Edge } from "./model";
const base = "";

export interface TemplateInfo { id: string; name: string; description: string }
export interface PackageInfo { id: string; version: string; resources: string[] }

async function ok(r: Response) {
  if (!r.ok) throw new Error(`${r.status}: ${await r.text()}`);
  return r.json();
}

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
