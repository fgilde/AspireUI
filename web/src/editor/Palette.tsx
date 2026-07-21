import { useEffect, useMemo, useState } from "react";
import { Stack as MStack, TextInput, Text, Button, ScrollArea, Divider, Tooltip, Badge, Group } from "@mantine/core";
import type { Stack, ResourceType, Node, ContainerPreset } from "../model";
import { sanitizeIdentifier } from "../model";
import { ResourceGlyph } from "../resourceIcons";
import { toastOk, toastErr } from "../ui";
import * as api from "../api";
import { AddResourceDialog } from "./AddResourceDialog";

export function Palette({ stack, setStack }: { stack: Stack; setStack: (s: Stack) => void }) {
  const [cat, setCat] = useState<ResourceType[]>([]);
  const [presets, setPresets] = useState<ContainerPreset[]>([]);
  const [q, setQ] = useState("");
  const [selectedRt, setSelectedRt] = useState<ResourceType | null>(null);
  useEffect(() => { api.getCatalog().then(setCat); api.getPresets().then(setPresets).catch(() => {}); }, []);

  const groups = useMemo(() => {
    const ql = q.toLowerCase();
    const by: Record<string, { rts: ResourceType[]; presets: ContainerPreset[] }> = {};
    for (const r of cat)
      if (r.label.toLowerCase().includes(ql)) (by[r.group || "Other"] ??= { rts: [], presets: [] }).rts.push(r);
    for (const p of presets)
      if (p.label.toLowerCase().includes(ql)) (by[p.group || "Apps"] ??= { rts: [], presets: [] }).presets.push(p);
    return by;
  }, [cat, presets, q]);

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

  // A preset drops a ready-made AddContainer node (image + HTTP endpoint + any preset env) directly.
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
      <ScrollArea style={{ flex: 1 }}>
        {Object.entries(groups)
          .sort(([a], [b]) => (a === "AspireUI" ? -1 : b === "AspireUI" ? 1 : 0)) // pin AspireUI first — because why not 🤯
          .map(([g, items]) => (
          <div key={g}>
            <Divider my="xs" label={g} labelPosition="left" />
            {items.rts.map(rt => (
              <Tooltip key={rt.addMethod} label={rt.description || "Click to add to the canvas"} position="right" withArrow openDelay={400} multiline w={260}>
                <Button variant="light" fullWidth justify="start" mb={4} onClick={() => setSelectedRt(rt)}
                  leftSection={<ResourceGlyph addMethod={rt.addMethod} size={16} />}>
                  <Text size="sm">{rt.label}</Text>
                </Button>
              </Tooltip>
            ))}
            {items.presets.map(p => (
              <Tooltip key={p.id} label={p.description || p.image} position="right" withArrow openDelay={400} multiline w={260}>
                <Button variant="subtle" fullWidth justify="start" mb={4} onClick={() => createPreset(p)}
                  leftSection={<ResourceGlyph addMethod={p.icon || ""} size={16} />}>
                  <Group gap={6} wrap="nowrap" justify="space-between" style={{ flex: 1 }}>
                    <Text size="sm" truncate>{p.label}</Text>
                    <Badge size="xs" variant="light" color="grape">app</Badge>
                  </Group>
                </Button>
              </Tooltip>
            ))}
          </div>
        ))}
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
