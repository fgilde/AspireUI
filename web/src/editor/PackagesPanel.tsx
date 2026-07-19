import { useEffect, useState } from "react";
import { ScrollArea, Group, Text, Badge, Divider } from "@mantine/core";
import type { Stack } from "../model";
import type { PackageInfo } from "../api";
import * as api from "../api";

export function PackagesPanel({ stack }: { stack: Stack }) {
  const [packages, setPackages] = useState<PackageInfo[]>([]);
  // `stack` is a fresh object on every content change (immutable update pattern
  // used throughout this app), so its identity alone signals when to refetch.
  useEffect(() => { api.getPackages(stack.id).then(setPackages).catch(() => setPackages([])); }, [stack]);

  return (
    <ScrollArea style={{ height: "100%" }} px="sm" py="xs">
      {packages.map(p => (
        <Group key={p.id} justify="space-between" py={6} wrap="nowrap"
          style={{ borderBottom: "1px solid var(--mantine-color-default-border)" }}>
          <Group gap="xs" wrap="nowrap">
            <Text ff="monospace" size="sm">{p.id}</Text>
            <Badge variant="light">{p.version}</Badge>
          </Group>
          <Group gap={4}>
            {p.resources.map(r => <Badge key={r} color="gray" variant="outline">{r}</Badge>)}
          </Group>
        </Group>
      ))}
      {stack.extraFiles.length > 0 && (
        <>
          <Divider label="Custom files" labelPosition="left" mt="sm" mb={4} />
          {stack.extraFiles.map(f => (
            <Text key={f.name} ff="monospace" size="sm" py={4} c="dimmed">{f.name}</Text>
          ))}
        </>
      )}
    </ScrollArea>
  );
}
