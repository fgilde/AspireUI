import { Button, Center, Stack as MStack, Text, Loader, Group, ActionIcon, Tooltip, Anchor } from "@mantine/core";
import { IconPlayerPlay, IconExternalLink, IconRefresh } from "@tabler/icons-react";
import { useState } from "react";
import { useEditor } from "./DockLayout";
import { toastErr } from "../ui";
import * as api from "../api";

// Embeds the live Aspire dashboard once the stack is running; a Start button otherwise.
// (The header Run/Stop/Dashboard controls stay — this is an in-tool view of the same thing.)
export function DashboardPanel() {
  const { stack, runStatus, setRunStatus } = useEditor();
  const [nonce, setNonce] = useState(0); // force iframe reload
  const url = runStatus.dashboardUrl;
  const running = runStatus.state === "Running" && !!url;

  const start = () => api.runStack(stack.id).then(setRunStatus).catch(e => toastErr(e, "Could not start"));

  if (running) {
    return (
      <div style={{ height: "100%", display: "flex", flexDirection: "column" }}>
        <Group justify="space-between" px="sm" py={4} wrap="nowrap">
          <Text size="xs" c="dimmed" truncate>Aspire dashboard — live</Text>
          <Group gap={4} wrap="nowrap">
            <Tooltip label="Reload" withArrow><ActionIcon size="sm" variant="subtle" onClick={() => setNonce(n => n + 1)}><IconRefresh size={14} /></ActionIcon></Tooltip>
            <Tooltip label="Open in new tab" withArrow><ActionIcon size="sm" variant="subtle" component="a" href={url!} target="_blank"><IconExternalLink size={14} /></ActionIcon></Tooltip>
          </Group>
        </Group>
        <iframe key={nonce} src={url!} title="Aspire Dashboard" style={{ flex: 1, width: "100%", border: 0 }} />
      </div>
    );
  }

  return (
    <Center h="100%">
      <MStack align="center" gap="sm" maw={340} px="md">
        {runStatus.state === "Starting" ? (
          <>
            <Loader />
            <Text size="sm" c="dimmed">Starting… the dashboard appears here once it's ready.</Text>
          </>
        ) : (
          <>
            <Text fw={600}>Dashboard</Text>
            <Text size="sm" c="dimmed" ta="center">
              The Aspire dashboard shows live logs, traces, metrics and per-resource status. Start the stack to view it here.
            </Text>
            <Button leftSection={<IconPlayerPlay size={16} />} color="green" onClick={start}>Start stack</Button>
            {runStatus.state === "Failed" && <Text size="xs" c="red">Last run failed — check the Logs panel.</Text>}
          </>
        )}
        {url && !running && <Anchor size="xs" href={url} target="_blank">Open dashboard in a new tab</Anchor>}
      </MStack>
    </Center>
  );
}
