import { ReactFlow, Background, Controls, MiniMap, Panel, Handle, Position, BaseEdge, EdgeLabelRenderer, getBezierPath, useNodesState } from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { useCallback, useEffect, useMemo, useState } from "react";
import { Card, Text, Badge, Group, Tooltip, useMantineColorScheme, ThemeIcon, Menu, Paper, UnstyledButton, TextInput, Anchor } from "@mantine/core";
import { IconCheck, IconArrowsLeftRight, IconTrash, IconCopy, IconPencil, IconSearch, IconLayoutGrid, IconExternalLink } from "@tabler/icons-react";
import dagre from "dagre";
import type { Stack, RunState, LiveResource } from "../model";
import { removeNode, runStateColor, sanitizeIdentifier, buildLiveOverlay, liveStateColor } from "../model";
import { resourceVisual, ResourceGlyph } from "../resourceIcons";
import { confirmDelete, toastOk, toastErr } from "../ui";
import * as api from "../api";

// Small dot showing the current stack-level run state for this node. This is
// NOT per-resource Aspire health (needs the Aspire resource gRPC service —
// see docs/superpowers/specs/2026-07-19-aspireui-polish.md §4 non-goals);
// every node shows the same shared runStatus for now.
// A theme color name (green/red/yellow/gray) -> a concrete CSS color for the status dot.
function dotColor(c: string | undefined): string | undefined {
  return c ? `var(--mantine-color-${c}-filled)` : undefined;
}
// First user-facing URL of a live resource (skip internal/inactive ones).
function primaryUrl(live: LiveResource | undefined): string | undefined {
  return live?.urls.find(u => !u.isInternal && !u.isInactive)?.url;
}

function ResourceNode({ data }: any) {
  const live = data.live as LiveResource | undefined;
  // When the stack runs, prefer the real per-resource state from Aspire; otherwise the shared run state.
  const color = live ? liveStateColor(live.state) : (runStateColor(data.runState as RunState) ?? undefined);
  const stateLabel = live ? (live.state ?? "…") : data.runState;
  const { color: iconColor } = resourceVisual(data.addMethod);
  const url = primaryUrl(live);
  return (
    <Card withBorder shadow="sm" padding="xs" radius="md" style={{ minWidth: 150 }}>
      <Handle type="target" position={Position.Left} />
      <Group justify="space-between" wrap="nowrap" gap={6}>
        <Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
          <ThemeIcon variant="light" size={22} radius="sm" style={{ background: `${iconColor}22`, flexShrink: 0 }}>
            <ResourceGlyph addMethod={data.addMethod} size={15} />
          </ThemeIcon>
          <Text fw={600} size="sm" truncate>{data.resourceName}</Text>
        </Group>
        {color && (
          <Tooltip label={stateLabel} withArrow>
            <span style={{ width: 8, height: 8, borderRadius: "50%", background: dotColor(color), flexShrink: 0 }} />
          </Tooltip>
        )}
      </Group>
      <Group justify="space-between" wrap="nowrap" gap={4} mt={4}>
        <Badge size="xs" variant="light">{data.addMethod}</Badge>
        {url && (
          <Anchor href={url} target="_blank" rel="noreferrer" onClick={e => e.stopPropagation()} title={url}>
            <IconExternalLink size={13} />
          </Anchor>
        )}
      </Group>
      <Handle type="source" position={Position.Right} />
    </Card>
  );
}

// Ephemeral, translucent node for an actual Aspire resource spawned by a builder (e.g. supabase-db
// under supabase). Not part of the saved stack — only shown while running.
function LiveNode({ data }: any) {
  const live = data.live as LiveResource;
  const url = primaryUrl(live);
  return (
    <Card withBorder padding={6} radius="md"
      style={{ minWidth: 130, opacity: 0.82, borderStyle: "dashed", background: "var(--mantine-color-body)" }}>
      <Handle type="target" position={Position.Left} style={{ opacity: 0 }} />
      <Group justify="space-between" wrap="nowrap" gap={6}>
        <Group gap={5} wrap="nowrap" style={{ minWidth: 0 }}>
          <Tooltip label={live.state ?? "…"} withArrow>
            <span style={{ width: 7, height: 7, borderRadius: "50%", background: dotColor(liveStateColor(live.state)), flexShrink: 0 }} />
          </Tooltip>
          <Text size="xs" truncate title={live.name}>{live.displayName}</Text>
        </Group>
        {url && (
          <Anchor href={url} target="_blank" rel="noreferrer" title={url}>
            <IconExternalLink size={12} />
          </Anchor>
        )}
      </Group>
      <Handle type="source" position={Position.Right} style={{ opacity: 0 }} />
    </Card>
  );
}
const nodeTypes = { resource: ResourceNode, live: LiveNode };

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
  const [menu, setMenu] = useState<{ x: number; y: number; nodeId: string } | null>(null);
  const [query, setQuery] = useState("");
  const [live, setLive] = useState<LiveResource[]>([]);

  // While the stack runs, poll the Aspire resource service for live per-resource state/urls/children.
  useEffect(() => {
    if (runState !== "Running" && runState !== "Starting") { setLive([]); return; }
    let alive = true;
    const tick = () => api.stackResources(stack.id).then(r => { if (alive) setLive(r); }).catch(() => {});
    tick();
    const iv = setInterval(tick, 2500);
    return () => { alive = false; clearInterval(iv); };
  }, [runState, stack.id]);

  const overlay = useMemo(() => buildLiveOverlay(stack.nodes, live), [stack.nodes, live]);
  // Position the ephemeral child/orphan resources: children hang in a column to the right of their
  // owning builder node; orphans (from macro extensions with no node) cluster far right.
  const liveFlow = useMemo(() => {
    const nodesById = new Map(stack.nodes.map(n => [n.id, n]));
    const maxX = Math.max(0, ...stack.nodes.map(n => n.x));
    const perOwner: Record<string, number> = {};
    const rfLive: any[] = [];
    const rfLiveEdges: any[] = [];
    for (const c of overlay.children) {
      const key = c.ownerNodeId ?? "__orphan";
      const idx = (perOwner[key] = (perOwner[key] ?? 0) + 1) - 1;
      const owner = c.ownerNodeId ? nodesById.get(c.ownerNodeId) : undefined;
      const x = owner ? owner.x + 250 : maxX + 340;
      const y = (owner ? owner.y : 40) + idx * 58;
      const id = "live:" + c.live.name;
      rfLive.push({ id, type: "live", position: { x, y }, draggable: false, selectable: false, deletable: false, data: { live: c.live } });
      if (c.parentElemId)
        rfLiveEdges.push({
          id: "le:" + id, source: c.parentElemId, target: id, selectable: false,
          animated: (c.live.state ?? "").toLowerCase().includes("start"),
          style: { strokeDasharray: "4 3", opacity: 0.55 },
        });
    }
    return { rfLive, rfLiveEdges };
  }, [overlay, stack.nodes]);

  const duplicateNode = useCallback((nodeId: string) => {
    const n = stack.nodes.find(x => x.id === nodeId);
    if (!n) return;
    const taken = new Set(stack.nodes.map(x => x.resourceName));
    let name = `${n.resourceName}-copy`, i = 2;
    while (taken.has(name)) name = `${n.resourceName}-copy${i++}`;
    const copy = { ...n, id: "n" + crypto.randomUUID().slice(0, 8), varName: sanitizeIdentifier(name),
      resourceName: name, x: n.x + 40, y: n.y + 40 };
    api.saveStack({ ...stack, nodes: [...stack.nodes, copy] }).then(setStack);
  }, [stack, setStack]);

  // Auto-arrange the graph left-to-right with dagre, then persist the new positions.
  const autoLayout = useCallback(() => {
    if (stack.nodes.length === 0) return;
    const g = new dagre.graphlib.Graph();
    g.setGraph({ rankdir: "LR", nodesep: 50, ranksep: 90 });
    g.setDefaultEdgeLabel(() => ({}));
    stack.nodes.forEach(n => g.setNode(n.id, { width: 170, height: 74 }));
    stack.edges.forEach(e => { if (e.fromNodeId !== e.toNodeId) g.setEdge(e.fromNodeId, e.toNodeId); });
    dagre.layout(g);
    const nodes = stack.nodes.map(n => { const p = g.node(n.id); return { ...n, x: Math.round(p.x - 85), y: Math.round(p.y - 37) }; });
    api.saveStack({ ...stack, nodes }).then(s => { setStack(s); toastOk("Layout arranged"); }).catch(toastErr);
  }, [stack, setStack]);

  const deleteNodeById = useCallback((nodeId: string) => {
    const n = stack.nodes.find(x => x.id === nodeId);
    if (!n) return;
    confirmDelete(`"${n.resourceName}"`, "This also removes its connections and any code that references it.").then(ok => {
      if (!ok) return;
      api.saveStack(removeNode(stack, nodeId)).then(s => { setStack(s); onSelect(null); toastOk("Resource deleted"); }).catch(toastErr);
    });
  }, [stack, setStack, onSelect]);

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

  // Inject live per-resource status onto each builder node, dim search misses, and append the
  // ephemeral live child/orphan nodes + their edges.
  const q = query.trim().toLowerCase();
  const displayNodes = useMemo(() => {
    const base = rfNodes.map(n => {
      const data = { ...n.data, live: overlay.statusByNodeId[n.id] };
      const opacity = q && !`${n.data.resourceName} ${n.data.addMethod}`.toLowerCase().includes(q) ? 0.25 : 1;
      return { ...n, data, style: { ...n.style, opacity } };
    });
    return [...base, ...liveFlow.rfLive];
  }, [rfNodes, overlay, liveFlow, q]);
  const allEdges = useMemo(() => [...edges, ...liveFlow.rfLiveEdges], [edges, liveFlow]);

  return (
    <ReactFlow nodes={displayNodes} edges={allEdges} nodeTypes={nodeTypes} edgeTypes={edgeTypes}
      colorMode={colorScheme === "light" ? "light" : "dark"}
      snapToGrid snapGrid={[16, 16]}
      onNodesChange={onNodesChange} onConnect={onConnect} onEdgesChange={onEdgesChange}
      deleteKeyCode={["Backspace", "Delete"]}
      onNodeClick={(_, n) => { if (!n.id.startsWith("live:")) onSelect(n.id); }}
      onNodeContextMenu={(e, n) => { if (n.id.startsWith("live:")) return; e.preventDefault(); setMenu({ x: e.clientX, y: e.clientY, nodeId: n.id }); }}
      onPaneClick={() => setMenu(null)} onMoveStart={() => setMenu(null)} fitView>
      <Background /><Controls />
      <MiniMap pannable zoomable nodeColor={n => (n.data as any).addMethod ? resourceVisual((n.data as any).addMethod).color : "#888"} />
      <Panel position="top-left">
        <Group gap={6}>
          <TextInput size="xs" w={180} placeholder="Find resource…" value={query}
            onChange={e => setQuery(e.currentTarget.value)}
            leftSection={<IconSearch size={13} />} />
          <Tooltip label="Auto-arrange layout" withArrow>
            <UnstyledButton onClick={autoLayout}
              style={{ display: "flex", alignItems: "center", padding: 6, borderRadius: 6,
                background: "var(--mantine-color-body)", border: "1px solid var(--mantine-color-default-border)" }}>
              <IconLayoutGrid size={15} />
            </UnstyledButton>
          </Tooltip>
        </Group>
      </Panel>
      {menu && (
        <Paper shadow="md" withBorder p={4} radius="sm"
          style={{ position: "fixed", left: menu.x, top: menu.y, zIndex: 1000, minWidth: 160 }}
          onMouseLeave={() => setMenu(null)}>
          {[
            { icon: IconPencil, label: "Edit properties", run: () => onSelect(menu.nodeId), color: undefined },
            { icon: IconCopy, label: "Duplicate", run: () => duplicateNode(menu.nodeId), color: undefined },
            { icon: IconTrash, label: "Delete", run: () => deleteNodeById(menu.nodeId), color: "var(--mantine-color-red-text)" },
          ].map(item => (
            <UnstyledButton key={item.label} onClick={() => { item.run(); setMenu(null); }}
              style={{ display: "flex", alignItems: "center", gap: 8, width: "100%", padding: "6px 8px", borderRadius: 4, fontSize: 13, color: item.color }}
              className="ctx-item">
              <item.icon size={15} /> {item.label}
            </UnstyledButton>
          ))}
        </Paper>
      )}
    </ReactFlow>
  );
}
