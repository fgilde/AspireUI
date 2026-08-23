import { useEffect, useRef, useState } from "react";
import type { ChangeEvent } from "react";
import { useNavigate } from "react-router-dom";
import JSZip from "jszip";
import ignore from "ignore";
import {
  Group, Title, Text, Button, SimpleGrid, Card, ActionIcon, Divider, Anchor,
  Modal, TextInput, Badge, Center, Loader, Stack as MStack, ThemeIcon, Menu, Tooltip, Select,
} from "@mantine/core";
import {
  IconPlus, IconTrash, IconLayoutGrid, IconChevronDown, IconSparkles,
  IconUpload, IconFileZip, IconFolder, IconDots, IconCopy, IconPencil, IconSearch, IconServer,
  IconPlayerPlay, IconPlayerStop, IconExternalLink, IconBookmark, IconUser, IconDownload, IconLayoutDashboard, IconBrandGithub, IconWorld,
} from "@tabler/icons-react";
import { runStateColor, canOpenEditor, hostingBroken, hostingHealthLabel, type Stack, type RunStatus, type Deployment } from "../model";
import { ResourceGlyph } from "../resourceIcons";
import * as api from "../api";
import { useTitle } from "../useTitle";
import type { TemplateInfo } from "../api";
import { PageShell } from "../components/PageShell";
import { useAuth } from "../auth/AuthContext";
import { HostingMenuItems, ConfigureModal, LogsModal, BackupsModal, DomainModal, TerminalModal, VolumesModal, hostingColor, deploymentColor } from "../hosting/HostingActions";
import { stackContainers } from "./Hosting";
import { InstallAppModal } from "../hosting/InstallAppModal";
import { MoveAppModal, targetIcon } from "../hosting/TargetsPanel";
import { GitImportModal } from "../hosting/GitImportModal";
import { confirmDelete, toastOk, toastErr, promptText } from "../ui";
import "./StacksOverview.css";

type Src = { path: string; content: string };
const SKIP_DIRS = new Set([".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea", "TestResults", "packages"]);
const skipPath = (p: string) => p.split("/").some(s => SKIP_DIRS.has(s));
const bufToB64 = (buf: ArrayBuffer) => {
  let bin = ""; const b = new Uint8Array(buf);
  for (let i = 0; i < b.length; i += 0x8000) bin += String.fromCharCode(...b.subarray(i, i + 0x8000));
  return btoa(bin);
};
// GitHub-style zips wrap everything in one top folder; strip it so paths match a git clone's root.
const stripCommonRoot = (src: Src[]): Src[] => {
  if (src.length === 0) return src;
  const firsts = new Set(src.map(s => s.path.split("/")[0]));
  if (firsts.size !== 1 || !src.every(s => s.path.includes("/"))) return src;
  const pre = `${[...firsts][0]}/`;
  return src.map(s => ({ ...s, path: s.path.slice(pre.length) }));
};
type Matcher = { dir: string; ig: ReturnType<typeof ignore> };
const mkMatcher = (dir: string, text: string): Matcher => ({ dir, ig: ignore().add(text) });
// A path is ignored if any applicable .gitignore ignores it; deeper .gitignore can re-include it via negation (!pattern).
const makeIsIgnored = (matchers: Matcher[]) => {
  const sorted = [...matchers].sort((a, b) => a.dir.length - b.dir.length);
  return (path: string) => {
    let hit = false;
    for (const { dir, ig } of sorted) {
      if (dir && path !== dir && !path.startsWith(`${dir}/`)) continue;
      const rel = dir ? path.slice(dir.length + 1) : path;
      if (!rel) continue;
      const r = ig.test(rel);
      if (r.ignored) hit = true; else if (r.unignored) hit = false;
    }
    return hit;
  };
};

type Handle = { path: string; getFile: () => Promise<File> };
// Enumerate a folder into file handles, applying .gitignore (root + nested) live so ignored files are never read.
async function collectFolder(dir: any, matchers: Matcher[], gi: boolean, prefix: string, out: Handle[]) {
  let local = matchers;
  if (gi) {
    for await (const e of dir.values())
      if (e.kind === "file" && e.name === ".gitignore") { local = [...matchers, mkMatcher(prefix, await (await e.getFile()).text())]; break; }
  }
  const isIgnored = makeIsIgnored(local);
  for await (const e of dir.values()) {
    const path = prefix ? `${prefix}/${e.name}` : e.name;
    if (skipPath(path)) continue;
    if (gi && isIgnored(path)) continue;
    if (e.kind === "directory") await collectFolder(e, local, gi, path, out);
    else out.push({ path, getFile: () => e.getFile() });
  }
}

export function StacksOverview({ simple = false }: { simple?: boolean }) {
  const nav = useNavigate();
  const { status } = useAuth();
  const canEdit = canOpenEditor(status?.user);
  const isAdmin = !!status?.user?.isAdmin;
  useTitle(simple ? "Apps" : "Stacks");
  const [stacks, setStacks] = useState<Stack[]>([]);
  const [deps, setDeps] = useState<Record<string, Deployment>>({});
  const [configFor, setConfigFor] = useState<Deployment | null>(null);
  const [logsFor, setLogsFor] = useState<Deployment | null>(null);
  const [backupsFor, setBackupsFor] = useState<Deployment | null>(null);
  const [domainFor, setDomainFor] = useState<Deployment | null>(null);
  const [terminalFor, setTerminalFor] = useState<Deployment | null>(null);
  const [filesFor, setFilesFor] = useState<Deployment | null>(null);
  const [installOpen, setInstallOpen] = useState(false);
  const [moveFor, setMoveFor] = useState<Deployment | null>(null);
  const [gitOpen, setGitOpen] = useState(false);
  const [importLocal, setImportLocal] = useState<{ name: string; sources: Src[] } | null>(null);
  const [readProg, setReadProg] = useState<{ label: string; done: number; total: number } | null>(null);
  const openStack = (s: Stack) => nav(deps[s.id] ? `/app/${s.id}` : simple ? "/hosting" : `/editor/${s.id}`);
  const hostedCount = stacks.filter(s => deps[s.id]).length;
  const loadDeps = () => api.listHosting().then(list => setDeps(Object.fromEntries(list.map(d => [d.stackId, d])))).catch(() => {});
  const [loading, setLoading] = useState(true);
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [templates, setTemplates] = useState<TemplateInfo[]>([]);
  const [query, setQuery] = useState("");
  const [creatorFilter, setCreatorFilter] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState<string | null>(null);
  const [statuses, setStatuses] = useState<Record<string, RunStatus>>({});
  const [liveByStack, setLiveByStack] = useState<Record<string, { name: string; url?: string; running: boolean }[]>>({});
  const [hostStat, setHostStat] = useState<Record<string, { cpu: number; memMb: number }>>({});

  useEffect(() => {
    if (stacks.length === 0) return;
    let cancelled = false;
    const poll = async () => {
      const entries = await Promise.all(stacks.map(async s =>
        [s.id, await api.statusStack(s.id).catch(() => ({ state: "NotRunning", log: [] } as RunStatus))] as const));
      if (!cancelled) setStatuses(Object.fromEntries(entries));
      loadDeps();
      const live: Record<string, { name: string; url?: string; running: boolean }[]> = {};
      await Promise.all(entries.filter(([, st]) => st.state === "Running").map(async ([id]) => {
        const res = await api.stackResources(id).catch(() => []);
        live[id] = res.filter(r => !r.hidden).map(r => ({
          name: r.displayName || r.name,
          url: r.urls.find(u => !u.isInternal && !u.isInactive)?.url,
          running: (r.state || "").toLowerCase().includes("running"),
        }));
      }));
      if (!cancelled) setLiveByStack(live);
      const stats = await api.hostingStats().catch(() => []);
      if (stats.length > 0) {
        const agg: Record<string, { cpu: number; memMb: number }> = {};
        for (const s of stacks) {
          const mine = stackContainers(stats, s.id);
          if (mine.length) agg[s.id] = {
            cpu: Math.round(mine.reduce((a, c) => a + c.cpu, 0) * 10) / 10,
            memMb: Math.round(mine.reduce((a, c) => a + c.memMb, 0)),
          };
        }
        if (!cancelled) setHostStat(agg);
      }
    };
    poll();
    const t = window.setInterval(poll, 4000);
    return () => { cancelled = true; window.clearInterval(t); };
  }, [stacks]);
  const setStatus = (id: string, rs: RunStatus) => setStatuses(m => ({ ...m, [id]: rs }));
  const zipInputRef = useRef<HTMLInputElement>(null);
  const folderInputRef = useRef<HTMLInputElement>(null);
  const composeInputRef = useRef<HTMLInputElement>(null);

  const [busy, setBusy] = useState<Set<string>>(new Set());
  const isBusy = (id: string) => busy.has(id);
  // Runs a card action with a per-stack busy lock (blocks double-clicks), a spinner, and error toasting.
  const withBusy = async (id: string, fn: () => Promise<unknown>, okMsg?: string) => {
    if (busy.has(id)) return;
    setBusy(p => new Set(p).add(id));
    try { await fn(); if (okMsg) toastOk(okMsg); }
    catch (e) { toastErr(e); }
    finally { setBusy(p => { const n = new Set(p); n.delete(id); return n; }); }
  };

  const creators = Array.from(new Set(stacks.map(s => s.createdBy).filter(Boolean))) as string[];
  const load = () => api.listStacks().then((s: Stack[]) => { setStacks(s); setLoading(false); });
  useEffect(() => { load(); }, []);
  useEffect(() => { api.getTemplates().then(setTemplates); }, []);
  useEffect(() => { folderInputRef.current?.setAttribute("webkitdirectory", ""); }, []);

  const create = async () => {
    const s = await api.createStack({
      name: name || "New Stack", targetFramework: "net10.0",
      nodes: [], edges: [], rawStatements: [], extraFiles: [], extraPackages: [],
    });
    setOpen(false); setName("");
    nav(`/editor/${s.id}`);
  };

  const createDemo = async (templateId: string) => {
    const s = await api.createFromTemplate(templateId);
    nav(`/editor/${s.id}`);
  };

  const rename = (s: Stack) => promptText("Rename stack", "Name", s.name).then(name => {
    if (name) api.saveStack({ ...s, name }).then(() => { load(); toastOk("Stack renamed"); }).catch(toastErr);
  });
  const duplicate = (s: Stack) => api.duplicateStack(s.id).then(() => { load(); toastOk("Stack duplicated"); }).catch(toastErr);
  const updateFromGit = async (s: Stack) => {
    toastOk(`Updating "${s.name}" from Git…`);
    try { await api.gitPull(s.id); load(); loadDeps(); toastOk(`Updated "${s.name}" from Git`); }
    catch (e) { toastErr(e, "Git update failed"); }
  };

  const openLocal = (name: string, sources: Src[]) => {
    const s = stripCommonRoot(sources);
    if (s.length === 0) { toastErr("No files found to import"); return; }
    setImportLocal({ name, sources: s });
  };
  const importSettings = async () => {
    const s = await api.getImportSettings().catch(() => ({ maxFileMb: 20, respectGitignore: true }));
    return { limit: (s.maxFileMb || 20) * 1024 * 1024, gitignore: s.respectGitignore };
  };
  const doneReading = (skipped: number, limit: number) => {
    setReadProg(null);
    if (skipped > 0) toastOk(`Skipped ${skipped} file(s) over ${Math.round(limit / 1024 / 1024)} MB`);
  };

  const onComposePicked = async (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file) return;
    try {
      const s = await api.importCompose(file.name.replace(/\.(ya?ml)$/i, "") || "compose", await file.text());
      nav(`/editor/${s.id}`);
    } catch (err) { toastErr(err, "Compose import failed"); }
  };

  const onZipPicked = async (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file) return;
    try {
      const { limit, gitignore } = await importSettings();
      const zip = await JSZip.loadAsync(file);
      let entries = Object.values(zip.files).filter((en: any) => !en.dir && !skipPath(en.name));
      if (gitignore) {
        const gis: Matcher[] = [];
        for (const en of entries) if ((en as any).name.split("/").pop() === ".gitignore") {
          const nm = (en as any).name as string;
          gis.push(mkMatcher(nm.includes("/") ? nm.slice(0, nm.lastIndexOf("/")) : "", await (en as any).async("string")));
        }
        if (gis.length) { const isIg = makeIsIgnored(gis); entries = entries.filter((en: any) => !isIg(en.name)); }
      }
      const sources: Src[] = []; let skipped = 0;
      setReadProg({ label: "", done: 0, total: entries.length });
      for (let i = 0; i < entries.length; i++) {
        const en: any = entries[i];
        setReadProg({ label: en.name, done: i + 1, total: entries.length });
        const b64 = await en.async("base64");
        if (b64.length * 0.75 > limit) { skipped++; continue; }
        sources.push({ path: en.name, content: b64 });
      }
      doneReading(skipped, limit);
      openLocal(file.name.replace(/\.zip$/i, ""), sources);
    } catch (err) { setReadProg(null); toastErr(err, "Zip import failed"); }
  };

  const onFolderFallbackPicked = async (e: ChangeEvent<HTMLInputElement>) => {
    const picked = e.target.files;
    e.target.value = "";
    if (!picked || picked.length === 0) return;
    const { limit, gitignore } = await importSettings();
    let all = Array.from(picked).filter(f => !skipPath(f.webkitRelativePath || f.name));
    if (gitignore) {
      const gis: Matcher[] = [];
      for (const f of all) { const p = f.webkitRelativePath || f.name; if (p.split("/").pop() === ".gitignore") gis.push(mkMatcher(p.includes("/") ? p.slice(0, p.lastIndexOf("/")) : "", await f.text())); }
      if (gis.length) { const isIg = makeIsIgnored(gis); all = all.filter(f => !isIg(f.webkitRelativePath || f.name)); }
    }
    const sources: Src[] = []; let skipped = 0;
    setReadProg({ label: "", done: 0, total: all.length });
    for (let i = 0; i < all.length; i++) {
      const file = all[i]; const path = file.webkitRelativePath || file.name;
      setReadProg({ label: path, done: i + 1, total: all.length });
      if (file.size > limit) { skipped++; continue; }
      sources.push({ path, content: bufToB64(await file.arrayBuffer()) });
    }
    doneReading(skipped, limit);
    const folderName = sources.find(f => f.path.includes("/"))?.path.split("/")[0] ?? "Imported";
    openLocal(folderName, sources);
  };

  const pickFolder = async () => {
    const showDirectoryPicker = (window as unknown as { showDirectoryPicker?: () => Promise<any> }).showDirectoryPicker;
    if (!showDirectoryPicker) { folderInputRef.current?.click(); return; }
    try {
      const dirHandle = await showDirectoryPicker();
      const { limit, gitignore } = await importSettings();
      setReadProg({ label: "", done: 0, total: 0 });
      const handles: Handle[] = [];
      await collectFolder(dirHandle, [], gitignore, "", handles);
      const sources: Src[] = []; let skipped = 0;
      for (let i = 0; i < handles.length; i++) {
        setReadProg({ label: handles[i].path, done: i + 1, total: handles.length });
        const file = await handles[i].getFile();
        if (file.size > limit) { skipped++; continue; }
        sources.push({ path: handles[i].path, content: bufToB64(await file.arrayBuffer()) });
      }
      doneReading(skipped, limit);
      openLocal(dirHandle.name, sources);
    } catch (e) {
      setReadProg(null);
      if (e instanceof DOMException && e.name === "AbortError") return; // user cancelled the picker
      toastErr(e);
    }
  };

  const headerActions = (
    <>
              <Button variant={simple ? "filled" : "default"} leftSection={<IconDownload size={16} />} onClick={() => setInstallOpen(true)}>Install from Store</Button>
              {!simple && <>
              <Button.Group>
                <Tooltip label="Create a new empty stack" withArrow>
                  <Button leftSection={<IconPlus size={16} />} onClick={() => setOpen(true)}>
                    New Stack
                  </Button>
                </Tooltip>
                <Menu position="bottom-end" withArrow>
                  <Menu.Target>
                    <Tooltip label="Create from a demo template" withArrow>
                      <Button px="xs" aria-label="Create from demo">
                        <IconChevronDown size={16} />
                      </Button>
                    </Tooltip>
                  </Menu.Target>
                  <Menu.Dropdown>
                    {templates.length === 0 ? (
                      <Menu.Item disabled>No demo templates</Menu.Item>
                    ) : (
                      <>
                        <Menu.Label>From demo…</Menu.Label>
                        {templates.filter(t => !t.id.startsWith("user:")).map(t => (
                          <Menu.Item key={t.id} leftSection={<IconSparkles size={14} />}
                            onClick={() => createDemo(t.id)}>
                            {t.name}
                          </Menu.Item>
                        ))}
                        {templates.some(t => t.id.startsWith("user:")) && <Menu.Label>Your templates</Menu.Label>}
                        {templates.filter(t => t.id.startsWith("user:")).map(t => (
                          <Menu.Item key={t.id} leftSection={<IconBookmark size={14} />}
                            onClick={() => createDemo(t.id)}
                            rightSection={
                              <ActionIcon component="div" size="sm" variant="subtle" color="red"
                                onClick={e => { e.stopPropagation();
                                  api.deleteUserTemplate(t.id.slice("user:".length)).then(() => { api.getTemplates().then(setTemplates); toastOk("Template deleted"); }).catch(toastErr); }}>
                                <IconTrash size={13} />
                              </ActionIcon>
                            }>
                            {t.name}
                          </Menu.Item>
                        ))}
                      </>
                    )}
                  </Menu.Dropdown>
                </Menu>
              </Button.Group>

              <Menu position="bottom-end" withArrow>
                <Menu.Target>
                  <Tooltip label="Import an existing AppHost (.cs/.csproj or .zip)" withArrow>
                    <Button variant="default" leftSection={<IconUpload size={16} />} rightSection={<IconChevronDown size={16} />}>
                      Import
                    </Button>
                  </Tooltip>
                </Menu.Target>
                <Menu.Dropdown>
                  <Menu.Item leftSection={<IconFileZip size={14} />} onClick={() => zipInputRef.current?.click()}>
                    ZIP archive
                  </Menu.Item>
                  <Menu.Item leftSection={<IconFolder size={14} />} onClick={pickFolder}>
                    Folder (.cs/.csproj)
                  </Menu.Item>
                  <Menu.Item leftSection={<IconFileZip size={14} />} onClick={() => composeInputRef.current?.click()}>
                    docker-compose.yml
                  </Menu.Item>
                  <Menu.Item leftSection={<IconBrandGithub size={14} />} onClick={() => setGitOpen(true)}>
                    Git repository…
                  </Menu.Item>
                </Menu.Dropdown>
              </Menu>
              <input ref={zipInputRef} type="file" accept=".zip" hidden onChange={onZipPicked} />
              <input ref={folderInputRef} type="file" multiple hidden onChange={onFolderFallbackPicked} />
              <input ref={composeInputRef} type="file" accept=".yml,.yaml" hidden onChange={onComposePicked} />
              </>}
    </>
  );

  const passesFilters = (s: typeof stacks[number]) => {
    if (!s.name.toLowerCase().includes(query.trim().toLowerCase())) return false;
    if (creatorFilter && s.createdBy !== creatorFilter) return false;
    if (statusFilter) {
      const running = ["Running", "Starting"].includes(statuses[s.id]?.state ?? "NotRunning");
      if (statusFilter === "running" && !running) return false;
      if (statusFilter === "stopped" && running) return false;
    }
    return true;
  };
  const visible = stacks.filter(passesFilters);
  const hostingStacks = visible.filter(s => deps[s.id]);              // has a hosting deployment
  const buildStacks = simple ? [] : visible.filter(s => !deps[s.id]); // dev/build stacks (hidden in appliance view)
  const runningHosted = hostingStacks.filter(s => deps[s.id]?.state === "running" && !hostingBroken(deps[s.id])).length;
  const brokenHosted = hostingStacks.filter(s => hostingBroken(deps[s.id])).length;

  const renderCard = (s: typeof stacks[number]) => {
                const st = statuses[s.id];
                const state = st?.state ?? "NotRunning";
                const dep = deps[s.id];
                // A hosted stack's card reflects its HOSTING state (border + chip + dot + controls);
                // otherwise it reflects the ephemeral dev-run state.
                const dot = dep ? `var(--mantine-color-${hostingColor(dep.state)}-6)` : (runStateColor(state) ?? "gray");
                const failDetail = dep?.state === "failed" ? (dep.lastError?.trim().split("\n").slice(-6).join("\n") || "Deploy failed")
                  : state === "Failed" ? (st!.log.slice(-6).join("\n") || "Run failed") : null;
                const active = dep ? dep.state === "running" : (state === "Running" || state === "Starting");
                const hostUrl = dep?.state === "running" ? dep.urls[0] : undefined;
                const domainUrl = dep?.state === "running" ? dep.domains?.[0] : undefined;
                const live = liveByStack[s.id] ?? [];
                // Match an icon-group (by its resource names) to a live resource; used for click-to-open + glow.
                const liveFor = (names: string[]) => live.find(l => names.some(nm => l.name === nm || l.name.startsWith(nm + "-") || nm.startsWith(l.name)));
                const openable = live.filter(l => l.url);   // resources with a reachable URL (dashboard menu)
                return (
                <Card
                  key={s.id}
                  withBorder
                  shadow="sm"
                  padding="lg"
                  className="stack-card"
                  style={{ cursor: "pointer", borderColor: dep ? dot : undefined, borderWidth: dep ? 2 : undefined }}
                  onClick={() => openStack(s)}
                >
                  <Group justify="space-between" wrap="nowrap" align="flex-start">
                    <Group gap={8} wrap="nowrap" style={{ minWidth: 0 }}>
                      <Tooltip label={failDetail ?? (dep?.state ?? state)} withArrow multiline maw={360}
                        styles={failDetail ? { tooltip: { whiteSpace: "pre-wrap", fontFamily: "monospace", fontSize: 11, textAlign: "left" } } : undefined}>
                        <span style={{ width: 10, height: 10, borderRadius: "50%", background: dot, flexShrink: 0,
                          boxShadow: active ? `0 0 6px ${dot}` : undefined }} />
                      </Tooltip>
                      <Text fw={600} lineClamp={1}>{s.name}</Text>
                      {s.fromGit && <Tooltip label="Imported from Git" withArrow><IconBrandGithub size={14} style={{ flexShrink: 0, opacity: 0.55 }} /></Tooltip>}
                    </Group>
                    <Group gap={6} wrap="nowrap">
                      {dep && (
                        <Tooltip label={dep.healthDetail ?? ""} withArrow disabled={!dep.healthDetail} multiline maw={320}>
                          <Badge size="xs" variant="light" color={deploymentColor(dep)}>
                            {dep.state === "running" ? (hostingHealthLabel(dep) ?? "Hosting") : dep.state}
                          </Badge>
                        </Tooltip>
                      )}
                      {dep && dep.targetId && dep.targetId !== "local" && (
                        <Tooltip label={`Runs on ${dep.targetName}`} withArrow>
                          <Badge size="xs" variant="default" leftSection={targetIcon(dep.targetKind ?? "ssh")}>{dep.targetName}</Badge>
                        </Tooltip>
                      )}
                      <Menu position="bottom-end" withArrow>
                        <Menu.Target>
                          <ActionIcon variant="subtle" color="gray" aria-label={`Actions for ${s.name}`}
                            onClick={e => e.stopPropagation()}>
                            <IconDots size={16} />
                          </ActionIcon>
                        </Menu.Target>
                        <Menu.Dropdown onClick={e => e.stopPropagation()}>
                          {dep ? (
                            <>
                              <HostingMenuItems d={dep} canEdit={canEdit} onConfigure={() => setConfigFor(dep)} onLogs={() => setLogsFor(dep)}
                                onBackups={() => setBackupsFor(dep)} onDomain={() => setDomainFor(dep)}
                                onTerminal={isAdmin ? () => setTerminalFor(dep) : undefined} onFiles={isAdmin ? () => setFilesFor(dep) : undefined}
                                onMove={isAdmin ? () => setMoveFor(dep) : undefined}
                                onOpenEditor={() => nav(`/editor/${s.id}`)} onChanged={loadDeps} />
                              <Menu.Divider />
                              {s.fromGit && <Menu.Item leftSection={<IconBrandGithub size={14} />} onClick={() => updateFromGit(s)}>Update from Git</Menu.Item>}
                              <Menu.Item leftSection={<IconPencil size={14} />} onClick={() => rename(s)}>Rename</Menu.Item>
                              <Menu.Item leftSection={<IconCopy size={14} />} onClick={() => duplicate(s)}>Duplicate</Menu.Item>
                            </>
                          ) : (
                            <>
                              {s.fromGit && <Menu.Item leftSection={<IconBrandGithub size={14} />} onClick={() => updateFromGit(s)}>Update from Git</Menu.Item>}
                              <Menu.Item leftSection={<IconPencil size={14} />} onClick={() => rename(s)}>Rename</Menu.Item>
                              <Menu.Item leftSection={<IconCopy size={14} />} onClick={() => duplicate(s)}>Duplicate</Menu.Item>
                              <Menu.Item leftSection={<IconServer size={14} />} disabled={isBusy(s.id)}
                                onClick={() => withBusy(s.id, async () => { toastOk(`Deploying "${s.name}" to hosting…`); await api.hostingDeploy(s.id); loadDeps(); nav("/hosting"); })}>
                                Deploy to hosting
                              </Menu.Item>
                              <Menu.Divider />
                              <Menu.Item color="red" leftSection={<IconTrash size={14} />} disabled={isBusy(s.id)}
                                onClick={async () => {
                                  if (!(await confirmDelete(`stack "${s.name}"`))) return;
                                  await withBusy(s.id, async () => { toastOk(`Deleting "${s.name}"…`); await api.deleteStack(s.id); load(); }, `Stack "${s.name}" deleted`);
                                }}>Delete</Menu.Item>
                            </>
                          )}
                        </Menu.Dropdown>
                      </Menu>
                    </Group>
                  </Group>
                  {s.nodes.length > 0 && (
                    <Group gap={5} mt="sm" wrap="nowrap">
                      {[...new Map(s.nodes.map(n => [n.icon || n.addMethod, n])).values()].slice(0, 8).map(n => {
                        const key = n.icon || n.addMethod;
                        const names = s.nodes.filter(m => (m.icon || m.addMethod) === key).map(m => m.resourceName);
                        const lm = liveFor(names);
                        const openUrl = lm?.url ?? hostUrl;      // resource URL wins, else the app URL
                        const lit = lm ? lm.running : active;    // glow when this service runs (else the whole stack's state)
                        return (
                          <Tooltip key={key} label={openUrl ? `Open ${names.join(", ")}` : names.join(", ")} withArrow>
                            <span style={{ display: "flex", cursor: openUrl ? "pointer" : undefined, borderRadius: 6,
                              filter: lit ? undefined : "grayscale(0.4) opacity(0.75)",
                              boxShadow: lit ? `0 0 7px 1px var(--mantine-color-${dep ? hostingColor(dep.state) : "green"}-5)` : undefined,
                              transition: "filter .3s, box-shadow .3s" }}
                              onClick={openUrl ? e => { e.stopPropagation(); window.open(openUrl, "_blank"); } : undefined}>
                              <ResourceGlyph addMethod={n.addMethod} iconKey={n.icon} size={17} />
                            </span>
                          </Tooltip>
                        );
                      })}
                      {new Map(s.nodes.map(n => [n.icon || n.addMethod, n])).size > 8 &&
                        <Text size="xs" c="dimmed">+{new Map(s.nodes.map(n => [n.icon || n.addMethod, n])).size - 8}</Text>}
                    </Group>
                  )}
                  <Group mt="sm" gap="xs" justify="space-between">
                    <Group gap="xs">
                      <Badge variant="light" color="indigo">{s.nodes.length} resource{s.nodes.length === 1 ? "" : "s"}</Badge>
                      {dep?.state === "running" && hostStat[s.id]
                        ? <Tooltip label={`CPU ${hostStat[s.id].cpu}% · ${hostStat[s.id].memMb} MB`} withArrow>
                            <Badge variant="light" color="blue" ff="monospace">{hostStat[s.id].cpu}% · {hostStat[s.id].memMb}MB</Badge>
                          </Tooltip>
                        : <Badge variant="outline" color="gray">{s.targetFramework}</Badge>}
                    </Group>
                    <Group gap={4} onClick={e => e.stopPropagation()}>
                      {dep ? (
                        <>
                          {dep.state === "deploying" || isBusy(s.id)
                            ? <Tooltip label={isBusy(s.id) ? "Working…" : "Deploying — pulling images &amp; starting…"} withArrow><Loader size="xs" color="yellow" /></Tooltip>
                            : active
                            ? <Tooltip label="Stop (hosting)" withArrow><ActionIcon size="sm" variant="subtle" color="red" disabled={isBusy(s.id)}
                                onClick={() => withBusy(s.id, async () => { toastOk(`Stopping "${s.name}"…`); await api.stopHosting(s.id); loadDeps(); })}><IconPlayerStop size={15} /></ActionIcon></Tooltip>
                            : <Tooltip label={dep.state === "failed" ? "Retry (hosting)" : "Start (hosting)"} withArrow><ActionIcon size="sm" variant="subtle" color="green" disabled={isBusy(s.id)}
                                onClick={() => withBusy(s.id, async () => { toastOk(`${dep.state === "failed" ? "Retrying" : "Starting"} "${s.name}"…`); await api.startHosting(s.id); loadDeps(); })}><IconPlayerPlay size={15} /></ActionIcon></Tooltip>}
                          {domainUrl && (
                            <Tooltip label={`Open via domain (${domainUrl})`} withArrow><ActionIcon size="sm" variant="subtle" color="grape" component="a"
                              href={domainUrl} target="_blank"><IconWorld size={15} /></ActionIcon></Tooltip>
                          )}
                          {hostUrl && (
                            <Tooltip label={domainUrl ? `Open internally (${hostUrl})` : "Open app"} withArrow><ActionIcon size="sm" variant="subtle" component="a"
                              href={hostUrl} target="_blank"><IconExternalLink size={15} /></ActionIcon></Tooltip>
                          )}
                        </>
                      ) : (
                        <>
                          {isBusy(s.id) ? (
                            <Loader size="xs" />
                          ) : active ? (
                            <Tooltip label="Stop" withArrow><ActionIcon size="sm" variant="subtle" color="red"
                              onClick={() => withBusy(s.id, async () => setStatus(s.id, await api.stopStack(s.id)))}><IconPlayerStop size={15} /></ActionIcon></Tooltip>
                          ) : (
                            <Tooltip label="Start" withArrow><ActionIcon size="sm" variant="subtle" color="green"
                              onClick={() => withBusy(s.id, async () => setStatus(s.id, await api.runStack(s.id)))}><IconPlayerPlay size={15} /></ActionIcon></Tooltip>
                          )}
                          {state === "Running" && (st?.dashboardUrl || openable.length > 0) && (
                            <Menu position="bottom-end" withArrow>
                              <Menu.Target>
                                <Tooltip label="Open dashboard / a resource" withArrow>
                                  <ActionIcon size="sm" variant="subtle"><IconExternalLink size={15} /></ActionIcon>
                                </Tooltip>
                              </Menu.Target>
                              <Menu.Dropdown>
                                {st?.dashboardUrl && <Menu.Item leftSection={<IconLayoutDashboard size={14} />} component="a" href={st.dashboardUrl} target="_blank">Aspire dashboard</Menu.Item>}
                                {openable.length > 0 && <Menu.Label>Resources</Menu.Label>}
                                {openable.map(l => (
                                  <Menu.Item key={l.name} leftSection={<IconExternalLink size={13} />} component="a" href={l.url} target="_blank">{l.name}</Menu.Item>
                                ))}
                              </Menu.Dropdown>
                            </Menu>
                          )}
                        </>
                      )}
                    </Group>
                  </Group>
                  {(s.createdBy || s.createdAt) && (
                    <Text size="xs" c="dimmed" mt={8}>
                      {s.createdBy && <>by <b>{s.createdBy}</b></>}
                      {s.createdBy && s.createdAt && " · "}
                      {s.createdAt && new Date(s.createdAt).toLocaleDateString()}
                    </Text>
                  )}
                </Card>
                );
  };

  return (
    <PageShell back={false} actions={headerActions}>
          <Group justify="space-between" mb="lg">
            <div>
              <Title order={2} fw={600}>{simple ? "My apps" : "Stacks"}</Title>
              <Text c="dimmed" size="sm">{simple ? "Your installed apps — click to manage." : "Your Aspire hosting projects, ready to open or run."}</Text>
            </div>
            {stacks.length > 0 && (
              <Group gap="xs">
                <TextInput w={220} placeholder="Search stacks…" value={query}
                  onChange={e => setQuery(e.currentTarget.value)} leftSection={<IconSearch size={14} />} />
                {creators.length > 1 && (
                  <Select w={150} placeholder="Any creator" clearable value={creatorFilter} onChange={setCreatorFilter}
                    data={creators} leftSection={<IconUser size={14} />} />
                )}
                <Select w={140} placeholder="Any status" clearable value={statusFilter} onChange={setStatusFilter}
                  data={[{ value: "running", label: "Running" }, { value: "stopped", label: "Not running" }]} />
              </Group>
            )}
          </Group>

          {loading ? (
            <Center py={80}>
              <Loader color="indigo" />
            </Center>
          ) : (simple ? hostedCount === 0 : stacks.length === 0) ? (
            <Center py={80}>
              <MStack align="center" gap="xs">
                <ThemeIcon variant="light" size={48} radius="xl" color="gray">
                  <IconLayoutGrid size={24} />
                </ThemeIcon>
                {simple ? (
                  <>
                    <Text fw={500}>No apps yet</Text>
                    <Text c="dimmed" size="sm" ta="center" maw={320}>Install an app from the store to get started.</Text>
                    <Button mt="sm" leftSection={<IconDownload size={16} />} onClick={() => setInstallOpen(true)}>Browse app store</Button>
                  </>
                ) : (
                  <>
                    <Text fw={500}>No stacks yet</Text>
                    <Text c="dimmed" size="sm" ta="center" maw={320}>
                      Create your first stack to start composing Aspire resources visually.
                    </Text>
                    <Button mt="sm" leftSection={<IconPlus size={16} />} onClick={() => setOpen(true)}>
                      New Stack
                    </Button>
                    {templates.length > 0 && (
                      <>
                        <Text c="dimmed" size="xs" mt="lg">…or start from a template</Text>
                        <Group justify="center" gap="xs" maw={460} mt={4}>
                          {templates.map(t => (
                            <Tooltip key={t.id} label={t.description} withArrow multiline w={260}>
                              <Button size="xs" variant="light" leftSection={<IconSparkles size={13} />}
                                onClick={() => createDemo(t.id)}>{t.name}</Button>
                            </Tooltip>
                          ))}
                        </Group>
                      </>
                    )}
                    <Text c="dimmed" size="xs" mt="md">Tip: press <b>Ctrl/⌘ + K</b> anywhere for the command palette, or <b>?</b> for shortcuts.</Text>
                  </>
                )}
              </MStack>
            </Center>
          ) : (
            <>
              {buildStacks.length > 0 && (
                <MStack gap="sm" mb={hostingStacks.length > 0 ? "xl" : 0}>
                  {!simple && (
                    <Group gap="xs">
                      <IconLayoutGrid size={16} />
                      <Title order={5} fw={600}>Stacks</Title>
                      <Badge variant="light" color="gray" size="sm">{buildStacks.length}</Badge>
                    </Group>
                  )}
                  <SimpleGrid cols={{ base: 1, sm: 2, md: 3 }} spacing="lg">
                    {buildStacks.map(renderCard)}
                  </SimpleGrid>
                </MStack>
              )}
              {hostingStacks.length > 0 && (
                <MStack gap="sm">
                  {!simple && buildStacks.length > 0 && <Divider />}
                  <Group justify="space-between" wrap="nowrap">
                    <Group gap="xs">
                      <IconServer size={16} />
                      <Title order={5} fw={600}>{simple ? "My apps" : "Hosting"}</Title>
                      <Badge variant="light" color="teal" size="sm">{runningHosted}/{hostingStacks.length} running</Badge>
                      {brokenHosted > 0 && (
                        <Tooltip label="Containers are crash-looping or unhealthy — open the app to see why" withArrow>
                          <Badge variant="light" color="red" size="sm">{brokenHosted} broken</Badge>
                        </Tooltip>
                      )}
                    </Group>
                    {!simple && <Anchor size="sm" onClick={() => nav("/hosting")}>Manage hosting →</Anchor>}
                  </Group>
                  <SimpleGrid cols={{ base: 1, sm: 2, md: 3 }} spacing="lg">
                    {hostingStacks.map(renderCard)}
                  </SimpleGrid>
                </MStack>
              )}
              {visible.length === 0 && (
                <Center py={60}><Text c="dimmed" size="sm">No {simple ? "apps" : "stacks"} match your filters.</Text></Center>
              )}
            </>
          )}

      <Modal opened={open} onClose={() => setOpen(false)} title="New Stack" centered>
        <TextInput
          label="Name"
          placeholder="e.g. checkout-service"
          value={name}
          onChange={e => setName(e.currentTarget.value)}
          onKeyDown={e => { if (e.key === "Enter") create(); }}
          data-autofocus
        />
        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setOpen(false)}>Cancel</Button>
          <Button onClick={create}>Create</Button>
        </Group>
      </Modal>

      {configFor && <ConfigureModal d={configFor} onClose={() => setConfigFor(null)} onDone={loadDeps} />}
      {logsFor && <LogsModal d={logsFor} onClose={() => setLogsFor(null)} />}
      {backupsFor && <BackupsModal d={backupsFor} onClose={() => setBackupsFor(null)} onChanged={loadDeps} />}
      {domainFor && <DomainModal d={domainFor} onClose={() => setDomainFor(null)} />}
      {terminalFor && <TerminalModal d={terminalFor} onClose={() => setTerminalFor(null)} />}
      {filesFor && <VolumesModal d={filesFor} onClose={() => setFilesFor(null)} />}
      {installOpen && <InstallAppModal onClose={() => setInstallOpen(false)} onInstalled={() => { load(); loadDeps(); }} />}
      {moveFor && (
        <MoveAppModal stackId={moveFor.stackId} name={moveFor.name} current={moveFor.targetId ?? "local"}
          onClose={() => setMoveFor(null)} onDone={() => { setMoveFor(null); load(); loadDeps(); }} />
      )}
      {gitOpen && <GitImportModal onClose={() => setGitOpen(false)} onImported={(id) => { setGitOpen(false); load(); nav(`/editor/${id}`); }} />}
      {importLocal && <GitImportModal local={importLocal} onClose={() => setImportLocal(null)} onImported={(id) => { setImportLocal(null); load(); nav(`/editor/${id}`); }} />}
      <Modal opened={readProg !== null} onClose={() => {}} withCloseButton={false} closeOnClickOutside={false} title="Reading files…" size="md" centered>
        {readProg && (
          <MStack gap="xs">
            <Group gap="sm" wrap="nowrap"><Loader size="sm" /><Text size="sm">{readProg.done}{readProg.total ? ` / ${readProg.total}` : ""} files</Text></Group>
            <Text size="xs" c="dimmed" style={{ wordBreak: "break-all" }}>{readProg.label}</Text>
          </MStack>
        )}
      </Modal>
    </PageShell>
  );
}
