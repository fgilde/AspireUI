import { useEffect, useMemo, useState } from "react";
import { Tabs, ScrollArea, Text, MultiSelect, Group, ActionIcon, ThemeIcon, Button, Stack as MStack, Badge } from "@mantine/core";
import { IconTrash, IconCopy, IconBookmark } from "@tabler/icons-react";
import type { Stack, ResourceType, OrphanDep } from "../model";
import { removeNode, orphanableDeps, sanitizeIdentifier, collectSubgraph } from "../model";
import { resourceVisual, ResourceGlyph } from "../resourceIcons";
import { confirmDelete, toastOk, toastErr, promptText } from "../ui";
import * as api from "../api";
import { PropertyGrid } from "./PropertyGrid";
import { SmartDeleteModal } from "./Canvas";
import { PANEL_FLASH_STYLE } from "./DockLayout";

export function PropertyPanel({ stack, nodeId, selectedIds = [], flash = false, setStack, onDeleted }:
  { stack: Stack; nodeId: string | null; selectedIds?: string[]; flash?: boolean;
    setStack: (s: Stack) => void; onDeleted: () => void }) {
  const [catalog, setCatalog] = useState<ResourceType[]>([]);
  useEffect(() => { api.getCatalog().then(setCatalog); }, []);

  const node = stack.nodes.find(n => n.id === nodeId) ?? null;
  const rt = node ? catalog.find(r => r.addMethod === node.addMethod) : undefined;
  const [del, setDel] = useState<{ node: NonNullable<typeof node>; deps: OrphanDep[] } | null>(null);

  // Same smart-delete flow as the canvas: if the node has deps that would be orphaned, offer to remove
  // them too; otherwise a plain confirm.
  const onDelete = () => {
    if (!nodeId || !node) return;
    const deps = orphanableDeps(stack, nodeId);
    if (deps.length > 0) { setDel({ node, deps }); return; }
    confirmDelete(`"${node.resourceName}"`, "This also removes its connections and any code that references it.").then(ok => {
      if (!ok) return;
      api.saveStack(removeNode(stack, nodeId)).then(s => { setStack(s); onDeleted(); toastOk("Resource deleted"); }).catch(toastErr);
    });
  };
  const applyDelete = (alsoRemove: string[]) => {
    if (!nodeId) return;
    let next = removeNode(stack, nodeId);
    for (const d of alsoRemove) next = removeNode(next, d);
    api.saveStack(next).then(s => { setStack(s); onDeleted(); setDel(null); toastOk("Deleted"); }).catch(toastErr);
  };

  const others = useMemo(() => stack.nodes.filter(n => n.id !== nodeId), [stack.nodes, nodeId]);
  const refEdges = useMemo(() => stack.edges.filter(e => e.fromNodeId === nodeId), [stack.edges, nodeId]);
  const refValue = refEdges.map(e => e.toNodeId);

  const onRefsChange = (next: string[]) => {
    if (!nodeId) return;
    const added = next.filter(id => !refValue.includes(id));
    const removed = refEdges.filter(e => !next.includes(e.toNodeId));

    Promise.all(added.map(toNodeId => api.addEdge(stack.id, { fromNodeId: nodeId, toNodeId, kind: "reference" })))
      .then(results => {
        const withAdds = results.length > 0 ? results[results.length - 1] : stack;
        return Promise.all(removed.map(e => api.deleteEdge(stack.id, e.id))).then(() => withAdds);
      })
      .then(withAdds => {
        setStack({ ...withAdds, edges: withAdds.edges.filter(e => !removed.some(r => r.id === e.id)) });
      });
  };

  // Save a set of nodes (with their spawned deps + referenced params + internal edges) as a reusable
  // palette snippet, so the whole configured part can be dropped again from the Custom tab.
  const saveAsSnippet = (rootIds: string[], defaultName: string, icon?: string | null) => {
    promptText("Save as snippet", "Snippet name", defaultName).then(name => {
      if (!name) return;
      const { nodes, edges } = collectSubgraph(stack, rootIds);
      const files = stack.extraFiles ?? [];
      api.saveSnippet({ id: "", name, group: "Custom", icon: icon ?? null, nodes, edges, files })
        .then(() => { toastOk(`Saved snippet "${name}"`); window.dispatchEvent(new Event("aspireui:snippets-changed")); })
        .catch(toastErr);
    });
  };

  // Batch actions on a multi-selection (2+ nodes) — surfaced here instead of a canvas overlay button.
  const multi = selectedIds.filter(id => stack.nodes.some(n => n.id === id));
  const deleteMany = () => {
    confirmDelete(`${multi.length} resources`, "This also removes their connections and any code that references them.")
      .then(ok => {
        if (!ok) return;
        const next = multi.reduce((s, id) => removeNode(s, id), stack);
        api.saveStack(next).then(s => { setStack(s); onDeleted(); toastOk(`Deleted ${multi.length}`); }).catch(toastErr);
      });
  };
  const duplicateMany = () => {
    const taken = new Set(stack.nodes.map(n => n.resourceName));
    const uniq = (base: string) => { let n = `${base}-copy`, i = 2; while (taken.has(n)) n = `${base}-copy${i++}`; taken.add(n); return n; };
    const copies = multi.map(id => stack.nodes.find(n => n.id === id)).filter(Boolean).map(n => {
      const name = uniq(n!.resourceName);
      return { ...n!, id: "n" + crypto.randomUUID().slice(0, 8), varName: sanitizeIdentifier(name),
        resourceName: name, x: n!.x + 40, y: n!.y + 40 };
    });
    api.saveStack({ ...stack, nodes: [...stack.nodes, ...copies] }).then(s => { setStack(s); toastOk(`Duplicated ${copies.length}`); }).catch(toastErr);
  };

  if (multi.length > 1) {
    return (
      <MStack gap="sm" p="md" style={flash ? PANEL_FLASH_STYLE : undefined}>
        <Group gap={8}><Badge size="lg" variant="light">{multi.length} selected</Badge></Group>
        <Text size="sm" c="dimmed">Batch actions apply to all selected resources.</Text>
        <Group gap="xs">
          <Button variant="light" leftSection={<IconCopy size={15} />} onClick={duplicateMany}>Duplicate</Button>
          <Button variant="light" leftSection={<IconBookmark size={15} />}
            onClick={() => saveAsSnippet(multi, "my-snippet")}>Save as snippet</Button>
          <Button color="red" variant="light" leftSection={<IconTrash size={15} />} onClick={deleteMany}>Delete</Button>
        </Group>
      </MStack>
    );
  }

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", ...(flash ? PANEL_FLASH_STYLE : {}) }}>
      {node && (() => {
        const { color } = resourceVisual(node.addMethod);
        return (
          <Group justify="space-between" px="sm" pt="xs" wrap="nowrap">
            <Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
              <ThemeIcon variant="light" size={24} radius="sm" style={{ background: `${color}22` }}>
                <ResourceGlyph addMethod={node.addMethod} size={16} />
              </ThemeIcon>
              <Text fw={600} size="sm" truncate>{node.resourceName}</Text>
            </Group>
            <Group gap={4} wrap="nowrap">
              <ActionIcon variant="subtle" title="Save as palette snippet"
                onClick={() => saveAsSnippet([node.id], node.resourceName, node.icon ?? node.addMethod)}>
                <IconBookmark size={16} />
              </ActionIcon>
              <ActionIcon color="red" variant="subtle" title="Delete node" onClick={onDelete}>
                <IconTrash size={16} />
              </ActionIcon>
            </Group>
          </Group>
        );
      })()}
      <Tabs defaultValue="props" style={{ flex: 1, minHeight: 0, display: "flex", flexDirection: "column" }}>
        <Tabs.List>
          <Tabs.Tab value="props">Properties</Tabs.Tab>
          <Tabs.Tab value="refs">References</Tabs.Tab>
        </Tabs.List>
        <ScrollArea style={{ flex: 1 }} p="sm">
          <Tabs.Panel value="props">
            {node
              ? <PropertyGrid stack={stack} node={node} rt={rt} setStack={setStack} />
              : <Text size="sm" c="dimmed">Select a node</Text>}
          </Tabs.Panel>
          <Tabs.Panel value="refs">
            {node
              ? <MultiSelect
                  label="References"
                  data={others.map(n => ({ value: n.id, label: n.resourceName }))}
                  value={refValue}
                  onChange={onRefsChange}
                  searchable
                  clearable
                />
              : <Text size="sm" c="dimmed">Select a node</Text>}
          </Tabs.Panel>
        </ScrollArea>
      </Tabs>
      {del && <SmartDeleteModal node={del.node} deps={del.deps}
        onCancel={() => setDel(null)} onConfirm={applyDelete} />}
    </div>
  );
}
