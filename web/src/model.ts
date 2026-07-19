export interface WithCall { method: string; args: string[] }
export interface Node { id: string; varName: string; addMethod: string; resourceName: string; withCalls: WithCall[]; x: number; y: number; addArgs: string[] }
export interface Edge { id: string; fromNodeId: string; toNodeId: string; kind: string }
export interface Stack { id: string; name: string; targetFramework: string; nodes: Node[]; edges: Edge[] }

export interface CatalogParam { name: string; type: "string" | "int" | "bool" | "enum"; required: boolean; default?: string | null; options?: string[] | null; enumTypeName?: string | null; label: string }
export interface CatalogOverload { params: CatalogParam[] }
export interface CatalogMethod { method: string; label: string; overloads: CatalogOverload[] }
export interface ResourceType { addMethod: string; label: string; icon?: string | null; group?: string | null; addOverloads: CatalogOverload[]; withs: CatalogMethod[] }
export type RunState = "NotRunning" | "Starting" | "Running" | "Failed";
export interface RunStatus { state: RunState; dashboardUrl?: string | null; log: string[] }

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
  if (type === "bool") return value === "true" ? "true" : "false";
  if (type === "enum") return enumTypeName ? `${enumTypeName}.${value}` : value;
  return `"${value.replace(/\\/g, "\\\\").replace(/"/g, '\\"')}"`;
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
export function sanitizeIdentifier(name: string): string {
  const cleaned = name.replace(/[^A-Za-z0-9_]/g, "");
  const s = /^[0-9]/.test(cleaned) ? "_" + cleaned : cleaned;
  return s || "resource";
}
export function removeNode(s: Stack, id: string): Stack {
  return {
    ...s,
    nodes: s.nodes.filter(n => n.id !== id),
    edges: s.edges.filter(e => e.fromNodeId !== id && e.toNodeId !== id),
  };
}
