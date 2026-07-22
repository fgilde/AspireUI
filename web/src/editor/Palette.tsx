import { useEffect, useMemo, useRef, useState } from "react";
import { Stack as MStack, TextInput, Text, ScrollArea, Tooltip, Badge, Group, Accordion, UnstyledButton, Chip } from "@mantine/core";
import type { Stack, ResourceType, Node, ContainerPreset } from "../model";
import { sanitizeIdentifier } from "../model";
import { ResourceGlyph, resourceVisual } from "../resourceIcons";
import { toastOk, toastErr } from "../ui";
import * as api from "../api";
import { AddResourceDialog } from "./AddResourceDialog";

// Cross-cutting tags for the combinable filter (orthogonal to the group sections).
const GPU_PRESETS = new Set(["comfyui", "sdnext", "acestep"]);
const TAG_ORDER = ["app", "resource", "setup", "ai", "gpu", "observability", "azure"];
function presetTags(p: ContainerPreset): string[] {
  const g = (p.group || "").toLowerCase();
  const t = ["app"];
  if (g.includes("ai")) t.push("ai");
  if (g.includes("observability")) t.push("observability");
  if (GPU_PRESETS.has(p.id)) t.push("gpu");
  return t;
}
function rtTags(rt: ResourceType): string[] {
  const g = (rt.group || "").toLowerCase();
  const t = [rt.composite ? "setup" : "resource"];
  if (g === "ai") t.push("ai");
  if (g === "observability") t.push("observability");
  if (rt.addMethod.startsWith("AddAzure")) t.push("azure");
  return t;
}

// One compact palette tile: colored icon box + label + a small caption, hover-highlighted.
function Tile({ iconKey, label, caption, badge, onClick, tooltip }: {
  iconKey: string; label: string; caption?: string; badge?: string; onClick: () => void; tooltip?: string;
}) {
  const color = resourceVisual(iconKey).color;
  return (
    <Tooltip label={tooltip || label} position="right" withArrow openDelay={500} multiline w={250} disabled={!tooltip}>
      <UnstyledButton onClick={onClick} className="ctx-item"
        style={{ display: "flex", alignItems: "center", gap: 9, width: "100%", padding: "6px 8px", borderRadius: 8 }}>
        <div style={{ width: 30, height: 30, borderRadius: 7, flexShrink: 0, display: "grid", placeItems: "center",
          background: `${color}1f`, border: `1px solid ${color}33` }}>
          <ResourceGlyph addMethod={iconKey} size={17} />
        </div>
        <div style={{ minWidth: 0, flex: 1 }}>
          <Text size="sm" fw={550} truncate lh={1.15}>{label}</Text>
          {caption && <Text size="10px" c="dimmed" truncate lh={1.2}>{caption}</Text>}
        </div>
        {badge && <Badge size="xs" variant="light" color="grape" style={{ flexShrink: 0 }}>{badge}</Badge>}
      </UnstyledButton>
    </Tooltip>
  );
}

export function Palette({ stack, setStack }: { stack: Stack; setStack: (s: Stack) => void }) {
  const [cat, setCat] = useState<ResourceType[]>([]);
  const [presets, setPresets] = useState<ContainerPreset[]>([]);
  const [q, setQ] = useState("");
  const [selectedRt, setSelectedRt] = useState<ResourceType | null>(null);
  const [opened, setOpened] = useState<string[]>([]);
  const [activeTags, setActiveTags] = useState<string[]>([]);
  const inited = useRef(false);
  useEffect(() => { api.getCatalog().then(setCat); api.getPresets().then(setPresets).catch(() => {}); }, []);

  // Tags actually present, in a stable order — drives the filter chips.
  const allTags = useMemo(() => {
    const seen = new Set<string>();
    cat.forEach(r => rtTags(r).forEach(t => seen.add(t)));
    presets.forEach(p => presetTags(p).forEach(t => seen.add(t)));
    return TAG_ORDER.filter(t => seen.has(t));
  }, [cat, presets]);

  const groups = useMemo(() => {
    const ql = q.toLowerCase();
    const hasTags = (tags: string[]) => activeTags.every(t => tags.includes(t)); // combine = AND
    const by: Record<string, { rts: ResourceType[]; presets: ContainerPreset[] }> = {};
    for (const r of cat)
      if (r.label.toLowerCase().includes(ql) && hasTags(rtTags(r)))
        (by[r.group || "Other"] ??= { rts: [], presets: [] }).rts.push(r);
    for (const p of presets)
      if (p.label.toLowerCase().includes(ql) && hasTags(presetTags(p)))
        (by[p.group || "Apps"] ??= { rts: [], presets: [] }).presets.push(p);
    return by;
  }, [cat, presets, q, activeTags]);

  // Sort groups: AspireUI first (🤯), then alphabetical.
  const groupKeys = useMemo(() => Object.keys(groups)
    .sort((a, b) => (a === "AspireUI" ? -1 : b === "AspireUI" ? 1 : a.localeCompare(b))), [groups]);
  useEffect(() => { if (!inited.current && groupKeys.length) { setOpened(groupKeys); inited.current = true; } }, [groupKeys]);

  const onCreate = (node: Node, refIds: string[], usedByIds: string[]) => {
    const eid = () => "e" + crypto.randomUUID().slice(0, 8);
    const edges = [
      ...refIds.map(toNodeId => ({ id: eid(), fromNodeId: node.id, toNodeId, kind: "reference" })),
      ...usedByIds.map(fromNodeId => ({ id: eid(), fromNodeId, toNodeId: node.id, kind: "reference" })),
    ];
    const extraPackages = [...stack.extraPackages];
    const pkg = selectedRt?.package;
    if (node.composite && pkg && !extraPackages.some(p => p.id === pkg))
      extraPackages.push({ id: pkg, version: selectedRt?.packageVersion || "" });
    api.saveStack({ ...stack, nodes: [...stack.nodes, node], edges: [...stack.edges, ...edges], extraPackages }).then(setStack);
    setSelectedRt(null);
  };

  const createPreset = (p: ContainerPreset) => {
    const taken = new Set(stack.nodes.map(n => n.resourceName));
    let name = p.id, i = 2;
    while (taken.has(name)) name = `${p.id}${i++}`;
    const node: Node = {
      id: "n" + crypto.randomUUID().slice(0, 8),
      varName: sanitizeIdentifier(name), resourceName: name, addMethod: "AddContainer",
      addArgs: [JSON.stringify(p.image)],
      withCalls: [
        { method: "WithHttpEndpoint", args: [`targetPort: ${p.port}`] },
        ...(p.env ?? []).map(([k, v]) => ({ method: "WithEnvironment", args: [JSON.stringify(k), JSON.stringify(v)] })),
      ],
      x: 60 + stack.nodes.length * 28, y: 60 + stack.nodes.length * 28,
    };
    api.saveStack({ ...stack, nodes: [...stack.nodes, node] })
      .then(s => { setStack(s); toastOk(`Added ${p.label}`); }).catch(toastErr);
  };

  return (
    <MStack gap="xs" p="sm" h="100%">
      <TextInput placeholder="Search…" value={q} onChange={e => setQ(e.currentTarget.value)} />
      {allTags.length > 0 && (
        <Chip.Group multiple value={activeTags} onChange={setActiveTags}>
          <Group gap={5}>
            {allTags.map(t => <Chip key={t} value={t} size="xs" variant="light">{t}</Chip>)}
          </Group>
        </Chip.Group>
      )}
      <ScrollArea style={{ flex: 1 }} offsetScrollbars scrollbarSize={8}>
        <Accordion multiple value={(q || activeTags.length > 0) ? groupKeys : opened} onChange={setOpened} chevronPosition="left"
          styles={{ control: { padding: "6px 4px" }, content: { padding: "2px 0 8px 14px" }, item: { border: "none" }, label: { padding: 0 } }}>
          {groupKeys.map(g => {
            const items = groups[g];
            const count = items.rts.length + items.presets.length;
            return (
              <Accordion.Item key={g} value={g}>
                <Accordion.Control>
                  <Group gap={7} wrap="nowrap">
                    <Text size="xs" fw={700} tt="uppercase" c="dimmed" style={{ letterSpacing: 0.4 }}>{g}</Text>
                    <Badge size="xs" variant="default" c="dimmed">{count}</Badge>
                  </Group>
                </Accordion.Control>
                <Accordion.Panel>
                  <MStack gap={1}>
                    {items.rts.map(rt => (
                      <Tile key={rt.addMethod} iconKey={rt.addMethod} label={rt.label} caption={rt.addMethod}
                        tooltip={rt.description || undefined} onClick={() => setSelectedRt(rt)} />
                    ))}
                    {items.presets.map(p => (
                      <Tile key={p.id} iconKey={p.icon || ""} label={p.label} caption={`:${p.port} · ${p.image.split("/").pop()}`}
                        badge="app" tooltip={p.description || p.image} onClick={() => createPreset(p)} />
                    ))}
                  </MStack>
                </Accordion.Panel>
              </Accordion.Item>
            );
          })}
        </Accordion>
      </ScrollArea>
      {selectedRt && (
        <AddResourceDialog
          rt={selectedRt}
          existingCount={stack.nodes.filter(n => n.addMethod === selectedRt.addMethod).length}
          totalCount={stack.nodes.length}
          nodes={stack.nodes}
          onCreate={onCreate}
          onClose={() => setSelectedRt(null)}
        />
      )}
    </MStack>
  );
}
