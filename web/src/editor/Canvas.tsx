import { ReactFlow, Background, Controls, MiniMap, Panel, Handle, Position, BaseEdge, EdgeLabelRenderer, getBezierPath, useNodesState, NodeResizer } from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Card, Text, Badge, Group, Tooltip, useMantineColorScheme, ThemeIcon, Menu, Paper, UnstyledButton, TextInput, Anchor, ActionIcon, Modal, Stack as MStack, Button } from "@mantine/core";
import { IconCheck, IconArrowsLeftRight, IconTrash, IconCopy, IconPencil, IconSearch, IconLayoutGrid, IconExternalLink, IconTerminal2, IconMap, IconMapOff, IconNote, IconBoxMargin, IconX, IconBookmark } from "@tabler/icons-react";
import dagre from "dagre";
import type { Stack, RunState, LiveResource } from "../model";
import { removeNode, runStateColor, sanitizeIdentifier, buildLiveOverlay, liveStateColor, nodesInGroup, collectSubgraph, rid, type Node, type StackGroup } from "../model";
import { useResourceDelete } from "./useResourceDelete";
import { resourceVisual, ResourceGlyph } from "../resourceIcons";
import { toastOk, toastErr, promptText } from "../ui";
import { ResourceLogDrawer } from "./ResourceLogDrawer";
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

function ResourceNode({ data, selected }: any) {
  const live = data.live as LiveResource | undefined;
  // When the stack runs, prefer the real per-resource state from Aspire; otherwise the shared run state.
  const color = live ? liveStateColor(live.state) : (runStateColor(data.runState as RunState) ?? undefined);
  const stateLabel = live ? (live.state ?? "…") : data.runState;
  const { color: iconColor } = resourceVisual(data.icon || data.addMethod);
  const url = primaryUrl(live);
  return (
    <Card withBorder shadow="sm" padding="xs" radius="md"
      style={{ minWidth: 150,
        borderColor: selected ? "var(--mantine-color-orange-filled)" : undefined,
        borderWidth: selected ? 2 : undefined,
        boxShadow: selected ? "0 0 0 2px var(--mantine-color-orange-filled)" : undefined }}>
      <Handle type="target" position={Position.Left} />
      <Group justify="space-between" wrap="nowrap" gap={6}>
        <Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
          <ThemeIcon variant="light" size={22} radius="sm" style={{ background: `${iconColor}22`, flexShrink: 0 }}>
            <ResourceGlyph addMethod={data.addMethod} iconKey={data.icon} size={15} />
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
        <Group gap={6} wrap="nowrap">
          {live && (
            <Tooltip label="Stream logs" withArrow>
              <span style={{ cursor: "pointer", display: "flex", color: "var(--mantine-color-dimmed)" }}
                onClick={e => { e.stopPropagation(); data.onLogs?.(live.name, data.resourceName); }}>
                <IconTerminal2 size={13} />
              </span>
            </Tooltip>
          )}
          {url && (
            <Anchor href={url} target="_blank" rel="noreferrer" onClick={e => e.stopPropagation()} title={url}>
              <IconExternalLink size={13} />
            </Anchor>
          )}
        </Group>
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
        <Group gap={5} wrap="nowrap">
          <Tooltip label="Stream logs" withArrow>
            <span style={{ cursor: "pointer", display: "flex", color: "var(--mantine-color-dimmed)" }}
              onClick={e => { e.stopPropagation(); data.onLogs?.(live.name, live.displayName); }}>
              <IconTerminal2 size={12} />
            </span>
          </Tooltip>
          {url && (
            <Anchor href={url} target="_blank" rel="noreferrer" title={url}>
              <IconExternalLink size={12} />
            </Anchor>
          )}
        </Group>
      </Group>
      <Handle type="source" position={Position.Right} style={{ opacity: 0 }} />
    </Card>
  );
}
// Sticky note — canvas-only annotation. Double-click to edit; blur/save persists via data.onText.
function NoteNode({ id, data }: any) {
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState(data.text as string);
  useEffect(() => setText(data.text), [data.text]);
  return (
    <div style={{ minWidth: 120, maxWidth: 240, background: "var(--mantine-color-yellow-light)",
      border: "1px solid var(--mantine-color-yellow-filled)", borderRadius: 6, padding: 6, fontSize: 12 }}
      onDoubleClick={() => setEditing(true)}>
      <ActionIcon size="xs" variant="subtle" color="red" style={{ position: "absolute", top: 2, right: 2 }}
        className="nodrag" onClick={() => data.onDelete(id)}><IconX size={11} /></ActionIcon>
      {editing ? (
        <textarea autoFocus value={text} className="nodrag nopan"
          onChange={e => setText(e.target.value)}
          onBlur={() => { setEditing(false); data.onText(id, text); }}
          style={{ width: "100%", minHeight: 48, resize: "vertical", border: "none", background: "transparent",
            font: "inherit", color: "inherit", outline: "none" }} />
      ) : (
        <div style={{ whiteSpace: "pre-wrap", cursor: "text", minHeight: 16 }}>{text || "Double-click to edit"}</div>
      )}
    </div>
  );
}

// Boundary group — a labeled, resizable rectangle drawn behind resources to organize them.
function GroupNode({ id, data, selected }: any) {
  const c = (data.color as string) || "#7c8291";
  return (
    <div style={{ width: "100%", height: "100%", borderRadius: 10, border: `1.5px dashed ${c}`,
      background: `${c}12`, boxSizing: "border-box" }}>
      <NodeResizer isVisible={selected} minWidth={140} minHeight={90} color={c} />
      <div className="nodrag" style={{ position: "absolute", top: -10, left: 10, display: "flex", alignItems: "center", gap: 4,
        background: "var(--mantine-color-body)", padding: "0 6px", borderRadius: 4, fontSize: 11, fontWeight: 600, color: c }}>
        <input value={data.label} onChange={e => data.onLabel(id, e.target.value)}
          style={{ border: "none", background: "transparent", color: "inherit", font: "inherit", width: `${Math.max(6, (data.label?.length || 6))}ch`, outline: "none" }} />
        <IconX size={11} style={{ cursor: "pointer" }} onClick={() => data.onDelete(id)} />
      </div>
    </div>
  );
}
const nodeTypes = { resource: ResourceNode, live: LiveNode, note: NoteNode, group: GroupNode };


// Editable edge for a directed pair (from → to). A connection can be a reference and/or a wait-for
// independently (both are valid in Aspire); direction = who references / waits on whom. Clicking the
// chip opens a menu to toggle each kind, reverse the direction, or remove the whole connection.
function EditableEdge({ id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, data }: any) {
  const [path, labelX, labelY] = getBezierPath({ sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition });
  const { hasRef, hasWait, hasEnv, from, to, ops } = data;
  // env-only pair = an app→parameter reference (WithEnvironment); render it distinctly, no ref/wait toggles.
  const envOnly = hasEnv && !hasRef && !hasWait;
  const label = envOnly ? "param" : hasRef && hasWait ? "ref + waits" : hasWait ? "waits for" : "references";
  const dashed = envOnly || (hasWait && !hasRef);
  return (
    <>
      <BaseEdge id={id} path={path} style={{ ...(dashed ? { strokeDasharray: "6 3" } : {}), ...(envOnly ? { stroke: "var(--mantine-color-grape-5)" } : {}) }} />
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
                color: envOnly ? "var(--mantine-color-grape-text)" : hasWait ? "var(--mantine-color-orange-text)" : "var(--mantine-color-indigo-text)",
              }}>{label} ▾</span>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Label>Connection: {from === to ? "self" : "→"}</Menu.Label>
              {envOnly
                ? <Menu.Label>Env reference to a parameter (WithEnvironment)</Menu.Label>
                : <>
                    <Menu.Item leftSection={hasRef ? <IconCheck size={14} /> : <span style={{ width: 14 }} />}
                      onClick={() => ops.setPair(from, to, !hasRef, hasWait)}>References</Menu.Item>
                    <Menu.Item leftSection={hasWait ? <IconCheck size={14} /> : <span style={{ width: 14 }} />}
                      onClick={() => ops.setPair(from, to, hasRef, !hasWait)}>Waits for</Menu.Item>
                    <Menu.Divider />
                    <Menu.Item leftSection={<IconArrowsLeftRight size={14} />} onClick={() => ops.reverse(from, to)}>Reverse direction</Menu.Item>
                  </>}
              <Menu.Item color="red" leftSection={<IconTrash size={14} />} onClick={() => ops.remove(from, to)}>Remove connection</Menu.Item>
            </Menu.Dropdown>
          </Menu>
        </div>
      </EdgeLabelRenderer>
    </>
  );
}
const edgeTypes = { editable: EditableEdge };

export function Canvas({ stack, setStack, onSelect, onSelectIds, onShowProperties, runState }:
  { stack: Stack; setStack: (s: Stack) => void; onSelect: (id: string | null) => void;
    onSelectIds?: (ids: string[]) => void; onShowProperties?: () => void; runState: RunState }) {
  const { colorScheme } = useMantineColorScheme();
  const [menu, setMenu] = useState<{ x: number; y: number; nodeId: string } | null>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  // Close the node context menu on an outside click or Escape (not on hover — that felt broken).
  useEffect(() => {
    if (!menu) return;
    const onDown = (e: MouseEvent) => { if (!menuRef.current?.contains(e.target as any)) setMenu(null); };
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setMenu(null); };
    window.addEventListener("mousedown", onDown);
    window.addEventListener("keydown", onKey);
    return () => { window.removeEventListener("mousedown", onDown); window.removeEventListener("keydown", onKey); };
  }, [menu]);
  const [query, setQuery] = useState("");
  const [live, setLive] = useState<LiveResource[]>([]);
  const [logTarget, setLogTarget] = useState<{ name: string; display: string } | null>(null);
  const rf = useRef<any>(null);                          // ReactFlow instance (for centering)
  const prevIds = useRef<Set<string> | null>(null);      // node ids last render, to detect additions
  const [glow, setGlow] = useState<Set<string>>(new Set());
  // When nodes are added, center the view on the first new one and briefly glow the additions.
  useEffect(() => {
    const ids = new Set(stack.nodes.map(n => n.id));
    if (prevIds.current === null) { prevIds.current = ids; return; } // skip initial load
    const added = stack.nodes.filter(n => !prevIds.current!.has(n.id));
    prevIds.current = ids;
    if (added.length === 0) return;
    const first = added[0];
    rf.current?.setCenter?.(first.x + 85, first.y + 37, { zoom: rf.current.getZoom?.() ?? 1, duration: 400 });
    setGlow(new Set(added.map(n => n.id)));
    const t = setTimeout(() => setGlow(new Set()), 1400);
    return () => clearTimeout(t);
  }, [stack.nodes]);
  const [groupDel, setGroupDel] = useState<{ id: string; count: number } | null>(null);
  // Tracks an in-progress group drag so contained nodes move with it (id -> {startX,startY,members,lastX,lastY}).
  const groupDrag = useRef<Record<string, { sx: number; sy: number; lx: number; ly: number; members: string[] }>>({});
  const [showMinimap, setShowMinimap] = useState(() => localStorage.getItem("aspireui.minimap") !== "off");
  const toggleMinimap = () => setShowMinimap(v => { localStorage.setItem("aspireui.minimap", v ? "off" : "on"); return !v; });
  const onLogs = useCallback((name: string, display: string) => setLogTarget({ name, display }), []);

  // Canvas annotations (notes + boundary groups) — persisted on the stack, never in the code.
  const annoOps = useMemo(() => {
    const save = (patch: Partial<Stack>) => api.saveStack({ ...stack, ...patch }).then(setStack);
    const gid = (p: string) => p + rid();
    return {
      addNote: () => save({ notes: [...(stack.notes ?? []), { id: gid("note:"), text: "", x: 80, y: 80 }] }),
      addGroup: () => save({ groups: [...(stack.groups ?? []), { id: gid("group:"), label: "Group", x: 40, y: 40, width: 320, height: 220, color: "#7c8291" }] }),
      setNoteText: (id: string, text: string) => save({ notes: (stack.notes ?? []).map(n => n.id === id ? { ...n, text } : n) }),
      setGroupLabel: (id: string, label: string) => save({ groups: (stack.groups ?? []).map(g => g.id === id ? { ...g, label } : g) }),
      remove: (id: string) => save({ notes: (stack.notes ?? []).filter(n => n.id !== id), groups: (stack.groups ?? []).filter(g => g.id !== id) }),
    };
  }, [stack, setStack]);

  // Catalog + presets drive "auto-group": map each canvas node to its palette group (a preset by its
  // icon, otherwise the catalog group of its AddMethod).
  const [autoCat, setAutoCat] = useState<{ addMethod: string; group?: string | null }[]>([]);
  const [autoPresets, setAutoPresets] = useState<{ id: string; icon?: string | null; group?: string | null }[]>([]);
  useEffect(() => { api.getCatalog().then(setAutoCat).catch(() => {}); api.getPresets().then(setAutoPresets).catch(() => {}); }, []);

  // Auto-group: create/reuse a boundary group per palette group present on the canvas, drop each node
  // into its group's grid, size each group to fit, and arrange the groups row-by-row.
  const autoGroupAll = useCallback(() => {
    const catGroup = new Map(autoCat.map(r => [r.addMethod, r.group || "Other"]));
    const presetGroup = new Map(autoPresets.map(p => [p.icon || p.id, p.group || "Apps"]));
    const groupOf = (n: Node): string =>
      (n.addMethod === "AddContainer" && n.icon && presetGroup.has(n.icon))
        ? presetGroup.get(n.icon)! : (catGroup.get(n.addMethod) || "Other");

    const byLabel = new Map<string, Node[]>();
    for (const n of stack.nodes) { const l = groupOf(n); (byLabel.get(l) ?? byLabel.set(l, []).get(l)!).push(n); }
    if (byLabel.size === 0) return;

    const PALETTE = ["#4c6ef5", "#12b886", "#e8590c", "#ae3ec9", "#1098ad", "#f08c00", "#7c8291"];
    const cellW = 200, cellH = 108, padX = 18, padTop = 42, padBottom = 16, gap = 48, maxRow = 1600;
    let cx = 40, rowY = 40, rowMaxH = 0, ci = 0;
    const existing = stack.groups ?? [];
    const managed: StackGroup[] = [];
    const moved = new Map<string, { x: number; y: number }>();

    for (const label of [...byLabel.keys()].sort()) {
      const members = byLabel.get(label)!;
      const cols = Math.min(3, Math.ceil(Math.sqrt(members.length)));
      const rows = Math.ceil(members.length / cols);
      const w = padX * 2 + cols * cellW, h = padTop + padBottom + rows * cellH;
      if (cx + w > maxRow && cx > 40) { cx = 40; rowY += rowMaxH + gap; rowMaxH = 0; }
      members.forEach((n, i) => moved.set(n.id, {
        x: cx + padX + (i % cols) * cellW, y: rowY + padTop + Math.floor(i / cols) * cellH,
      }));
      const prev = existing.find(g => g.label.toLowerCase() === label.toLowerCase());
      managed.push({ id: prev?.id ?? "group:" + rid(), label,
        x: cx, y: rowY, width: w, height: h, color: prev?.color ?? PALETTE[ci++ % PALETTE.length] });
      cx += w + gap; rowMaxH = Math.max(rowMaxH, h);
    }
    const managedLabels = new Set(managed.map(g => g.label.toLowerCase()));
    const groups = [...existing.filter(g => !managedLabels.has(g.label.toLowerCase())), ...managed];
    const nodes = stack.nodes.map(n => moved.has(n.id) ? { ...n, ...moved.get(n.id)! } : n);
    api.saveStack({ ...stack, groups, nodes }).then(s => { setStack(s); toastOk(`Grouped into ${managed.length}`); }).catch(toastErr);
  }, [stack, setStack, autoCat, autoPresets]);

  // Deleting a group with resources inside asks whether to delete them too or just ungroup.
  const onGroupDelete = useCallback((id: string) => {
    const g = (stack.groups ?? []).find(x => x.id === id);
    if (!g) return;
    const members = nodesInGroup(stack, g);
    if (members.length > 0) setGroupDel({ id, count: members.length });
    else annoOps.remove(id);
  }, [stack, annoOps]);

  // Save a node (or a group's members) + everything it's connected to as a reusable palette snippet.
  const saveAsSnippet = useCallback((rootIds: string[], defaultName: string, icon?: string | null) => {
    promptText("Save as snippet", "Snippet name", defaultName).then(name => {
      if (!name) return;
      const { nodes, edges } = collectSubgraph(stack, rootIds);
      api.saveSnippet({ id: "", name, group: "Custom", icon: icon ?? null, nodes, edges, files: stack.extraFiles ?? [] })
        .then(() => { toastOk(`Saved snippet "${name}"`); window.dispatchEvent(new Event("aspireui:snippets-changed")); })
        .catch(toastErr);
    });
  }, [stack]);

  // Duplicate a group + its members, offset — a quick way to clone a whole cluster.
  const duplicateGroup = useCallback((id: string) => {
    const g = (stack.groups ?? []).find(x => x.id === id);
    if (!g) return;
    const dx = 40, dy = 40;
    const memberIds = new Set(nodesInGroup(stack, g));
    const members = stack.nodes.filter(n => memberIds.has(n.id));
    const taken = new Set(stack.nodes.map(n => n.resourceName));
    const uniq = (base: string) => { let n = `${base}-copy`, i = 2; while (taken.has(n)) n = `${base}-copy${i++}`; taken.add(n); return n; };
    const copies = members.map(n => {
      const name = uniq(n.resourceName);
      return { ...n, id: "n" + rid(), varName: sanitizeIdentifier(name),
        resourceName: name, x: n.x + dx, y: n.y + dy };
    });
    const newGroup = { ...g, id: "group:" + rid(), label: `${g.label} copy`, x: g.x + dx, y: g.y + dy };
    api.saveStack({ ...stack, nodes: [...stack.nodes, ...copies], groups: [...(stack.groups ?? []), newGroup] })
      .then(s => { setStack(s); toastOk("Group duplicated"); }).catch(toastErr);
  }, [stack, setStack]);

  // rf nodes for annotations: groups first (behind, low z), then notes.
  const annoFlow = useMemo(() => {
    const groups = (stack.groups ?? []).map(g => ({
      id: g.id, type: "group", position: { x: g.x, y: g.y }, zIndex: 0, style: { width: g.width, height: g.height },
      data: { label: g.label, color: g.color, onLabel: annoOps.setGroupLabel, onDelete: onGroupDelete },
    }));
    const notes = (stack.notes ?? []).map(n => ({
      id: n.id, type: "note", position: { x: n.x, y: n.y }, zIndex: 5,
      data: { text: n.text, onText: annoOps.setNoteText, onDelete: annoOps.remove },
    }));
    return [...groups, ...notes];
  }, [stack.notes, stack.groups, annoOps, onGroupDelete]);

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
      rfLive.push({ id, type: "live", position: { x, y }, draggable: false, selectable: false, deletable: false, data: { live: c.live, onLogs } });
      if (c.parentElemId)
        rfLiveEdges.push({
          id: "le:" + id, source: c.parentElemId, target: id, selectable: false,
          animated: (c.live.state ?? "").toLowerCase().includes("start"),
          style: { strokeDasharray: "4 3", opacity: 0.55 },
        });
    }
    return { rfLive, rfLiveEdges };
  }, [overlay, stack.nodes, onLogs]);

  const duplicateNode = useCallback((nodeId: string) => {
    const n = stack.nodes.find(x => x.id === nodeId);
    if (!n) return;
    const taken = new Set(stack.nodes.map(x => x.resourceName));
    let name = `${n.resourceName}-copy`, i = 2;
    while (taken.has(name)) name = `${n.resourceName}-copy${i++}`;
    const copy = { ...n, id: "n" + rid(), varName: sanitizeIdentifier(name),
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

  // THE shared delete path (context menu, Delete key, property grid all use it).
  const { deleteOne, deleteMany, dialog: deleteDialog } = useResourceDelete(stack, setStack, () => onSelect(null));

  // All edge mutations rewrite the pair's edges and persist the whole stack (edges live in the model,
  // so one saveStack is enough — no per-edge endpoints needed).
  const ops = useMemo(() => {
    const eid = () => "e" + rid();
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
  // Include annotations in the sig so they re-sync; put them in the SAME rf-node state as resources
  // so dragging/resizing renders live (a derived-then-appended list only moved them on mouse-up).
  const nodeSig = JSON.stringify(stack.nodes.map(n => [n.id, n.resourceName, n.addMethod, n.x, n.y]))
    + JSON.stringify((stack.groups ?? []).map(g => [g.id, g.label, g.x, g.y, g.width, g.height, g.color]))
    + JSON.stringify((stack.notes ?? []).map(n => [n.id, n.text, n.x, n.y])) + runState;
  useEffect(() => {
    setRfNodes([...annoFlow.filter(n => n.type === "group"),
      ...stack.nodes.map(n => ({
        id: n.id, type: "resource", position: { x: n.x, y: n.y }, deletable: true,
        data: { resourceName: n.resourceName, addMethod: n.addMethod, icon: n.icon, runState },
      })),
      ...annoFlow.filter(n => n.type === "note")]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [nodeSig]);

  // One visual edge per directed pair, combining the reference/waitFor edges that connect them.
  const edges = useMemo(() => {
    const pairs = new Map<string, { from: string; to: string; hasRef: boolean; hasWait: boolean; hasEnv: boolean }>();
    const bump = (from: string, to: string) => {
      const key = `${from}->${to}`;
      const g = pairs.get(key) ?? { from, to, hasRef: false, hasWait: false, hasEnv: false };
      pairs.set(key, g); return g;
    };
    for (const e of stack.edges) {
      const g = bump(e.fromNodeId, e.toNodeId);
      if (e.kind === "waitFor") g.hasWait = true; else if (e.kind === "env") g.hasEnv = true; else g.hasRef = true;
    }
    // Derive a "param" line from each WithEnvironment(key, <paramVar>) reference — its 2nd arg is a bare
    // identifier (not a quoted literal) naming another node's varName. Covers pre-existing nodes too.
    const byVar = new Map(stack.nodes.filter(n => n.varName).map(n => [n.varName, n.id]));
    for (const n of stack.nodes)
      for (const w of n.withCalls)
        if (w.method === "WithEnvironment" && w.args[1] && !w.args[1].startsWith('"')) {
          const toId = byVar.get(w.args[1]);
          if (toId && toId !== n.id) bump(n.id, toId).hasEnv = true;
        }
    return [...pairs.values()].map(g => ({
      id: `${g.from}->${g.to}`, source: g.from, target: g.to, type: "editable",
      data: { hasRef: g.hasRef, hasWait: g.hasWait, hasEnv: g.hasEnv, from: g.from, to: g.to, ops },
    }));
  }, [stack.edges, ops]);

  const onNodesChange = useCallback((changes: any[]) => {
    if (changes.some(c => c.type === "position" || c.type === "remove")) setMenu(null); // close ctx menu on canvas activity
    const isAnno = (id: string) => id.startsWith("note:") || id.startsWith("group:");
    // A single resource-node delete that would orphan deps → route to the smart-delete dialog instead
    // of removing it here. Suppress that change so the node doesn't visually vanish before deciding.
    // Delete-key removals ALWAYS route through the shared delete path (suppress the raw change so the
    // node doesn't vanish before confirming): one item → deleteOne (confirm or orphan dialog), many →
    // deleteMany (one confirm). Same behaviour as the context menu + property grid.
    const removeChanges = changes.filter(c => c.type === "remove" && !isAnno(c.id));
    let suppress: any[] = [];
    if (removeChanges.length > 0) {
      suppress = removeChanges;
      const ids = removeChanges.map(c => c.id);
      if (ids.length === 1) deleteOne(ids[0]); else deleteMany(ids);
    }
    onNodesChangeInternal(suppress.length ? changes.filter(c => !suppress.includes(c)) : changes);

    // Group drag: move the resource nodes inside it live (rf state), persist on drop (group + members).
    for (const c of changes) {
      if (c.type !== "position" || !c.id.startsWith("group:") || !c.position) continue;
      const g = (stack.groups ?? []).find(x => x.id === c.id);
      if (!g) continue;
      let d = groupDrag.current[c.id];
      if (!d) { d = { sx: g.x, sy: g.y, lx: g.x, ly: g.y, members: nodesInGroup(stack, g) }; groupDrag.current[c.id] = d; }
      const dx = c.position.x - d.lx, dy = c.position.y - d.ly;
      if ((dx || dy) && d.members.length)
        setRfNodes(ns => ns.map(n => d!.members.includes(n.id) ? { ...n, position: { x: n.position.x + dx, y: n.position.y + dy } } : n));
      d.lx = c.position.x; d.ly = c.position.y;
      if (c.dragging === false) {
        const tdx = c.position.x - d.sx, tdy = c.position.y - d.sy;
        const nodes = stack.nodes.map(n => d!.members.includes(n.id) ? { ...n, x: n.x + tdx, y: n.y + tdy } : n);
        api.saveStack({ ...stack, nodes, groups: (stack.groups ?? []).map(gg => gg.id === c.id ? { ...gg, x: c.position.x, y: c.position.y } : gg) }).then(setStack);
        delete groupDrag.current[c.id];
      }
    }

    // Persist notes position + group resize on drop (group position handled above).
    let notes = stack.notes ?? [], groups = stack.groups ?? [], annoDirty = false;
    for (const c of changes) {
      if (c.type === "position" && c.dragging === false && c.id.startsWith("note:") && c.position) {
        notes = notes.map(n => n.id === c.id ? { ...n, x: c.position.x, y: c.position.y } : n);
        annoDirty = true;
      }
      if (c.type === "dimensions" && c.resizing === false && c.id.startsWith("group:") && c.dimensions) {
        groups = groups.map(g => g.id === c.id ? { ...g, width: c.dimensions.width, height: c.dimensions.height } : g);
        annoDirty = true;
      }
    }
    if (annoDirty) api.saveStack({ ...stack, notes, groups }).then(setStack);

    // Persist moved resource nodes in ONE saveStack — a multi-select drag emits a position change per
    // node, and firing a patchNode each (all off the same old stack) raced so only one stuck.
    const moved = changes.filter(c => c.type === "position" && c.dragging === false && !isAnno(c.id) && c.position);
    if (moved.length > 0) {
      const at = new Map(moved.map(c => [c.id, c.position]));
      const nodes = stack.nodes.map(n => at.has(n.id) ? { ...n, x: at.get(n.id).x, y: at.get(n.id).y } : n);
      api.saveStack({ ...stack, nodes }).then(setStack);
    }
    const removed = changes.filter(c => c.type === "remove");
    const removedAnno = removed.filter(c => isAnno(c.id));
    if (removedAnno.length > 0)
      api.saveStack({ ...stack,
        notes: (stack.notes ?? []).filter(n => !removedAnno.some(c => c.id === n.id)),
        groups: (stack.groups ?? []).filter(g => !removedAnno.some(c => c.id === g.id)) }).then(setStack);
    const removedNodes = removed.filter(c => !isAnno(c.id) && !suppress.includes(c));
    if (removedNodes.length > 0) {
      const next = removedNodes.reduce((s, c) => removeNode(s, c.id), stack);
      api.saveStack(next).then(setStack);
      onSelect(null);
    }
  }, [stack, setStack, onSelect, onNodesChangeInternal, deleteOne, deleteMany, setRfNodes]);
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
      if (n.type !== "resource") return n; // annotations pass through unchanged
      const data = { ...n.data, live: overlay.statusByNodeId[n.id], onLogs };
      const opacity = q && !`${n.data.resourceName} ${n.data.addMethod}`.toLowerCase().includes(q) ? 0.25 : 1;
      const glowing = glow.has(n.id);
      return { ...n, data, style: { ...n.style, opacity,
        boxShadow: glowing ? "0 0 0 3px var(--mantine-primary-color-filled), 0 0 16px var(--mantine-primary-color-filled)" : undefined,
        borderRadius: glowing ? 12 : undefined, transition: "box-shadow .3s" } };
    });
    return [...base, ...liveFlow.rfLive];
  }, [rfNodes, overlay, liveFlow, q, onLogs, glow]);
  const allEdges = useMemo(() => [...edges, ...liveFlow.rfLiveEdges], [edges, liveFlow]);
  // Derived from node state (not an onSelectionChange callback — that fired during unmount and threw,
  // leaving the route half-rendered so "<- Stacks" appeared dead).
  const selIds = useMemo(() => rfNodes.filter(n => n.selected && n.type === "resource").map(n => n.id), [rfNodes]);
  // Publish the multi-selection so the Properties panel can show count + batch actions.
  useEffect(() => { onSelectIds?.(selIds); }, [selIds, onSelectIds]);
  // Ctrl/Cmd+A selects all resource nodes (ignored while typing in a field/editor).
  useEffect(() => {
    const h = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey) || e.key.toLowerCase() !== "a") return;
      const t = e.target as HTMLElement | null;
      if (t?.closest?.(".monaco-editor, input, textarea, [contenteditable=true]")) return;
      e.preventDefault();
      setRfNodes(ns => ns.map(n => n.type === "resource" ? { ...n, selected: true } : n));
    };
    window.addEventListener("keydown", h);
    return () => window.removeEventListener("keydown", h);
  }, [setRfNodes]);

  return (
    <>
    <ReactFlow nodes={displayNodes} edges={allEdges} nodeTypes={nodeTypes} edgeTypes={edgeTypes}
      onInit={i => { rf.current = i; }}
      colorMode={colorScheme === "light" ? "light" : "dark"}
      snapToGrid snapGrid={[16, 16]}
      onNodesChange={onNodesChange} onConnect={onConnect} onEdgesChange={onEdgesChange}
      deleteKeyCode={["Backspace", "Delete"]}
      onNodeClick={(_, n) => { if (!n.id.startsWith("live:") && !n.id.startsWith("note:") && !n.id.startsWith("group:")) onSelect(n.id); }}
      onNodeContextMenu={(e, n) => { if (n.id.startsWith("live:") || n.id.startsWith("note:")) return; e.preventDefault(); setMenu({ x: e.clientX, y: e.clientY, nodeId: n.id }); }}
      onPaneClick={() => setMenu(null)} onMoveStart={() => setMenu(null)}
      onNodeDragStart={() => setMenu(null)} fitView>
      <Background /><Controls />
      {showMinimap && <MiniMap pannable zoomable nodeColor={n => (n.data as any).addMethod ? resourceVisual((n.data as any).addMethod).color : "#888"} />}
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
          <Tooltip label={showMinimap ? "Hide minimap" : "Show minimap"} withArrow>
            <UnstyledButton onClick={toggleMinimap}
              style={{ display: "flex", alignItems: "center", padding: 6, borderRadius: 6,
                background: "var(--mantine-color-body)", border: "1px solid var(--mantine-color-default-border)" }}>
              {showMinimap ? <IconMapOff size={15} /> : <IconMap size={15} />}
            </UnstyledButton>
          </Tooltip>
          <Tooltip label="Add a sticky note" withArrow>
            <UnstyledButton onClick={annoOps.addNote}
              style={{ display: "flex", alignItems: "center", padding: 6, borderRadius: 6,
                background: "var(--mantine-color-body)", border: "1px solid var(--mantine-color-default-border)" }}>
              <IconNote size={15} />
            </UnstyledButton>
          </Tooltip>
          <Menu shadow="md" width={230} position="bottom-start" withArrow>
            <Menu.Target>
              <Tooltip label="Boundary groups" withArrow>
                <UnstyledButton
                  style={{ display: "flex", alignItems: "center", padding: 6, borderRadius: 6,
                    background: "var(--mantine-color-body)", border: "1px solid var(--mantine-color-default-border)" }}>
                  <IconBoxMargin size={15} />
                </UnstyledButton>
              </Tooltip>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Item leftSection={<IconBoxMargin size={14} />} onClick={annoOps.addGroup}>Add a simple group</Menu.Item>
              <Menu.Item leftSection={<IconLayoutGrid size={14} />} onClick={autoGroupAll}>
                Auto-group all by palette group
              </Menu.Item>
            </Menu.Dropdown>
          </Menu>
        </Group>
      </Panel>
      {menu && (
        <Paper ref={menuRef} shadow="md" withBorder p={4} radius="sm"
          style={{ position: "fixed", left: menu.x, top: menu.y, zIndex: 1000, minWidth: 160 }}>
          {(menu.nodeId.startsWith("group:")
            ? [
                { icon: IconCopy, label: "Duplicate group", run: () => duplicateGroup(menu.nodeId), color: undefined },
                { icon: IconBookmark, label: "Save group as snippet", run: () => { const g = (stack.groups ?? []).find(x => x.id === menu.nodeId); saveAsSnippet(nodesInGroup(stack, g!), g?.label || "group"); }, color: undefined },
                { icon: IconTrash, label: "Delete group", run: () => onGroupDelete(menu.nodeId), color: "var(--mantine-color-red-text)" },
              ]
            : [
                { icon: IconPencil, label: "Edit properties", run: () => { onSelect(menu.nodeId); onShowProperties?.(); }, color: undefined },
                { icon: IconCopy, label: "Duplicate", run: () => duplicateNode(menu.nodeId), color: undefined },
                { icon: IconBookmark, label: "Save as snippet", run: () => { const n = stack.nodes.find(x => x.id === menu.nodeId); saveAsSnippet([menu.nodeId], n?.resourceName || "snippet", n?.icon ?? n?.addMethod); }, color: undefined },
                { icon: IconTrash, label: "Delete", run: () => deleteOne(menu.nodeId), color: "var(--mantine-color-red-text)" },
              ]).map(item => (
            <UnstyledButton key={item.label} onClick={() => { item.run(); setMenu(null); }}
              style={{ display: "flex", alignItems: "center", gap: 8, width: "100%", padding: "6px 8px", borderRadius: 4, fontSize: 13, color: item.color }}
              className="ctx-item">
              <item.icon size={15} /> {item.label}
            </UnstyledButton>
          ))}
        </Paper>
      )}
    </ReactFlow>
    <ResourceLogDrawer stackId={stack.id} target={logTarget} onClose={() => setLogTarget(null)} />
    {deleteDialog}
    {groupDel && (
      <Modal opened onClose={() => setGroupDel(null)} title="Delete group" size="md" centered>
        <MStack gap="sm">
          <Text size="sm">This group contains {groupDel.count} resource(s). Delete them too, or just remove the group boundary?</Text>
          <Group justify="flex-end" gap="xs">
            <Button variant="subtle" onClick={() => setGroupDel(null)}>Cancel</Button>
            <Button variant="default" onClick={() => { annoOps.remove(groupDel.id); setGroupDel(null); }}>Just the group</Button>
            <Button color="red" onClick={() => {
              const g = (stack.groups ?? []).find(x => x.id === groupDel.id);
              let next = stack;
              if (g) for (const nid of nodesInGroup(stack, g)) next = removeNode(next, nid);
              next = { ...next, groups: (next.groups ?? []).filter(x => x.id !== groupDel.id) };
              api.saveStack(next).then(s => { setStack(s); onSelect(null); toastOk("Group + contents deleted"); }).catch(toastErr);
              setGroupDel(null);
            }}>Delete group + {groupDel.count}</Button>
          </Group>
        </MStack>
      </Modal>
    )}
    </>
  );
}
