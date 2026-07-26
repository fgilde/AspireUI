import { useEffect, useRef, useState } from "react";
import type { ChangeEvent } from "react";
import { useNavigate } from "react-router-dom";
import JSZip from "jszip";
import {
  Group, Title, Text, Button, SimpleGrid, Card, ActionIcon, Divider, Anchor,
  Modal, TextInput, Badge, Center, Loader, Stack as MStack, ThemeIcon, Menu, Tooltip, Select,
} from "@mantine/core";
import {
  IconPlus, IconTrash, IconLayoutGrid, IconChevronDown, IconSparkles,
  IconUpload, IconFileZip, IconFolder, IconDots, IconCopy, IconPencil, IconSearch, IconServer,
  IconPlayerPlay, IconPlayerStop, IconExternalLink, IconBookmark, IconUser, IconDownload, IconLayoutDashboard, IconBrandGithub,
} from "@tabler/icons-react";
import { pickAppHost, runStateColor, canOpenEditor, type Stack, type RunStatus, type Deployment } from "../model";
import { ResourceGlyph } from "../resourceIcons";
import * as api from "../api";
import { useTitle } from "../useTitle";
import type { TemplateInfo, BundleFile } from "../api";
import { PageShell } from "../components/PageShell";
import { useAuth } from "../auth/AuthContext";
import { HostingMenuItems, ConfigureModal, LogsModal, BackupsModal, DomainModal, TerminalModal, VolumesModal, hostingColor } from "../hosting/HostingActions";
import { InstallAppModal } from "../hosting/InstallAppModal";
import { GitImportModal } from "../hosting/GitImportModal";
import { confirmDelete, toastOk, toastErr, promptText } from "../ui";
import "./StacksOverview.css";

const isImportable = (path: string) => /\.(cs|csproj)$/i.test(path);

async function walkDirectory(dir: any, prefix = ""): Promise<BundleFile[]> {
  const files: BundleFile[] = [];
  for await (const entry of dir.values()) {
    const path = prefix ? `${prefix}/${entry.name}` : entry.name;
    if (entry.kind === "directory") files.push(...await walkDirectory(entry, path));
    else if (isImportable(entry.name)) files.push({ path, content: await (await entry.getFile()).text() });
  }
  return files;
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
  const [gitOpen, setGitOpen] = useState(false);
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
          const mine = stats.filter(c => c.name.startsWith(`aspireui-${s.id.slice(0, 8)}`));
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

  const finishImport = async (bundleName: string, files: BundleFile[]) => {
    if (files.length === 0) { toastErr("No .cs/.csproj files found to import.", "Nothing to import"); return; }
    try {
      const s = await api.importBundle(bundleName, files, pickAppHost(files));
      nav(`/editor/${s.id}`);
    } catch (e) {
      toastErr(e, "Import failed");
    }
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
    const zip = await JSZip.loadAsync(file);
    const files: BundleFile[] = [];
    for (const entry of Object.values(zip.files)) {
      if (entry.dir || !isImportable(entry.name)) continue;
      files.push({ path: entry.name, content: await entry.async("string") });
    }
    await finishImport(file.name.replace(/\.zip$/i, ""), files);
  };

  const onFolderFallbackPicked = async (e: ChangeEvent<HTMLInputElement>) => {
    const picked = e.target.files;
    e.target.value = "";
    if (!picked || picked.length === 0) return;
    const files: BundleFile[] = [];
    for (const file of Array.from(picked)) {
      if (!isImportable(file.name)) continue;
      files.push({ path: file.webkitRelativePath || file.name, content: await file.text() });
    }
    toastErr("Folder picking isn't supported in this browser — some referenced files may be missing.", "Heads up");
    const folderName = files.find(f => f.path.includes("/"))?.path.split("/")[0] ?? "Imported";
    await finishImport(folderName, files);
  };

  const pickFolder = async () => {
    const showDirectoryPicker = (window as unknown as { showDirectoryPicker?: () => Promise<any> }).showDirectoryPicker;
    if (!showDirectoryPicker) { folderInputRef.current?.click(); return; }
    try {
      const dirHandle = await showDirectoryPicker();
      const files = await walkDirectory(dirHandle);
      await finishImport(dirHandle.name, files);
    } catch (e) {
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
  const runningHosted = hostingStacks.filter(s => deps[s.id]?.state === "running").length;

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
                      {dep && <Badge size="xs" variant="light" color={hostingColor(dep.state)}>{dep.state === "running" ? "Hosting" : dep.state}</Badge>}
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
                              <Menu.Item leftSection={<IconServer size={14} />}
                                onClick={() => { toastOk(`Deploying "${s.name}" to hosting…`); api.hostingDeploy(s.id).then(() => { loadDeps(); nav("/hosting"); }).catch(toastErr); }}>
                                Deploy to hosting
                              </Menu.Item>
                              <Menu.Divider />
                              <Menu.Item color="red" leftSection={<IconTrash size={14} />}
                                onClick={async () => {
                                  if (!(await confirmDelete(`stack "${s.name}"`))) return;
                                  await api.deleteStack(s.id); load(); toastOk(`Stack "${s.name}" deleted`);
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
                          {dep.state === "deploying"
                            ? <Tooltip label="Deploying — pulling images &amp; starting…" withArrow><Loader size="xs" color="yellow" /></Tooltip>
                            : active
                            ? <Tooltip label="Stop (hosting)" withArrow><ActionIcon size="sm" variant="subtle" color="red"
                                onClick={() => { toastOk(`Stopping "${s.name}"…`); api.stopHosting(s.id).then(loadDeps).catch(toastErr); }}><IconPlayerStop size={15} /></ActionIcon></Tooltip>
                            : <Tooltip label={dep.state === "failed" ? "Retry (hosting)" : "Start (hosting)"} withArrow><ActionIcon size="sm" variant="subtle" color="green"
                                onClick={() => { toastOk(`${dep.state === "failed" ? "Retrying" : "Starting"} "${s.name}"…`); api.startHosting(s.id).then(loadDeps).catch(toastErr); }}><IconPlayerPlay size={15} /></ActionIcon></Tooltip>}
                          {hostUrl && (
                            <Tooltip label="Open app" withArrow><ActionIcon size="sm" variant="subtle" component="a"
                              href={hostUrl} target="_blank"><IconExternalLink size={15} /></ActionIcon></Tooltip>
                          )}
                        </>
                      ) : (
                        <>
                          {active ? (
                            <Tooltip label="Stop" withArrow><ActionIcon size="sm" variant="subtle" color="red"
                              onClick={() => api.stopStack(s.id).then(rs => setStatus(s.id, rs)).catch(e => toastErr(e))}><IconPlayerStop size={15} /></ActionIcon></Tooltip>
                          ) : (
                            <Tooltip label="Start" withArrow><ActionIcon size="sm" variant="subtle" color="green"
                              onClick={() => api.runStack(s.id).then(rs => setStatus(s.id, rs)).catch(e => toastErr(e, "Could not start"))}><IconPlayerPlay size={15} /></ActionIcon></Tooltip>
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
      {gitOpen && <GitImportModal onClose={() => setGitOpen(false)} onImported={(id) => { setGitOpen(false); load(); nav(`/editor/${id}`); }} />}
    </PageShell>
  );
}
