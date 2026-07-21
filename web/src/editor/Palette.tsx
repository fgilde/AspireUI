import { useEffect, useMemo, useState } from "react";
import { Stack as MStack, TextInput, Text, Button, ScrollArea, Divider, Tooltip } from "@mantine/core";
import type { Stack, ResourceType, Node } from "../model";
import { ResourceGlyph } from "../resourceIcons";
import * as api from "../api";
import { AddResourceDialog } from "./AddResourceDialog";

export function Palette({ stack, setStack }: { stack: Stack; setStack: (s: Stack) => void }) {
  const [cat, setCat] = useState<ResourceType[]>([]);
  const [q, setQ] = useState("");
  const [selectedRt, setSelectedRt] = useState<ResourceType | null>(null);
  useEffect(() => { api.getCatalog().then(setCat); }, []);

  const groups = useMemo(() => {
    const f = cat.filter(r => r.label.toLowerCase().includes(q.toLowerCase()));
    const by: Record<string, ResourceType[]> = {};
    for (const r of f) (by[r.group || "Other"] ??= []).push(r);
    return by;
  }, [cat, q]);

  const onCreate = (node: Node, refIds: string[], usedByIds: string[]) => {
    const eid = () => "e" + crypto.randomUUID().slice(0, 8);
    const edges = [
      ...refIds.map(toNodeId => ({ id: eid(), fromNodeId: node.id, toNodeId, kind: "reference" })),
      ...usedByIds.map(fromNodeId => ({ id: eid(), fromNodeId, toNodeId: node.id, kind: "reference" })),
    ];
    // A composite/macro node's NuGet package isn't in the overlay AddMethod->package map (it's
    // discovered dynamically), so pull it in via extraPackages — deduped by id.
    const extraPackages = [...stack.extraPackages];
    const pkg = selectedRt?.package;
    if (node.composite && pkg && !extraPackages.some(p => p.id === pkg))
      extraPackages.push({ id: pkg, version: selectedRt?.packageVersion || "" });
    api.saveStack({ ...stack, nodes: [...stack.nodes, node], edges: [...stack.edges, ...edges], extraPackages }).then(setStack);
    setSelectedRt(null);
  };

  return (
    <MStack gap="xs" p="sm" h="100%">
      <TextInput placeholder="Search…" value={q} onChange={e => setQ(e.currentTarget.value)} />
      <ScrollArea style={{ flex: 1 }}>
        {Object.entries(groups).map(([g, items]) => (
          <div key={g}>
            <Divider my="xs" label={g} labelPosition="left" />
            {items.map(rt => (
              <Tooltip key={rt.addMethod} label="Click to add to the canvas" position="right" withArrow openDelay={400}>
                <Button variant="light" fullWidth justify="start" mb={4} onClick={() => setSelectedRt(rt)}
                  leftSection={<ResourceGlyph addMethod={rt.addMethod} size={16} />}>
                  <Text size="sm">{rt.label}</Text>
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
