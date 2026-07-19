import { ReactFlow, Background, Controls, Handle, Position, BaseEdge, EdgeLabelRenderer, getBezierPath } from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { useCallback } from "react";
import { Card, Text, Badge, Group, Tooltip, useMantineColorScheme } from "@mantine/core";
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

// Editable edge: shows what the connection MEANS (references / waits for), lets you
// flip the kind by clicking the chip, and delete it with the ×. Fixes the "wrong
// waitFor I couldn't remove/change" trap — plain reactflow edges have no visible affordance.
function EditableEdge({ id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, data }: any) {
  const [path, labelX, labelY] = getBezierPath({ sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition });
  const isWait = data.kind === "waitFor";
  return (
    <>
      <BaseEdge id={id} path={path} style={isWait ? { strokeDasharray: "6 3" } : undefined} />
      <EdgeLabelRenderer>
        <div style={{
          position: "absolute", transform: `translate(-50%,-50%) translate(${labelX}px,${labelY}px)`,
          pointerEvents: "all", display: "flex", alignItems: "center", gap: 4,
          fontSize: 10, background: "var(--mantine-color-body)", border: "1px solid var(--mantine-color-default-border)",
          borderRadius: 6, padding: "1px 4px",
        }} className="nodrag nopan">
          <Tooltip label="Click to switch reference ⇄ waits-for" withArrow openDelay={300}>
            <span style={{ cursor: "pointer", color: isWait ? "var(--mantine-color-orange-text)" : "var(--mantine-color-indigo-text)" }}
              onClick={() => data.onToggle(id)}>
              {isWait ? "waits for" : "references"}
            </span>
          </Tooltip>
          <Tooltip label="Remove this connection" withArrow openDelay={300}>
            <span style={{ cursor: "pointer", color: "var(--mantine-color-red-text)", fontWeight: 700 }}
              onClick={() => data.onDelete(id)}>×</span>
          </Tooltip>
        </div>
      </EdgeLabelRenderer>
    </>
  );
}
const edgeTypes = { editable: EditableEdge };

export function Canvas({ stack, setStack, onSelect, runState }:
  { stack: Stack; setStack: (s: Stack) => void; onSelect: (id: string | null) => void; runState: RunState }) {
  const { colorScheme } = useMantineColorScheme();

  const deleteEdge = useCallback((edgeId: string) => {
    api.deleteEdge(stack.id, edgeId).then(() =>
      setStack({ ...stack, edges: stack.edges.filter(e => e.id !== edgeId) }));
  }, [stack, setStack]);

  // Flip an edge's kind (reference ⇄ waitFor). Edges are part of the stack model, so a whole-stack
  // save persists it — no dedicated endpoint needed.
  const toggleEdge = useCallback((edgeId: string) => {
    const edges = stack.edges.map(e =>
      e.id === edgeId ? { ...e, kind: e.kind === "waitFor" ? "reference" : "waitFor" } : e);
    api.saveStack({ ...stack, edges }).then(setStack);
  }, [stack, setStack]);

  const nodes = stack.nodes.map(n => ({
    id: n.id, type: "resource", position: { x: n.x, y: n.y }, deletable: true,
    data: { resourceName: n.resourceName, addMethod: n.addMethod, runState },
  }));
  const edges = stack.edges.map(e => ({
    id: e.id, source: e.fromNodeId, target: e.toNodeId, type: "editable",
    data: { kind: e.kind, onToggle: toggleEdge, onDelete: deleteEdge },
  }));

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
    <ReactFlow nodes={nodes} edges={edges} nodeTypes={nodeTypes} edgeTypes={edgeTypes}
      colorMode={colorScheme === "light" ? "light" : "dark"}
      onNodesChange={onNodesChange} onConnect={onConnect} onEdgesChange={onEdgesChange}
      deleteKeyCode={["Backspace", "Delete"]}
      onNodeClick={(_, n) => onSelect(n.id)} fitView>
      <Background /><Controls />
    </ReactFlow>
  );
}
