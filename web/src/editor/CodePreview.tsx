import { useEffect, useState } from "react";
import { ScrollArea, Code, Group, Text, CopyButton, Button } from "@mantine/core";
import * as api from "../api";

export function CodePreview({ stackId, version }: { stackId: string; version: number }) {
  const [code, setCode] = useState("");
  useEffect(() => { api.previewStack(stackId).then(setCode); }, [stackId, version]);
  return (
    <div style={{ borderTop: "1px solid var(--mantine-color-dark-4)", height: 260, display: "flex", flexDirection: "column" }}>
      <Group justify="space-between" px="sm" py={4}>
        <Text size="xs" fw={600} c="dimmed">Program.cs</Text>
        <CopyButton value={code}>
          {({ copied, copy }) => <Button size="compact-xs" variant="subtle" onClick={copy}>{copied ? "Copied" : "Copy"}</Button>}
        </CopyButton>
      </Group>
      <ScrollArea style={{ flex: 1 }} px="sm">
        <Code block style={{ whiteSpace: "pre", fontSize: 12 }}>{code}</Code>
      </ScrollArea>
    </div>
  );
}
