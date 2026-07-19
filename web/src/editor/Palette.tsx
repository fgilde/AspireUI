import { useEffect, useMemo, useState } from "react";
import { Stack as MStack, TextInput, Text, Button, ScrollArea, Divider } from "@mantine/core";
import type { Stack, ResourceType } from "../model";
import * as api from "../api";

export function Palette({ stack, setStack }: { stack: Stack; setStack: (s: Stack) => void }) {
  const [cat, setCat] = useState<ResourceType[]>([]);
  const [q, setQ] = useState("");
  useEffect(() => { api.getCatalog().then(setCat); }, []);

  const groups = useMemo(() => {
    const f = cat.filter(r => r.label.toLowerCase().includes(q.toLowerCase()));
    const by: Record<string, ResourceType[]> = {};
    for (const r of f) (by[r.group || "Other"] ??= []).push(r);
    return by;
  }, [cat, q]);

  const add = (rt: ResourceType) => {
    const suffix = stack.nodes.filter(n => n.addMethod === rt.addMethod).length || "";
    const varName = rt.addMethod.replace(/^Add/, "").toLowerCase() + suffix;
    const node = {
      id: "n" + crypto.randomUUID().slice(0, 8),
      varName, addMethod: rt.addMethod, resourceName: varName,
      withCalls: [], addArgs: (rt.addOverloads[0]?.params ?? []).map(() => '""'),
      x: 60 + stack.nodes.length * 24, y: 60 + stack.nodes.length * 24,
    };
    api.saveStack({ ...stack, nodes: [...stack.nodes, node] }).then(setStack);
  };

  return (
    <MStack gap="xs" p="sm" h="100%">
      <TextInput placeholder="Search…" value={q} onChange={e => setQ(e.currentTarget.value)} />
      <ScrollArea style={{ flex: 1 }}>
        {Object.entries(groups).map(([g, items]) => (
          <div key={g}>
            <Divider my="xs" label={g} labelPosition="left" />
            {items.map(rt => (
              <Button key={rt.addMethod} variant="light" fullWidth justify="start" mb={4} onClick={() => add(rt)}>
                <Text size="sm">{rt.label}</Text>
              </Button>
            ))}
          </div>
        ))}
      </ScrollArea>
    </MStack>
  );
}
