import { useEffect, useMemo, useState } from "react";
import { Stack as MStack, TextInput, Text, Highlight, ScrollArea, Tooltip, Badge, Group, Accordion, UnstyledButton, Modal, Button, Select } from "@mantine/core";
import { IconFoldUp, IconFoldDown, IconPlus, IconMinus, IconCheck } from "@tabler/icons-react";
import type { Stack, ResourceType, Node, Edge, ContainerPreset, PresetCompanion, CompanionChoice } from "../model";
import { buildPresetNodes, reuseCandidates, parameterCandidates, ROLE_ALTERNATIVES } from "../model";
import { ResourceGlyph, resourceVisual } from "../resourceIcons";
import { toastOk, toastErr } from "../ui";
import * as api from "../api";
import { AddResourceDialog } from "./AddResourceDialog";

// Combinable filter tags — fully data-driven: a "kind" tag + the item's group + a couple of flags.
const GPU_PRESETS = new Set(["comfyui", "sdnext", "acestep"]);
const KINDS = ["app", "resource", "setup"];
const TAG_COLLAPSE = 8; // ~2 rows before the +N toggle
function presetTags(p: ContainerPreset): string[] {
  const t = ["app"];
  if (p.group) t.push(p.group);
  if (GPU_PRESETS.has(p.id)) t.push("gpu");
  return t;
}
function rtTags(rt: ResourceType): string[] {
  const t = [rt.composite ? "setup" : "resource"];
  if (rt.group) t.push(rt.group);
  if (rt.addMethod.startsWith("AddAzure")) t.push("azure");
  return t;
}
// kinds first, then everything else alphabetical.
function sortTags(tags: string[]): string[] {
  return [...tags].sort((a, b) => {
    const ai = KINDS.indexOf(a), bi = KINDS.indexOf(b);
    if (ai >= 0 || bi >= 0) return (ai < 0 ? 99 : ai) - (bi < 0 ? 99 : bi);
    return a.localeCompare(b);
  });
}

// One compact palette tile: colored icon box + label + a small caption, hover-highlighted.
function Tile({ iconKey, label, caption, badge, onClick, tooltip, highlight }: {
  iconKey: string; label: string; caption?: string; badge?: string; onClick: () => void; tooltip?: string; highlight?: string;
}) {
  const color = resourceVisual(iconKey).color;
  return (
    <Tooltip label={tooltip || label} position="right" withArrow openDelay={500} multiline w={250} disabled={!tooltip}>
      {/* Draggable for feel — dropping anywhere runs the same add as a click (dialog opens for resources,
          companion prompt for presets). Prevents the "why can't I drag this" confusion. */}
      <UnstyledButton onClick={onClick} draggable onDragEnd={onClick} className="ctx-item"
        style={{ display: "flex", alignItems: "center", gap: 9, width: "100%", padding: "6px 8px", borderRadius: 8, cursor: "grab" }}>
        <div style={{ width: 30, height: 30, borderRadius: 7, flexShrink: 0, display: "grid", placeItems: "center",
          background: `${color}1f`, border: `1px solid ${color}33` }}>
          <ResourceGlyph addMethod={iconKey} size={17} />
        </div>
        <div style={{ minWidth: 0, flex: 1 }}>
          <Highlight component={Text} highlight={highlight || ""} size="sm" fw={550} truncate lh={1.15}>{label}</Highlight>
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
  // Groups the user explicitly collapsed (everything else stays expanded — incl. groups that appear later).
  const [collapsed, setCollapsed] = useState<string[]>([]);
  // Tri-state tag filter: "in" = must have, "ex" = must NOT have (negate), absent = ignore.
  const [tagState, setTagState] = useState<Record<string, "in" | "ex">>({});
  const cycleTag = (t: string) => setTagState(s => {
    const next = { ...s };
    if (!s[t]) next[t] = "in"; else if (s[t] === "in") next[t] = "ex"; else delete next[t];
    return next;
  });
  const [tagsExpanded, setTagsExpanded] = useState(false);
  const [presetPick, setPresetPick] = useState<ContainerPreset | null>(null);
  useEffect(() => { api.getCatalog().then(setCat); api.getPresets().then(setPresets).catch(() => {}); }, []);

  // Every tag actually assigned (kinds + groups + flags), stable order — drives the filter chips.
  const allTags = useMemo(() => {
    const seen = new Set<string>();
    cat.forEach(r => rtTags(r).forEach(t => seen.add(t)));
    presets.forEach(p => presetTags(p).forEach(t => seen.add(t)));
    return sortTags([...seen]);
  }, [cat, presets]);

  const groups = useMemo(() => {
    const ql = q.toLowerCase();
    const inc = Object.keys(tagState).filter(t => tagState[t] === "in");
    const exc = Object.keys(tagState).filter(t => tagState[t] === "ex");
    // Include = OR (union: any checked tag matches), Exclude = must have none of them.
    const hasTags = (tags: string[]) =>
      (inc.length === 0 || inc.some(t => tags.includes(t))) && !exc.some(t => tags.includes(t));
    const by: Record<string, { rts: ResourceType[]; presets: ContainerPreset[] }> = {};
    for (const r of cat)
      if (r.label.toLowerCase().includes(ql) && hasTags(rtTags(r)))
        (by[r.group || "Other"] ??= { rts: [], presets: [] }).rts.push(r);
    for (const p of presets)
      if (p.label.toLowerCase().includes(ql) && hasTags(presetTags(p)))
        (by[p.group || "Apps"] ??= { rts: [], presets: [] }).presets.push(p);
    return by;
  }, [cat, presets, q, tagState]);

  // Sort groups: AspireUI first (🤯), then alphabetical.
  const groupKeys = useMemo(() => Object.keys(groups)
    .sort((a, b) => (a === "AspireUI" ? -1 : b === "AspireUI" ? 1 : a.localeCompare(b))), [groups]);
  // Expanded = all groups minus the ones the user collapsed — respected even while filtering/searching.
  const openValue = groupKeys.filter(g => !collapsed.includes(g));
  const allOpen = groupKeys.every(g => !collapsed.includes(g));

  const onCreate = (node: Node, refIds: string[], usedByIds: string[], extra?: { nodes: Node[]; edges: Edge[] }) => {
    const eid = () => "e" + crypto.randomUUID().slice(0, 8);
    const edges = [
      ...refIds.map(toNodeId => ({ id: eid(), fromNodeId: node.id, toNodeId, kind: "reference" })),
      ...usedByIds.map(fromNodeId => ({ id: eid(), fromNodeId, toNodeId: node.id, kind: "reference" })),
      ...(extra?.edges ?? []),
    ];
    const extraPackages = [...stack.extraPackages];
    const pkg = selectedRt?.package;
    if (node.composite && pkg && !extraPackages.some(p => p.id === pkg))
      extraPackages.push({ id: pkg, version: selectedRt?.packageVersion || "" });
    api.saveStack({ ...stack, nodes: [...stack.nodes, node, ...(extra?.nodes ?? [])], edges: [...stack.edges, ...edges], extraPackages }).then(setStack);
    setSelectedRt(null);
  };

  const dropPreset = (p: ContainerPreset, choices?: Record<string, CompanionChoice> | "none") => {
    const off = stack.nodes.length * 28;
    const { nodes, edges } = buildPresetNodes(p, stack.nodes, choices);
    const placed = nodes.map(n => ({ ...n, x: n.x + off, y: n.y + off }));
    api.saveStack({ ...stack, nodes: [...stack.nodes, ...placed], edges: [...stack.edges, ...edges] })
      .then(s => { setStack(s); toastOk(`Added ${p.label}`); }).catch(toastErr);
  };
  // Presets with companions or params ask first (backend choice + parameter/value picks); others drop directly.
  const createPreset = (p: ContainerPreset) => (p.companions?.length || p.params?.length) ? setPresetPick(p) : dropPreset(p);

  return (
    <MStack gap="xs" p="sm" h="100%">
      <TextInput placeholder="Search…" value={q} onChange={e => setQ(e.currentTarget.value)} />
      {allTags.length > 0 && (
        <Group gap={5}>
          {(tagsExpanded ? allTags : allTags.slice(0, TAG_COLLAPSE)).map(t => {
            const st = tagState[t];
            const base = { display: "flex", alignItems: "center", gap: 3, padding: "2px 9px", borderRadius: 999, fontSize: 12, cursor: "pointer" } as const;
            const style = st === "in"
              ? { ...base, background: "var(--mantine-primary-color-light)", color: "var(--mantine-primary-color-light-color)", border: "1px solid transparent" }
              : st === "ex"
                ? { ...base, background: "var(--mantine-color-red-light)", color: "var(--mantine-color-red-light-color)", border: "1px solid transparent", textDecoration: "line-through" }
                : { ...base, border: "1px solid var(--mantine-color-default-border)", color: "var(--mantine-color-dimmed)" };
            return (
              <UnstyledButton key={t} style={style} onClick={() => cycleTag(t)}
                title={st === "in" ? "Included — click to exclude" : st === "ex" ? "Excluded — click to clear" : "Click to include"}>
                {st === "ex" && <IconMinus size={11} />}{st === "in" && <IconCheck size={11} />}{t}
              </UnstyledButton>
            );
          })}
          {allTags.length > TAG_COLLAPSE && (
            <UnstyledButton onClick={() => setTagsExpanded(v => !v)} title={tagsExpanded ? "Show fewer" : "Show all filters"}
              style={{ display: "flex", alignItems: "center", gap: 2, padding: "2px 9px", borderRadius: 999, fontSize: 12,
                border: "1px dashed var(--mantine-color-default-border)", color: "var(--mantine-color-dimmed)" }}>
              {tagsExpanded ? <><IconMinus size={12} /> less</> : <><IconPlus size={12} />{allTags.length - TAG_COLLAPSE}</>}
            </UnstyledButton>
          )}
        </Group>
      )}
      <Group justify="flex-end" gap={4}>
        <Tooltip label={allOpen ? "Collapse all" : "Expand all"} withArrow>
          <UnstyledButton onClick={() => setCollapsed(allOpen ? groupKeys : [])}
            style={{ display: "flex", alignItems: "center", gap: 4, fontSize: 11, color: "var(--mantine-color-dimmed)" }}>
            {allOpen ? <IconFoldUp size={14} /> : <IconFoldDown size={14} />}
            {allOpen ? "Collapse all" : "Expand all"}
          </UnstyledButton>
        </Tooltip>
      </Group>
      <ScrollArea style={{ flex: 1 }} offsetScrollbars scrollbarSize={8}>
        <Accordion multiple value={openValue}
          onChange={v => setCollapsed(groupKeys.filter(g => !v.includes(g)))} chevronPosition="left"
          styles={{ control: { padding: "1px 0" }, chevron: { marginInlineEnd: 6 }, content: { padding: "2px 0 8px 23px" }, item: { border: "none" }, label: { padding: 0 } }}>
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
                        highlight={q} tooltip={rt.description || undefined} onClick={() => setSelectedRt(rt)} />
                    ))}
                    {items.presets.map(p => (
                      <Tile key={p.id} iconKey={p.icon || ""} label={p.label} highlight={q}
                        caption={[`:${p.port}`, p.gpu ? "GPU" : null, p.hostNetwork ? "host-net" : null, p.volumes?.length ? `${p.volumes.length} vol` : null].filter(Boolean).join(" · ")}
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
      {presetPick && (
        <CompanionPickerModal preset={presetPick} stackNodes={stack.nodes}
          onCancel={() => setPresetPick(null)}
          onConfirm={choices => { dropPreset(presetPick, choices); setPresetPick(null); }} />
      )}
    </MStack>
  );
}

// Per-companion backend chooser for a preset with dependencies: reuse an existing matching resource,
// drop the default container, or add an Aspire alternative — so stacks don't duplicate DB/LLM/etc.
function CompanionPickerModal({ preset, stackNodes, onConfirm, onCancel }: {
  preset: ContainerPreset; stackNodes: Node[];
  onConfirm: (choices: Record<string, CompanionChoice> | "none") => void; onCancel: () => void;
}) {
  const companions = preset.companions ?? [];
  const params = preset.params ?? [];
  const initial: Record<string, string> = {};
  for (const c of companions) {
    const reuse = reuseCandidates(stackNodes, c.role)[0];
    initial[c.key] = reuse ? `reuse:${reuse.id}` : "new";
  }
  for (const p of params) initial[`param:${p.key}`] = "new"; // default: a fresh parameter resource
  const [sel, setSel] = useState<Record<string, string>>(initial);
  // Literal value per param (used only when its mode is "value"); prefilled from the seeded default.
  const [vals, setVals] = useState<Record<string, string>>(
    () => Object.fromEntries(params.map(p => [p.key, p.default ?? ""])));

  const optionsFor = (c: PresetCompanion) => {
    const opts: { value: string; label: string }[] = [];
    for (const n of reuseCandidates(stackNodes, c.role))
      opts.push({ value: `reuse:${n.id}`, label: `Reuse existing: ${n.resourceName}` });
    opts.push({ value: "new", label: `New container: ${c.image}` });
    for (const alt of (c.role ? ROLE_ALTERNATIVES[c.role] ?? [] : []))
      opts.push({ value: `add:${alt.addMethod}`, label: `Add ${alt.label}` });
    return opts;
  };
  const paramOptions = (secret?: boolean) => {
    const opts = [
      { value: "new", label: secret ? "New parameter (secret)" : "New parameter" },
      { value: "value", label: "Write value (plain env)" },
    ];
    for (const n of parameterCandidates(stackNodes))
      opts.push({ value: `reuse:${n.id}`, label: `Reuse existing: ${n.resourceName}` });
    return opts;
  };
  const toChoice = (v: string): CompanionChoice =>
    v.startsWith("reuse:") ? { mode: "reuse", nodeId: v.slice(6) }
      : v.startsWith("add:") ? { mode: "add", addMethod: v.slice(4) }
      : { mode: "new" };

  const buildChoices = () => ({
    ...Object.fromEntries(companions.map(c => [c.key, toChoice(sel[c.key])])),
    ...Object.fromEntries(params.map(p => [`param:${p.key}`,
      sel[`param:${p.key}`] === "value"
        ? { mode: "value", value: vals[p.key] ?? "" } as CompanionChoice
        : toChoice(sel[`param:${p.key}`])])),
  });

  return (
    <Modal opened onClose={onCancel} title={`Add ${preset.label}`} size="lg" centered>
      <MStack gap="sm">
        <Text size="sm" c="dimmed">{preset.description}</Text>
        {companions.length > 0 && <Text size="sm" fw={600}>Dependencies</Text>}
        {companions.map(c => (
          <Group key={c.key} gap="sm" wrap="nowrap" align="center">
            <Text size="sm" w={120} truncate>{c.role || c.key}</Text>
            <Select style={{ flex: 1 }} size="xs" allowDeselect={false}
              data={optionsFor(c)} value={sel[c.key]}
              onChange={v => v && setSel(s => ({ ...s, [c.key]: v }))} />
          </Group>
        ))}
        {params.length > 0 && <Text size="sm" fw={600}>Parameters</Text>}
        {params.map(p => (
          <Group key={p.key} gap="sm" wrap="nowrap" align="center">
            <Text size="sm" w={120} truncate title={p.env}>{p.key}{p.secret ? " 🔒" : ""}</Text>
            <Select w={200} size="xs" allowDeselect={false}
              data={paramOptions(p.secret)} value={sel[`param:${p.key}`]}
              onChange={v => v && setSel(st => ({ ...st, [`param:${p.key}`]: v }))} />
            {sel[`param:${p.key}`] === "value" && (
              <TextInput style={{ flex: 1 }} size="xs" placeholder={p.env}
                type={p.secret ? "password" : undefined}
                value={vals[p.key] ?? ""} onChange={e => setVals(s => ({ ...s, [p.key]: e.currentTarget.value }))} />
            )}
          </Group>
        ))}
        <Text size="xs" c="dimmed">Reuse picks a resource already on the canvas; “Add …” drops a real Aspire resource. A parameter becomes an AddParameter resource (seeded default, change before publishing); “Write value” just sets a literal env value.</Text>
        <Group justify="flex-end" gap="xs" mt="xs">
          <Button variant="subtle" onClick={() => onConfirm("none")}>Just the app</Button>
          <Button onClick={() => onConfirm(buildChoices())}>Add</Button>
        </Group>
      </MStack>
    </Modal>
  );
}
