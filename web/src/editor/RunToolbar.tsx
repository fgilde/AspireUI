import { useEffect, useRef, useState } from "react";
import { Button, Group, Badge, Anchor } from "@mantine/core";
import { IconPlayerPlay, IconPlayerStop, IconExternalLink, IconDownload } from "@tabler/icons-react";
import type { Stack, RunStatus } from "../model";
import * as api from "../api";

export function RunToolbar({ stack }: { stack: Stack }) {
  const [st, setSt] = useState<RunStatus>({ state: "NotRunning", log: [] });
  const timer = useRef<number | undefined>(undefined);

  const poll = () => api.statusStack(stack.id).then(setSt).catch(() => {});
  useEffect(() => {
    poll();
    timer.current = window.setInterval(poll, 2000);
    return () => clearInterval(timer.current);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [stack.id]);

  const color = { NotRunning: "gray", Starting: "yellow", Running: "green", Failed: "red" }[st.state];

  return (
    <Group gap="xs">
      <Badge color={color} variant="light">{st.state}</Badge>
      {st.state === "Running" && st.dashboardUrl &&
        <Anchor href={st.dashboardUrl} target="_blank">
          <Button size="xs" variant="light" leftSection={<IconExternalLink size={14} />}>Dashboard</Button>
        </Anchor>}
      {st.state === "Running" || st.state === "Starting"
        ? <Button size="xs" color="red" leftSection={<IconPlayerStop size={14} />} onClick={() => api.stopStack(stack.id).then(setSt)}>Stop</Button>
        : <Button size="xs" color="green" leftSection={<IconPlayerPlay size={14} />} onClick={() => api.runStack(stack.id).then(setSt)}>Run</Button>}
      <Button size="xs" variant="default" leftSection={<IconDownload size={14} />}
        onClick={() => { window.location.href = `/stacks/${stack.id}/export`; }}>Export</Button>
    </Group>
  );
}
