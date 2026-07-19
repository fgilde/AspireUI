import { useEffect, useState } from "react";
import { ScrollArea, Group, Text, CopyButton, Button } from "@mantine/core";
import { PrismLight as SyntaxHighlighter } from "react-syntax-highlighter";
import csharp from "react-syntax-highlighter/dist/esm/languages/prism/csharp";
import { oneDark } from "react-syntax-highlighter/dist/esm/styles/prism";
import * as api from "../api";

SyntaxHighlighter.registerLanguage("csharp", csharp);

export function CodePreview({ stackId, version }: { stackId: string; version: string }) {
  const [code, setCode] = useState("");
  useEffect(() => { api.previewStack(stackId).then(setCode); }, [stackId, version]);
  return (
    <div style={{ height: "100%", display: "flex", flexDirection: "column" }}>
      <Group justify="space-between" px="sm" py={4}>
        <Text size="xs" fw={600} c="dimmed">Program.cs</Text>
        <CopyButton value={code}>
          {({ copied, copy }) => <Button size="compact-xs" variant="subtle" onClick={copy}>{copied ? "Copied" : "Copy"}</Button>}
        </CopyButton>
      </Group>
      <ScrollArea style={{ flex: 1 }} px="sm">
        <SyntaxHighlighter language="csharp" style={oneDark}
          customStyle={{ margin: 0, background: "transparent", fontSize: 12 }} wrapLongLines>
          {code}
        </SyntaxHighlighter>
      </ScrollArea>
    </div>
  );
}
