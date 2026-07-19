export interface WithCall { method: string; args: string[] }
export interface Node { id: string; varName: string; addMethod: string; resourceName: string; withCalls: WithCall[]; x: number; y: number; addArgs: string[] }
export interface Edge { id: string; fromNodeId: string; toNodeId: string; kind: string }
export interface Stack { id: string; name: string; targetFramework: string; nodes: Node[]; edges: Edge[] }

export interface CatalogParam { name: string; type: "string" | "int" | "bool" | "enum"; required: boolean; default?: string | null; options?: string[] | null; label: string }
export interface CatalogWith { method: string; label: string; params: CatalogParam[] }
export interface ResourceType { addMethod: string; label: string; icon?: string | null; group?: string | null; addParams: CatalogParam[]; withs: CatalogWith[] }
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
