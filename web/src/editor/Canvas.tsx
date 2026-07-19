import { ReactFlow, Background, Controls, Handle, Position } from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { useCallback } from "react";
import { Card, Text, Badge, Group, Tooltip } from "@mantine/core";
import type { Stack, RunState } from "../model";
import { removeNode, runStateColor } from "../model";
import * as api from "../api";

// Small dot showing the current stack-level run state for this node. This is
// NOT per-resource Aspire health (needs the Aspire resource gRPC service —
// see docs/superpowers/specs/2026-07-19-aspireui-polish.md §4 non-goals);
// every node shows the same shared runStatus for now.
function ResourceNode({ data }: any) {
  const color = runStateColor(data.runState as RunState);
  return (
    <Card withBorder shadow="sm" padding="xs" radius="md" style={{ minWidth: 140 }}>
      <Handle type="target" position={Position.Left} />
      <Group justify="space-between" wrap="nowrap" gap={4}>
        <Text fw={600} size="sm">{data.resourceName}</Text>
        {color && (
          <Tooltip label={data.runState} withArrow>
            <span style={{ width: 8, height: 8, borderRadius: "50%", background: color, flexShrink: 0 }} />
          </Tooltip>
        )}
      </Group>
      <Badge size="xs" variant="light" mt={4}>{data.addMethod}</Badge>
      <Handle type="source" position={Position.Right} />
    </Card>
  );
}
const nodeTypes = { resource: ResourceNode };

export function Canvas({ stack, setStack, onSelect, runState }:
  { stack: Stack; setStack: (s: Stack) => void; onSelect: (id: string | null) => void; runState: RunState }) {
  const nodes = stack.nodes.map(n => ({
    id: n.id, type: "resource", position: { x: n.x, y: n.y }, deletable: true,
    data: { resourceName: n.resourceName, addMethod: n.addMethod, runState },
  }));
  const edges = stack.edges.map(e => e.kind === "waitFor"
    ? { id: e.id, source: e.fromNodeId, target: e.toNodeId, label: "waits for", style: { strokeDasharray: "6 3" } }
    : { id: e.id, source: e.fromNodeId, target: e.toNodeId });

  const onNodesChange = useCallback((changes: any[]) => {
    changes.filter(c => c.type === "position" && c.dragging === false).forEach(c => {
      const node = stack.nodes.find(n => n.id === c.id);
      if (node && c.position) api.patchNode(stack.id, { ...node, x: c.position.x, y: c.position.y }).then(setStack);
    });
    const removed = changes.filter(c => c.type === "remove");
    if (removed.length > 0) {
      const next = removed.reduce((s, c) => removeNode(s, c.id), stack);
      api.saveStack(next).then(setStack);
      onSelect(null);
    }
  }, [stack, setStack, onSelect]);
  const onConnect = useCallback((c: any) =>
    api.addEdge(stack.id, { fromNodeId: c.source, toNodeId: c.target, kind: "reference" }).then(setStack),
    [stack, setStack]);

  const onEdgesChange = useCallback((changes: any[]) => {
    const removed = changes.filter(c => c.type === "remove");
    if (removed.length === 0) return;
    Promise.all(removed.map(c => api.deleteEdge(stack.id, c.id))).then(() => {
      setStack({ ...stack, edges: stack.edges.filter(e => !removed.some(c => c.id === e.id)) });
    });
  }, [stack, setStack]);

  return (
    <ReactFlow nodes={nodes} edges={edges} nodeTypes={nodeTypes}
      onNodesChange={onNodesChange} onConnect={onConnect} onEdgesChange={onEdgesChange}
      deleteKeyCode={["Backspace", "Delete"]}
      onNodeClick={(_, n) => onSelect(n.id)} fitView>
      <Background /><Controls />
    </ReactFlow>
  );
}
