import { useEffect, useMemo, useRef, useState } from "react";
import { useParams, useNavigate } from "react-router-dom";
import { AppShell, Group, Title, Button, Menu, ActionIcon } from "@mantine/core";
import { IconArrowLeft, IconLayoutGrid, IconDeviceFloppy, IconTrash, IconRestore } from "@tabler/icons-react";
import type { Stack, RunStatus } from "../model";
import * as api from "../api";
import { DockLayout, EditorContext } from "../editor/DockLayout";
import type { DockLayoutHandle } from "../editor/DockLayout";
import { RunToolbar } from "../editor/RunToolbar";
import { HelpButton } from "../HelpButton";
import { UserMenu } from "../auth/UserMenu";
import { ThemeMenu } from "../ThemeMenu";
import { GitHubLink } from "../GitHubLink";

const HEADER_HEIGHT = 56;
const NOT_RUNNING: RunStatus = { state: "NotRunning", log: [] };

export function Editor() {
  const { id = "" } = useParams();
  const nav = useNavigate();
  const [stack, setStack] = useState<Stack | null>(null);
  const [sel, setSel] = useState<string | null>(null);
  const [runStatus, setRunStatus] = useState<RunStatus>(NOT_RUNNING);
  const dockRef = useRef<DockLayoutHandle>(null);
  const [savedLayouts, setSavedLayouts] = useState<string[]>([]);
  const refreshLayouts = () => setSavedLayouts(dockRef.current?.listNamed() ?? []);
  const saveLayout = () => {
    const name = window.prompt("Save current layout as:")?.trim();
    if (name) { dockRef.current?.saveNamed(name); refreshLayouts(); }
  };

  useEffect(() => { api.getStack(id).then(setStack); }, [id]);

  // Single shared poller for run status: 2s while a run is starting/active,
  // 5s otherwise. RunToolbar, LogsPanel and any other consumer read the
  // result from EditorContext instead of polling on their own.
  useEffect(() => {
    if (!stack) return;
    let cancelled = false;
    let timer: number | undefined;
    const poll = () => {
      api.statusStack(stack.id).then((s: RunStatus) => {
        if (cancelled) return;
        setRunStatus(s);
        timer = window.setTimeout(poll, s.state === "Starting" || s.state === "Running" ? 2000 : 5000);
      }).catch(() => {
        if (!cancelled) timer = window.setTimeout(poll, 5000);
      });
    };
    poll();
    return () => { cancelled = true; window.clearTimeout(timer); };
  }, [stack?.id]);

  // Memoize the context value so the 2s status poller doesn't re-render every
  // dock panel each tick — only when the data a panel actually reads changes.
  const ctx = useMemo(
    () => ({ stack: stack!, setStack, selected: sel, setSelected: setSel, runStatus, setRunStatus }),
    [stack, sel, runStatus]);

  if (!stack) return null;

  return (
    <EditorContext.Provider value={ctx}>
      <AppShell header={{ height: HEADER_HEIGHT }} padding={0}>
        <AppShell.Header>
          <Group h="100%" px="md" justify="space-between">
            <Group>
              <Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={() => nav("/")}>Stacks</Button>
              <Title order={4}>{stack.name}</Title>
            </Group>
            <Group>
              <RunToolbar />
              <Menu position="bottom-end" withArrow onOpen={refreshLayouts} width={220}>
                <Menu.Target>
                  <Button variant="default" size="xs" leftSection={<IconLayoutGrid size={14} />}>Layout</Button>
                </Menu.Target>
                <Menu.Dropdown>
                  <Menu.Item leftSection={<IconDeviceFloppy size={14} />} onClick={saveLayout}>Save current layout…</Menu.Item>
                  <Menu.Item leftSection={<IconRestore size={14} />} onClick={() => dockRef.current?.resetLayout()}>Reset to default</Menu.Item>
                  {savedLayouts.length > 0 && <Menu.Label>Saved layouts</Menu.Label>}
                  {savedLayouts.map(name => (
                    <Menu.Item key={name} onClick={() => dockRef.current?.loadNamed(name)}
                      rightSection={
                        <ActionIcon component="div" size="sm" variant="subtle" color="red"
                          onClick={(e) => { e.stopPropagation(); dockRef.current?.deleteNamed(name); refreshLayouts(); }}>
                          <IconTrash size={13} />
                        </ActionIcon>
                      }>{name}</Menu.Item>
                  ))}
                </Menu.Dropdown>
              </Menu>
              <HelpButton />
              <GitHubLink />
              <ThemeMenu />
              <UserMenu />
            </Group>
          </Group>
        </AppShell.Header>
        <AppShell.Main style={{ height: `calc(100vh - ${HEADER_HEIGHT}px)` }}>
          <DockLayout ref={dockRef} />
        </AppShell.Main>
      </AppShell>
    </EditorContext.Provider>
  );
}
