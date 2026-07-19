import { useEffect, useRef, useState } from "react";
import { useParams, useNavigate } from "react-router-dom";
import { AppShell, Group, Title, Button } from "@mantine/core";
import { IconArrowLeft, IconLayoutGrid } from "@tabler/icons-react";
import type { Stack } from "../model";
import * as api from "../api";
import { DockLayout } from "../editor/DockLayout";
import type { DockLayoutHandle } from "../editor/DockLayout";
import { RunToolbar } from "../editor/RunToolbar";

const HEADER_HEIGHT = 56;

export function Editor() {
  const { id = "" } = useParams();
  const nav = useNavigate();
  const [stack, setStack] = useState<Stack | null>(null);
  const [sel, setSel] = useState<string | null>(null);
  const dockRef = useRef<DockLayoutHandle>(null);

  useEffect(() => { api.getStack(id).then(setStack); }, [id]);
  if (!stack) return null;

  return (
    <AppShell header={{ height: HEADER_HEIGHT }} padding={0}>
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Group>
            <Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={() => nav("/")}>Stacks</Button>
            <Title order={4}>{stack.name}</Title>
          </Group>
          <Group>
            <RunToolbar stack={stack} />
            <Button variant="default" size="xs" leftSection={<IconLayoutGrid size={14} />}
              onClick={() => dockRef.current?.resetLayout()}>Reset Layout</Button>
          </Group>
        </Group>
      </AppShell.Header>
      <AppShell.Main style={{ height: `calc(100vh - ${HEADER_HEIGHT}px)` }}>
        <DockLayout ref={dockRef} stack={stack} setStack={setStack} selected={sel} setSelected={setSel} />
      </AppShell.Main>
    </AppShell>
  );
}
