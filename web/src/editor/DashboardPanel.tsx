import { useState } from "react";
import { Button, Center, Stack as MStack, Text, Loader, Group, ActionIcon, Tooltip, Switch, Alert } from "@mantine/core";
import { IconPlayerPlay, IconExternalLink, IconRefresh, IconInfoCircle } from "@tabler/icons-react";
import { useEditor } from "./DockLayout";
import { toastErr } from "../ui";
import * as api from "../api";

// Live dashboard access. The Aspire dashboard is a Blazor app that blocks cross-origin framing and is
// fragile when reverse-proxied under a subpath, so the reliable default is "open in a new tab".
// An experimental embedded view (sandboxed so it can't navigate the app away) is available behind a toggle.
export function DashboardPanel() {
  const { stack, runStatus, setRunStatus } = useEditor();
  const [nonce, setNonce] = useState(0);
  const [embed, setEmbed] = useState(false);
  const url = runStatus.dashboardUrl;
  const running = runStatus.state === "Running" && !!url;
  const proxied = (() => {
    if (!url) return "";
    try { const u = new URL(url); return `/dash/${stack.id}${u.pathname}${u.search}`; } catch { return url; }
  })();

  const start = () => api.runStack(stack.id).then(setRunStatus).catch(e => toastErr(e, "Could not start"));

  if (running) {
    return (
      <div style={{ height: "100%", display: "flex", flexDirection: "column" }}>
        <Group justify="space-between" px="sm" py={6} wrap="nowrap">
          <Group gap="xs" wrap="nowrap">
            <Button size="xs" leftSection={<IconExternalLink size={14} />} component="a" href={url!} target="_blank">
              Open dashboard
            </Button>
            <Switch size="xs" label="Embed (experimental)" checked={embed} onChange={e => setEmbed(e.currentTarget.checked)} />
          </Group>
          {embed && (
            <Tooltip label="Reload" withArrow><ActionIcon size="sm" variant="subtle" onClick={() => setNonce(n => n + 1)}><IconRefresh size={14} /></ActionIcon></Tooltip>
          )}
        </Group>
        {embed ? (
          <iframe key={nonce} src={proxied} title="Aspire Dashboard"
            sandbox="allow-scripts allow-same-origin allow-forms allow-popups"
            style={{ flex: 1, width: "100%", border: 0 }} />
        ) : (
          <Center style={{ flex: 1 }}>
            <MStack align="center" gap="sm" maw={380} px="md">
              <Text fw={600}>Dashboard is running</Text>
              <Text size="sm" c="dimmed" ta="center">
                Opens best in its own tab — the Aspire dashboard (Blazor + live SignalR) blocks embedding for
                security and misbehaves inside an iframe. Use the button above, or try the experimental embed.
              </Text>
              <Button leftSection={<IconExternalLink size={16} />} component="a" href={url!} target="_blank">Open Aspire dashboard</Button>
            </MStack>
          </Center>
        )}
        {embed && (
          <Alert color="yellow" radius={0} icon={<IconInfoCircle size={14} />} py={4}>
            <Text size="xs">Experimental — SignalR/navigation may glitch. The dashboard can't escape this panel. Prefer “Open dashboard”.</Text>
          </Alert>
        )}
      </div>
    );
  }

  return (
    <Center h="100%">
      <MStack align="center" gap="sm" maw={340} px="md">
        {runStatus.state === "Starting" ? (
          <><Loader /><Text size="sm" c="dimmed">Starting… the dashboard link appears here once it's ready.</Text></>
        ) : (
          <>
            <Text fw={600}>Dashboard</Text>
            <Text size="sm" c="dimmed" ta="center">
              The Aspire dashboard shows live logs, traces, metrics and per-resource status. Start the stack to open it.
            </Text>
            <Button leftSection={<IconPlayerPlay size={16} />} color="green" onClick={start}>Start stack</Button>
            {runStatus.state === "Failed" && <Text size="xs" c="red">Last run failed — check the Logs panel.</Text>}
          </>
        )}
      </MStack>
    </Center>
  );
}
