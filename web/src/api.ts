import type { Stack, Node, Edge } from "./model";
const base = "";
export const getCatalog = () => fetch(`${base}/catalog`).then(r => r.json());
export const listStacks = () => fetch(`${base}/stacks`).then(r => r.json());
export const getStack = (id: string): Promise<Stack> => fetch(`${base}/stacks/${id}`).then(r => r.json());
export const createStack = (s: Partial<Stack>): Promise<Stack> =>
  fetch(`${base}/stacks`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(s) }).then(r => r.json());
export const saveStack = (s: Stack): Promise<Stack> =>
  fetch(`${base}/stacks/${s.id}`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(s) }).then(r => r.json());
export const patchNode = (id: string, node: Node): Promise<Stack> =>
  fetch(`${base}/stacks/${id}/nodes/${node.id}`, { method: "PATCH", headers: { "content-type": "application/json" }, body: JSON.stringify(node) }).then(r => r.json());
export const addEdge = (id: string, edge: Partial<Edge>): Promise<Stack> =>
  fetch(`${base}/stacks/${id}/edges`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(edge) }).then(r => r.json());
