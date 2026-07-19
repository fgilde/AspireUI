import { useEffect, useMemo, useState } from "react";
import { Tabs, ScrollArea, Text, MultiSelect } from "@mantine/core";
import type { Stack, ResourceType } from "../model";
import * as api from "../api";
import { PropertyGrid } from "./PropertyGrid";
import { CodePreview } from "./CodePreview";

export function PropertyPanel({ stack, nodeId, setStack }:
  { stack: Stack; nodeId: string | null; setStack: (s: Stack) => void }) {
  const [catalog, setCatalog] = useState<ResourceType[]>([]);
  useEffect(() => { api.getCatalog().then(setCatalog); }, []);

  const node = stack.nodes.find(n => n.id === nodeId) ?? null;
  const rt = node ? catalog.find(r => r.addMethod === node.addMethod) : undefined;

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
      <CodePreview stackId={stack.id} version={JSON.stringify(stack)} />
    </div>
  );
}
