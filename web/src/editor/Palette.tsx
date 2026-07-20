import { useEffect, useMemo, useState } from "react";
import { Stack as MStack, TextInput, Text, Button, ScrollArea, Divider, Tooltip, ThemeIcon } from "@mantine/core";
import type { Stack, ResourceType, Node } from "../model";
import { resourceVisual } from "../resourceIcons";
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
            {items.map(rt => {
              const { Icon, color } = resourceVisual(rt.addMethod);
              return (
                <Tooltip key={rt.addMethod} label="Click to add to the canvas" position="right" withArrow openDelay={400}>
                  <Button variant="light" fullWidth justify="start" mb={4} onClick={() => setSelectedRt(rt)}
                    leftSection={<ThemeIcon variant="transparent" size={20} style={{ color }}><Icon size={16} /></ThemeIcon>}>
                    <Text size="sm">{rt.label}</Text>
                  </Button>
                </Tooltip>
              );
            })}
          </div>
        ))}
      </ScrollArea>
      {selectedRt && (
        <AddResourceDialog
          rt={selectedRt}
          existingCount={stack.nodes.filter(n => n.addMethod === selectedRt.addMethod).length}
          totalCount={stack.nodes.length}
          onCreate={onCreate}
          onClose={() => setSelectedRt(null)}
        />
      )}
    </MStack>
  );
}
