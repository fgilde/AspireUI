import { useEffect, useMemo, useState } from "react";
import { Tabs, ScrollArea, Text, MultiSelect, Group, ActionIcon, ThemeIcon } from "@mantine/core";
import { IconTrash } from "@tabler/icons-react";
import type { Stack, ResourceType } from "../model";
import { removeNode } from "../model";
import { resourceVisual } from "../resourceIcons";
import { confirmDelete, toastOk, toastErr } from "../ui";
import * as api from "../api";
import { PropertyGrid } from "./PropertyGrid";

export function PropertyPanel({ stack, nodeId, setStack, onDeleted }:
  { stack: Stack; nodeId: string | null; setStack: (s: Stack) => void; onDeleted: () => void }) {
  const [catalog, setCatalog] = useState<ResourceType[]>([]);
  useEffect(() => { api.getCatalog().then(setCatalog); }, []);

  const node = stack.nodes.find(n => n.id === nodeId) ?? null;
  const rt = node ? catalog.find(r => r.addMethod === node.addMethod) : undefined;

  const onDelete = () => {
    if (!nodeId || !node) return;
    confirmDelete(`"${node.resourceName}"`, "This also removes its connections and any code that references it.").then(ok => {
      if (!ok) return;
      api.saveStack(removeNode(stack, nodeId)).then(s => { setStack(s); onDeleted(); toastOk("Resource deleted"); }).catch(toastErr);
    });
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

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%" }}>
      {node && (() => {
        const { Icon, color } = resourceVisual(node.addMethod);
        return (
          <Group justify="space-between" px="sm" pt="xs" wrap="nowrap">
            <Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
              <ThemeIcon variant="light" size={24} radius="sm" style={{ color, background: `${color}22` }}>
                <Icon size={16} />
              </ThemeIcon>
              <Text fw={600} size="sm" truncate>{node.resourceName}</Text>
            </Group>
            <ActionIcon color="red" variant="subtle" title="Delete node" onClick={onDelete}>
              <IconTrash size={16} />
            </ActionIcon>
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
    </div>
  );
}
