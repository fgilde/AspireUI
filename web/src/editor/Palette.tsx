import { useEffect, useMemo, useState } from "react";
import { Stack as MStack, TextInput, Text, Button, ScrollArea, Divider } from "@mantine/core";
import type { Stack, ResourceType, Node } from "../model";
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

  const onCreate = (node: Node) => {
    api.saveStack({ ...stack, nodes: [...stack.nodes, node] }).then(setStack);
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
              <Button key={rt.addMethod} variant="light" fullWidth justify="start" mb={4} onClick={() => setSelectedRt(rt)}>
                <Text size="sm">{rt.label}</Text>
              </Button>
            ))}
          </div>
        ))}
      </ScrollArea>
      {selectedRt && (
        <AddResourceDialog
          rt={selectedRt}
          existingCount={stack.nodes.filter(n => n.addMethod === selectedRt.addMethod).length}
          onCreate={onCreate}
          onClose={() => setSelectedRt(null)}
        />
      )}
    </MStack>
  );
}
