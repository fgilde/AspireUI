import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useParams, useNavigate } from "react-router-dom";
import { AppShell, Group, Title, Button, Menu, ActionIcon, Tooltip, Text, Badge, LoadingOverlay } from "@mantine/core";
import { IconArrowLeft, IconLayoutGrid, IconLayoutSidebar, IconCheck, IconDeviceFloppy, IconTrash, IconRestore, IconArrowBackUp, IconArrowForwardUp, IconExternalLink, IconWindowMaximize, IconBookmark, IconServer, IconRocket, IconPuzzle, IconFolderDown, IconDownload, IconLock, IconLockOpen, IconAlertTriangle } from "@tabler/icons-react";
import { Modal, Stack as MStack, Alert } from "@mantine/core";
import { InstallAppModal } from "../hosting/InstallAppModal";
import JSZip from "jszip";
import type { Stack, RunStatus } from "../model";
import type { CodeDiagnostic } from "../api";
import * as api from "../api";
import { useTitle } from "../useTitle";
import { DockLayout, EditorContext } from "../editor/DockLayout";
import type { DockLayoutHandle } from "../editor/DockLayout";
import { RunToolbar } from "../editor/RunToolbar";
import { ValidateBadge } from "../editor/ValidateBadge";
import { UserMenu } from "../auth/UserMenu";
import { promptText, toastOk, toastErr, confirmDelete } from "../ui";

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
      if (t?.closest?.(".monaco-editor, input, textarea, [contenteditable=true]")) return;
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
  const saveAsSnippet = () => promptText("Save stack as snippet", "Snippet name", stack?.name ?? "").then(name => {
    if (!name || !stack) return;
    api.saveSnippet({ id: "", name, group: "Custom", icon: stack.nodes[0]?.icon ?? stack.nodes[0]?.addMethod ?? null,
      nodes: stack.nodes, edges: stack.edges, files: stack.extraFiles ?? [] })
      .then(() => { toastOk(`Saved snippet "${name}"`); window.dispatchEvent(new Event("aspireui:snippets-changed")); }).catch(toastErr);
  });
  const exportProject = async () => {
    if (!stack) return;
    try {
      const blob = await api.exportStackZip(stack.id);
      const pick = (window as unknown as { showDirectoryPicker?: (o?: unknown) => Promise<any> }).showDirectoryPicker;
      if (pick) {
        const root = await pick({ mode: "readwrite" });
        const zip = await JSZip.loadAsync(blob);
        for (const [path, entry] of Object.entries(zip.files)) {
          if ((entry as { dir: boolean }).dir) continue;
          const parts = path.split("/").filter(Boolean);
          const fname = parts.pop();
          if (!fname) continue;
          let dir = root;
          for (const p of parts) dir = await dir.getDirectoryHandle(p, { create: true });
          const fh = await dir.getFileHandle(fname, { create: true });
          const w = await fh.createWritable(); await w.write(await (entry as any).async("blob")); await w.close();
        }
        toastOk("Project written to folder");
      } else {
        const url = URL.createObjectURL(blob);
        const a = document.createElement("a"); a.href = url; a.download = `${stack.name || "stack"}.zip`; a.click();
        URL.revokeObjectURL(url); toastOk("Project downloaded");
      }
    } catch (e) { if ((e as { name?: string })?.name !== "AbortError") toastErr(e, "Export failed"); }
  };

  useEffect(() => { api.getStack(id).then(setStack); }, [id]);

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
  const showPanel = (id: string) => { dockRef.current?.showPanel(id); setFlashSignal(s => ({ id, n: s.n + 1 })); };

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

  const locked = stack?.deployment?.state === "running" || stack?.deployment?.state === "deploying";
  const runAsIs = !!stack?.runAsIs;
  const [installOpen, setInstallOpen] = useState(false);
  const [editWarn, setEditWarn] = useState(false);
  const unlockForEdit = async () => {
    if (!stack) return;
    const saved = await api.saveStack({ ...stack, runAsIs: false, appHostProject: null });
    setStackState(saved);
    setEditWarn(false);
    toastOk("Editing unlocked — the code will regenerate from the graph");
  };
  const [hostingBusy, setHostingBusy] = useState(false);
  const deployToHosting = useCallback(async () => {
    if (!stack) return;
    setHostingBusy(true);
    try { await api.hostingDeploy(stack.id); setStackState(await api.getStack(stack.id)); toastOk("Deployed to hosting"); }
    catch (e) { toastErr(e); }
    finally { setHostingBusy(false); }
  }, [stack?.id]);
  const stopHosting = useCallback(async () => {
    if (!stack) return;
    setHostingBusy(true);
    try { await api.stopHosting(stack.id); setStackState(await api.getStack(stack.id)); }
    catch (e) { toastErr(e); }
    finally { setHostingBusy(false); }
  }, [stack?.id]);
  const guardedSetStack = useCallback((next: Stack) => {
    if (stack?.deployment?.state === "running" || stack?.deployment?.state === "deploying") {
      toastErr("Stop the hosting deployment to edit this stack."); return;
    }
    setStack(next);
  }, [stack, setStack]);

  const ctx = useMemo(
    () => ({ stack: stack!, setStack: guardedSetStack, selected: sel, setSelected: setSel, selectedIds: selIds, setSelectedIds: setSelIds,
      runStatus, setRunStatus, diagnostics, showPanel, flashSignal, deployToHosting, stopHosting, hostingBusy }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [stack, sel, selIds, runStatus, diagnostics, flashSignal, locked, hostingBusy, deployToHosting, stopHosting]);

  if (!stack) return null;

  return (
    <EditorContext.Provider value={ctx}>
      <AppShell header={{ height: HEADER_HEIGHT }} padding={0}>
        <AppShell.Header>
          <Group h="100%" px="md" justify="space-between">
            <Group>
              <Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={() => nav("/")}>Stacks</Button>
              <Title order={4}>{stack.name}</Title>
              {locked
                ? <Badge color="green" variant="light" leftSection={<IconServer size={12} />} style={{ cursor: "pointer" }} onClick={() => showPanel("publish")}>Hosting</Badge>
                : <Tooltip label="Deploy this stack to hosting" withArrow>
                    <Button size="xs" variant="light" color="teal" leftSection={<IconRocket size={14} />} onClick={() => showPanel("publish")}>Deploy</Button>
                  </Tooltip>}
              <Tooltip label="Install an app from the store" withArrow>
                <Button size="xs" variant="default" leftSection={<IconDownload size={14} />} onClick={() => setInstallOpen(true)}>Install from Store</Button>
              </Tooltip>
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
              <Menu position="bottom-end" withArrow width={240}>
                <Menu.Target>
                  <Button variant="default" size="xs" leftSection={<IconDeviceFloppy size={14} />}>Save</Button>
                </Menu.Target>
                <Menu.Dropdown>
                  <Menu.Item leftSection={<IconBookmark size={14} />} onClick={saveAsTemplate}>As template…</Menu.Item>
                  <Menu.Item leftSection={<IconPuzzle size={14} />} onClick={saveAsSnippet}>As snippet…</Menu.Item>
                  <Menu.Item leftSection={<IconFolderDown size={14} />} onClick={exportProject}>As project (download / folder)…</Menu.Item>
                </Menu.Dropdown>
              </Menu>
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
        <AppShell.Main style={{ height: `calc(100vh - ${HEADER_HEIGHT}px)`, display: "flex", flexDirection: "column" }}>
          {locked && (
            <Group gap="sm" px="md" py={6} style={{ background: "var(--mantine-color-orange-light)", flexShrink: 0 }}>
              <Text size="sm">Running in hosting — this stack is read-only. Stop it to edit.</Text>
              <Button size="compact-xs" color="orange"
                onClick={() => confirmDelete("this deployment", "Stop it so you can edit? It stays deployed (stopped).").then(okd => { if (okd) stopHosting(); })}>
                Stop &amp; edit
              </Button>
            </Group>
          )}
          {runAsIs && (
            <Group gap="sm" px="md" py={6} style={{ background: "var(--mantine-color-violet-light)", flexShrink: 0 }}>
              <IconLock size={15} />
              <Text size="sm">Imported project — runs <b>as-is</b>. The visual editor is locked so your original code isn't regenerated.</Text>
              <Button size="compact-xs" color="violet" variant="light" leftSection={<IconLockOpen size={13} />} onClick={() => setEditWarn(true)}>Edit anyway</Button>
            </Group>
          )}
          <div style={{ flex: 1, minHeight: 0, position: "relative" }}>
            <LoadingOverlay visible={hostingBusy} zIndex={400} overlayProps={{ blur: 2 }}
              loaderProps={{ children: <Text fw={600}>Deploying to hosting…</Text> }} />
            {runAsIs && (
              <div onClick={() => setEditWarn(true)}
                style={{ position: "absolute", inset: 0, zIndex: 350, cursor: "pointer",
                  background: "light-dark(rgba(255,255,255,.35), rgba(0,0,0,.45))", backdropFilter: "blur(1px)",
                  display: "grid", placeItems: "center" }}>
                <MStack gap={6} align="center" style={{ background: "var(--mantine-color-body)", border: "1px solid var(--mantine-color-default-border)", borderRadius: 12, padding: "18px 24px", boxShadow: "var(--mantine-shadow-md)" }}>
                  <IconLock size={26} />
                  <Text fw={600}>Editing locked</Text>
                  <Text size="xs" c="dimmed" ta="center" maw={320}>This is an imported project running as-is. Click to edit — it will be regenerated from the visual graph.</Text>
                </MStack>
              </div>
            )}
            <DockLayout ref={dockRef} />
          </div>
        </AppShell.Main>
      </AppShell>
      {installOpen && <InstallAppModal onClose={() => setInstallOpen(false)} onInstalled={() => setInstallOpen(false)} />}
      <Modal opened={editWarn} onClose={() => setEditWarn(false)} title={<Group gap={8}><IconAlertTriangle size={18} color="var(--mantine-color-orange-6)" /><Text fw={600}>Edit this imported project?</Text></Group>} centered>
        <MStack gap="md">
          <Alert color="orange" icon={<IconAlertTriangle size={16} />}>
            AspireUI regenerates the AppHost code from the visual graph. Custom C# — loops, conditionals, helper methods, your own extensions — that couldn't be parsed into nodes will be <b>lost</b> once you edit and save.
          </Alert>
          <Text size="sm" c="dimmed">You can keep running it as-is instead. Only unlock if the graph fully represents your project.</Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setEditWarn(false)}>Keep as-is</Button>
            <Button color="orange" leftSection={<IconLockOpen size={16} />} onClick={unlockForEdit}>Unlock &amp; edit</Button>
          </Group>
        </MStack>
      </Modal>
    </EditorContext.Provider>
  );
}
