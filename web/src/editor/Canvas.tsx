import { ReactFlow, Background, Controls, Handle, Position, BaseEdge, EdgeLabelRenderer, getBezierPath, useNodesState } from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { useCallback, useEffect, useMemo } from "react";
import { Card, Text, Badge, Group, Tooltip, useMantineColorScheme, ThemeIcon, Menu } from "@mantine/core";
import { IconCheck, IconArrowsLeftRight, IconTrash } from "@tabler/icons-react";
import type { Stack, RunState } from "../model";
import { removeNode, runStateColor } from "../model";
import { resourceVisual } from "../resourceIcons";
import * as api from "../api";

// Small dot showing the current stack-level run state for this node. This is
// NOT per-resource Aspire health (needs the Aspire resource gRPC service —
// see docs/superpowers/specs/2026-07-19-aspireui-polish.md §4 non-goals);
// every node shows the same shared runStatus for now.
function ResourceNode({ data }: any) {
  const color = runStateColor(data.runState as RunState);
  const { Icon, color: iconColor } = resourceVisual(data.addMethod);
  return (
    <Card withBorder shadow="sm" padding="xs" radius="md" style={{ minWidth: 150 }}>
      <Handle type="target" position={Position.Left} />
      <Group justify="space-between" wrap="nowrap" gap={6}>
        <Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
          <ThemeIcon variant="light" size={22} radius="sm" style={{ color: iconColor, background: `${iconColor}22`, flexShrink: 0 }}>
            <Icon size={15} />
          </ThemeIcon>
          <Text fw={600} size="sm" truncate>{data.resourceName}</Text>
        </Group>
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

// Editable edge for a directed pair (from → to). A connection can be a reference and/or a wait-for
// independently (both are valid in Aspire); direction = who references / waits on whom. Clicking the
// chip opens a menu to toggle each kind, reverse the direction, or remove the whole connection.
function EditableEdge({ id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, data }: any) {
  const [path, labelX, labelY] = getBezierPath({ sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition });
  const { hasRef, hasWait, from, to, ops } = data;
  const label = hasRef && hasWait ? "ref + waits" : hasWait ? "waits for" : "references";
  const dashed = hasWait && !hasRef;
  return (
    <>
      <BaseEdge id={id} path={path} style={dashed ? { strokeDasharray: "6 3" } : undefined} />
      <EdgeLabelRenderer>
        <div style={{
          position: "absolute", transform: `translate(-50%,-50%) translate(${labelX}px,${labelY}px)`,
          pointerEvents: "all", fontSize: 10,
        }} className="nodrag nopan">
          <Menu shadow="md" width={190} position="top" withArrow>
            <Menu.Target>
              <span style={{
                cursor: "pointer", padding: "1px 6px", borderRadius: 6,
                background: "var(--mantine-color-body)", border: "1px solid var(--mantine-color-default-border)",
                color: hasWait ? "var(--mantine-color-orange-text)" : "var(--mantine-color-indigo-text)",
              }}>{label} ▾</span>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Label>Connection: {from === to ? "self" : "→"}</Menu.Label>
              <Menu.Item leftSection={hasRef ? <IconCheck size={14} /> : <span style={{ width: 14 }} />}
                onClick={() => ops.setPair(from, to, !hasRef, hasWait)}>References</Menu.Item>
              <Menu.Item leftSection={hasWait ? <IconCheck size={14} /> : <span style={{ width: 14 }} />}
                onClick={() => ops.setPair(from, to, hasRef, !hasWait)}>Waits for</Menu.Item>
              <Menu.Divider />
              <Menu.Item leftSection={<IconArrowsLeftRight size={14} />} onClick={() => ops.reverse(from, to)}>Reverse direction</Menu.Item>
              <Menu.Item color="red" leftSection={<IconTrash size={14} />} onClick={() => ops.remove(from, to)}>Remove connection</Menu.Item>
            </Menu.Dropdown>
          </Menu>
        </div>
      </EdgeLabelRenderer>
    </>
  );
}
const edgeTypes = { editable: EditableEdge };

export function Canvas({ stack, setStack, onSelect, runState }:
  { stack: Stack; setStack: (s: Stack) => void; onSelect: (id: string | null) => void; runState: RunState }) {
  const { colorScheme } = useMantineColorScheme();

  // All edge mutations rewrite the pair's edges and persist the whole stack (edges live in the model,
  // so one saveStack is enough — no per-edge endpoints needed).
  const ops = useMemo(() => {
    const eid = () => "e" + crypto.randomUUID().slice(0, 8);
    const save = (edges: typeof stack.edges) => api.saveStack({ ...stack, edges }).then(setStack);
    return {
      setPair(from: string, to: string, ref: boolean, wait: boolean) {
        const rest = stack.edges.filter(e => !(e.fromNodeId === from && e.toNodeId === to));
        if (ref) rest.push({ id: eid(), fromNodeId: from, toNodeId: to, kind: "reference" });
        if (wait) rest.push({ id: eid(), fromNodeId: from, toNodeId: to, kind: "waitFor" });
        return save(rest);
      },
      reverse(from: string, to: string) {
        return save(stack.edges.map(e =>
          e.fromNodeId === from && e.toNodeId === to ? { ...e, fromNodeId: to, toNodeId: from } : e));
      },
      remove(from: string, to: string) {
        return save(stack.edges.filter(e => !(e.fromNodeId === from && e.toNodeId === to)));
      },
    };
  }, [stack, setStack]);

  // Local ReactFlow node state so dragging renders live (a fully-controlled `nodes` prop only moved the
  // node on mouse-up). Re-synced from the stack whenever the node set / positions / run-state change;
  // position changes are persisted to the backend on drag-stop, removals cascade through removeNode.
  const [rfNodes, setRfNodes, onNodesChangeInternal] = useNodesState<any>([]);
  const nodeSig = JSON.stringify(stack.nodes.map(n => [n.id, n.resourceName, n.addMethod, n.x, n.y])) + runState;
  useEffect(() => {
    setRfNodes(stack.nodes.map(n => ({
      id: n.id, type: "resource", position: { x: n.x, y: n.y }, deletable: true,
      data: { resourceName: n.resourceName, addMethod: n.addMethod, runState },
    })));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [nodeSig]);

  // One visual edge per directed pair, combining the reference/waitFor edges that connect them.
  const edges = useMemo(() => {
    const pairs = new Map<string, { from: string; to: string; hasRef: boolean; hasWait: boolean }>();
    for (const e of stack.edges) {
      const key = `${e.fromNodeId}->${e.toNodeId}`;
      const g = pairs.get(key) ?? { from: e.fromNodeId, to: e.toNodeId, hasRef: false, hasWait: false };
      if (e.kind === "waitFor") g.hasWait = true; else g.hasRef = true;
      pairs.set(key, g);
    }
    return [...pairs.values()].map(g => ({
      id: `${g.from}->${g.to}`, source: g.from, target: g.to, type: "editable",
      data: { hasRef: g.hasRef, hasWait: g.hasWait, from: g.from, to: g.to, ops },
    }));
  }, [stack.edges, ops]);

  const onNodesChange = useCallback((changes: any[]) => {
    onNodesChangeInternal(changes); // apply live (drag, select) to the local RF state
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
  }, [stack, setStack, onSelect, onNodesChangeInternal]);
  const onConnect = useCallback((c: any) =>
    api.addEdge(stack.id, { fromNodeId: c.source, toNodeId: c.target, kind: "reference" }).then(setStack),
    [stack, setStack]);

  const onEdgesChange = useCallback((changes: any[]) => {
    // Visual edge ids are "from->to" (a directed pair); Delete-key removal drops every underlying edge.
    const removedPairs = changes.filter(c => c.type === "remove").map(c => String(c.id).split("->"));
    if (removedPairs.length === 0) return;
    const keep = stack.edges.filter(e => !removedPairs.some(([f, t]) => e.fromNodeId === f && e.toNodeId === t));
    api.saveStack({ ...stack, edges: keep }).then(setStack);
  }, [stack, setStack]);

  return (
    <ReactFlow nodes={rfNodes} edges={edges} nodeTypes={nodeTypes} edgeTypes={edgeTypes}
      colorMode={colorScheme === "light" ? "light" : "dark"}
      onNodesChange={onNodesChange} onConnect={onConnect} onEdgesChange={onEdgesChange}
      deleteKeyCode={["Backspace", "Delete"]}
      onNodeClick={(_, n) => onSelect(n.id)} fitView>
      <Background /><Controls />
    </ReactFlow>
  );
}
