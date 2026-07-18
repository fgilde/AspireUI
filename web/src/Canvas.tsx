import { ReactFlow, Background, Controls } from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { useCallback } from "react";
import type { Stack } from "./model";
import * as api from "./api";

export function Canvas({ stack, setStack, onSelect }:
  { stack: Stack; setStack: (s: Stack) => void; onSelect: (nodeId: string) => void }) {

  const flowNodes = stack.nodes.map(n => ({
    id: n.id, position: { x: n.x, y: n.y },
    data: { label: `${n.resourceName}\n${n.addMethod}` },
  }));
  const flowEdges = stack.edges.map(e => ({ id: e.id, source: e.fromNodeId, target: e.toNodeId }));

  const onNodesChange = useCallback((changes: any[]) => {
    // Persist position on drag-stop.
    changes.filter(c => c.type === "position" && c.dragging === false).forEach(c => {
      const node = stack.nodes.find(n => n.id === c.id);
      if (node && c.position) api.patchNode(stack.id, { ...node, x: c.position.x, y: c.position.y }).then(setStack);
    });
  }, [stack, setStack]);

  const onConnect = useCallback((c: any) => {
    api.addEdge(stack.id, { fromNodeId: c.source, toNodeId: c.target, kind: "reference" }).then(setStack);
  }, [stack, setStack]);

  return (
    <div style={{ flex: 1, height: "100vh" }}>
      <ReactFlow nodes={flowNodes} edges={flowEdges}
        onNodesChange={onNodesChange} onConnect={onConnect}
        onNodeClick={(_, n) => onSelect(n.id)} fitView>
        <Background /><Controls />
      </ReactFlow>
    </div>
  );
}
