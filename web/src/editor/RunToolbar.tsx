import { Button, Group, Badge, Anchor, Tooltip } from "@mantine/core";
import { IconPlayerPlay, IconPlayerStop, IconExternalLink, IconDownload } from "@tabler/icons-react";
import * as api from "../api";
import { useEditor } from "./DockLayout";
import { confirmAction, toastErr } from "../ui";

export function RunToolbar() {
  const { stack, runStatus: st, setRunStatus } = useEditor();
  const color = { NotRunning: "gray", Starting: "yellow", Running: "green", Failed: "red" }[st.state];

  // AddGithubRepository resources make Aspire `git clone` at run time; without git the run dies with a
  // raw unhandled exception. Pre-check and let the user bail instead of hitting the confusing crash.
  const start = async () => {
    if (stack.nodes.some(n => n.addMethod === "AddGithubRepository")) {
      const health = await api.envHealth().catch(() => null);
      if (health && !health.git.ok &&
          !(await confirmAction("git not found",
            "This stack has a GitHub-repository resource, but git was not found on the host. Aspire will fail to clone it at run time. Run anyway?", "Run anyway")))
        return;
    }
    try { setRunStatus(await api.runStack(stack.id)); } catch (e) { toastErr(e, "Could not start"); }
  };

  return (
    <Group gap="xs">
      <Badge color={color} variant="light">{st.state}</Badge>
      {st.state === "Running" && st.dashboardUrl &&
        <Anchor href={st.dashboardUrl} target="_blank">
          <Button size="xs" variant="light" leftSection={<IconExternalLink size={14} />}>Dashboard</Button>
        </Anchor>}
      {st.state === "Running" || st.state === "Starting"
        ? <Tooltip label="Stop the running stack" withArrow>
            <Button size="xs" color="red" leftSection={<IconPlayerStop size={14} />} onClick={() => api.stopStack(stack.id).then(setRunStatus)}>Stop</Button>
          </Tooltip>
        : <Tooltip label="Run this stack (needs Docker)" withArrow>
            <Button size="xs" color="green" leftSection={<IconPlayerPlay size={14} />} onClick={() => void start()}>Run</Button>
          </Tooltip>}
      <Button size="xs" variant="default" leftSection={<IconDownload size={14} />}
        onClick={() => { window.location.href = `/api/stacks/${stack.id}/export`; }}>Export</Button>
    </Group>
  );
}
