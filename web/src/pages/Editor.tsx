import { useEffect, useState } from "react";
import { useParams, useNavigate } from "react-router-dom";
import { AppShell, Group, Title, Button } from "@mantine/core";
import { IconArrowLeft } from "@tabler/icons-react";
import type { Stack } from "../model";
import * as api from "../api";
import { Palette } from "../editor/Palette";
import { Canvas } from "../editor/Canvas";
import { PropertyPanel } from "../editor/PropertyPanel";
import { RunToolbar } from "../editor/RunToolbar";

export function Editor() {
  const { id = "" } = useParams();
  const nav = useNavigate();
  const [stack, setStack] = useState<Stack | null>(null);
  const [sel, setSel] = useState<string | null>(null);

  useEffect(() => { api.getStack(id).then(setStack); }, [id]);
  if (!stack) return null;

  return (
    <AppShell header={{ height: 56 }} navbar={{ width: 240, breakpoint: 0 }} aside={{ width: 380, breakpoint: 0 }} padding={0}>
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Group>
            <Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={() => nav("/")}>Stacks</Button>
            <Title order={4}>{stack.name}</Title>
          </Group>
          <RunToolbar stack={stack} />
        </Group>
      </AppShell.Header>
      <AppShell.Navbar><Palette stack={stack} setStack={setStack} /></AppShell.Navbar>
      <AppShell.Main style={{ height: "calc(100vh - 56px)" }}>
        <Canvas stack={stack} setStack={setStack} onSelect={setSel} />
      </AppShell.Main>
      <AppShell.Aside><PropertyPanel stack={stack} nodeId={sel} setStack={setStack} onDeleted={() => setSel(null)} /></AppShell.Aside>
    </AppShell>
  );
}
