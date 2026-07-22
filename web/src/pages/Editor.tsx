import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useParams, useNavigate } from "react-router-dom";
import { AppShell, Group, Title, Button, Menu, ActionIcon, Tooltip } from "@mantine/core";
import { IconArrowLeft, IconLayoutGrid, IconLayoutSidebar, IconCheck, IconDeviceFloppy, IconTrash, IconRestore, IconArrowBackUp, IconArrowForwardUp, IconExternalLink, IconWindowMaximize, IconBookmark } from "@tabler/icons-react";
import type { Stack, RunStatus } from "../model";
import type { CodeDiagnostic } from "../api";
import * as api from "../api";
import { useTitle } from "../useTitle";
import { DockLayout, EditorContext } from "../editor/DockLayout";
import type { DockLayoutHandle } from "../editor/DockLayout";
import { RunToolbar } from "../editor/RunToolbar";
import { ValidateBadge } from "../editor/ValidateBadge";
import { UserMenu } from "../auth/UserMenu";
import { promptText, toastOk, toastErr } from "../ui";

const HEADER_HEIGHT = 56;
const NOT_RUNNING: RunStatus = { state: "NotRunning", log: [] };

export function Editor() {
  const { id = "" } = useParams();
  const nav = useNavigate();
  const [stack, setStackState] = useState<Stack | null>(null);
  useTitle(stack?.name ?? "Editor");
  const [sel, setSel] = useState<string | null>(null);
  const [selIds, setSelIds] = useState<string[]>([]);
  const [flashSignal, setFlashSignal] = useState({ id: "", n: 0 });
  const [runStatus, setRunStatus] = useState<RunStatus>(NOT_RUNNING);
  const dockRef = useRef<DockLayoutHandle>(null);

  // Undo/redo: snapshot the stack before each edit. undo/redo restore + persist a snapshot.
  const undoRef = useRef<Stack[]>([]);
  const redoRef = useRef<Stack[]>([]);
  const [canUndo, setCanUndo] = useState(false);
  const [canRedo, setCanRedo] = useState(false);
  const sync = useCallback(() => { setCanUndo(undoRef.current.length > 0); setCanRedo(redoRef.current.length > 0); }, []);
  const setStack = useCallback((next: Stack) => {
    setStackState(prev => {
      if (prev) { undoRef.current.push(prev); if (undoRef.current.length > 50) undoRef.current.shift(); redoRef.current = []; }
      return next;
    });
    sync();
  }, [sync]);
  const undo = useCallback(() => {
    const prev = undoRef.current.pop();
    if (!prev) return;
    setStackState(cur => { if (cur) redoRef.current.push(cur); return prev; });
    api.saveStack(prev).catch(() => {}); sync();
  }, [sync]);
  const redo = useCallback(() => {
    const next = redoRef.current.pop();
    if (!next) return;
    setStackState(cur => { if (cur) undoRef.current.push(cur); return next; });
    api.saveStack(next).catch(() => {}); sync();
  }, [sync]);

  useEffect(() => {
    const h = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey)) return;
      const t = e.target as HTMLElement | null;
      if (t?.closest?.(".monaco-editor, input, textarea, [contenteditable=true]")) return; // let text fields undo their own
      if (e.key === "z" && !e.shiftKey) { e.preventDefault(); undo(); }
      else if (e.key === "y" || (e.key === "z" && e.shiftKey)) { e.preventDefault(); redo(); }
    };
    window.addEventListener("keydown", h);
    return () => window.removeEventListener("keydown", h);
  }, [undo, redo]);
  const [savedLayouts, setSavedLayouts] = useState<string[]>([]);
  const refreshLayouts = () => setSavedLayouts(dockRef.current?.listNamed() ?? []);
  const [panels, setPanels] = useState<{ id: string; title: string; open: boolean }[]>([]);
  const refreshPanels = () => setPanels(dockRef.current?.listPanels() ?? []);
  const saveLayout = () => {
    promptText("Save layout", "Layout name").then(name => {
      if (name) { dockRef.current?.saveNamed(name); refreshLayouts(); toastOk(`Layout "${name}" saved`); }
    });
  };
  const openIde = (ide: "vscode" | "rider" | "vs") =>
    api.openInIde(id, ide).then(r => r.ok ? toastOk("Opening in your IDE…") : toastErr(r.error, "Couldn't open")).catch(toastErr);
  const saveAsTemplate = () => promptText("Save as template", "Template name", stack?.name ?? "").then(name => {
    if (name) api.saveTemplate(id, name, "").then(() => toastOk(`Saved template "${name}"`)).catch(toastErr);
  });

  useEffect(() => { api.getStack(id).then(setStack); }, [id]);

  // Central validation: Roslyn diagnostics over the generated code, debounced on stack change.
  // Shared via context so the header badge and the Validation panel read the same result.
  const [diagnostics, setDiagnostics] = useState<CodeDiagnostic[]>([]);
  const stackSig = stack ? JSON.stringify(stack.nodes) + JSON.stringify(stack.edges) + JSON.stringify(stack.rawStatements) : "";
  useEffect(() => {
    if (!stack) return;
    let cancelled = false;
    const t = window.setTimeout(() => {
      api.validateStack(stack.id).then(d => { if (!cancelled) setDiagnostics(d); }).catch(() => { if (!cancelled) setDiagnostics([]); });
    }, 500);
    return () => { cancelled = true; window.clearTimeout(t); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [stackSig]);
  // One helper to jump to a panel: open/focus it and flash it (border glow) so the eye lands on it.
  const showPanel = (id: string) => { dockRef.current?.showPanel(id); setFlashSignal(s => ({ id, n: s.n + 1 })); };

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
    () => ({ stack: stack!, setStack, selected: sel, setSelected: setSel, selectedIds: selIds, setSelectedIds: setSelIds,
      runStatus, setRunStatus, diagnostics, showPanel, flashSignal }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [stack, sel, selIds, runStatus, diagnostics, flashSignal]);

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
              <ValidateBadge />
              <RunToolbar />
              <Tooltip label="Undo (Ctrl+Z)" withArrow>
                <ActionIcon variant="default" size="lg" disabled={!canUndo} onClick={undo}><IconArrowBackUp size={16} /></ActionIcon>
              </Tooltip>
              <Tooltip label="Redo (Ctrl+Shift+Z)" withArrow>
                <ActionIcon variant="default" size="lg" disabled={!canRedo} onClick={redo}><IconArrowForwardUp size={16} /></ActionIcon>
              </Tooltip>
              <Menu position="bottom-end" withArrow onOpen={refreshPanels} width={220}>
                <Menu.Target>
                  <Button variant="default" size="xs" leftSection={<IconLayoutSidebar size={14} />}>Panels</Button>
                </Menu.Target>
                <Menu.Dropdown>
                  <Menu.Label>Show / hide · ⤢ pops out to a window</Menu.Label>
                  {panels.map(p => (
                    <Menu.Item key={p.id}
                      leftSection={p.open ? <IconCheck size={14} /> : <span style={{ width: 14 }} />}
                      closeMenuOnClick={false}
                      rightSection={
                        <Tooltip label="Pop out to window" withArrow position="right">
                          <ActionIcon component="div" size="sm" variant="subtle"
                            onClick={e => { e.stopPropagation(); dockRef.current?.popoutPanel(p.id); refreshPanels(); }}>
                            <IconWindowMaximize size={13} />
                          </ActionIcon>
                        </Tooltip>
                      }
                      onClick={() => { dockRef.current?.togglePanel(p.id); refreshPanels(); }}>
                      {p.title}
                    </Menu.Item>
                  ))}
                </Menu.Dropdown>
              </Menu>
              <Menu position="bottom-end" withArrow onOpen={refreshLayouts} width={220}>
                <Menu.Target>
                  <Button variant="default" size="xs" leftSection={<IconLayoutGrid size={14} />}>Layout</Button>
                </Menu.Target>
                <Menu.Dropdown>
                  <Menu.Item leftSection={<IconBookmark size={14} />} onClick={saveAsTemplate}>Save stack as template…</Menu.Item>
                  <Menu.Divider />
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
              <Menu position="bottom-end" withArrow>
                <Menu.Target>
                  <Button variant="default" size="xs" leftSection={<IconExternalLink size={14} />}>Open in…</Button>
                </Menu.Target>
                <Menu.Dropdown>
                  <Menu.Label>Open project in</Menu.Label>
                  <Menu.Item onClick={() => openIde("vscode")}>VS Code</Menu.Item>
                  <Menu.Item onClick={() => openIde("rider")}>Rider</Menu.Item>
                  <Menu.Item onClick={() => openIde("vs")}>Visual Studio</Menu.Item>
                </Menu.Dropdown>
              </Menu>
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
