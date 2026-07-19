import { Tabs, ScrollArea, Text } from "@mantine/core";
import type { Stack } from "../model";
import { CodePreview } from "./CodePreview";

export function PropertyPanel({ stack, nodeId }:
  { stack: Stack; nodeId: string | null; setStack: (s: Stack) => void }) {
  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%" }}>
      <Tabs defaultValue="props" style={{ flex: 1, minHeight: 0, display: "flex", flexDirection: "column" }}>
        <Tabs.List>
          <Tabs.Tab value="props">Properties</Tabs.Tab>
          <Tabs.Tab value="refs">References</Tabs.Tab>
        </Tabs.List>
        <ScrollArea style={{ flex: 1 }} p="sm">
          <Tabs.Panel value="props">{nodeId ? <Text size="sm">Properties for {nodeId}</Text> : <Text size="sm" c="dimmed">Select a node</Text>}</Tabs.Panel>
          <Tabs.Panel value="refs"><Text size="sm">References</Text></Tabs.Panel>
        </ScrollArea>
      </Tabs>
      <CodePreview stackId={stack.id} version={JSON.stringify(stack).length} />
    </div>
  );
}
