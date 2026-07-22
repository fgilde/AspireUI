import { useEffect, useRef, useState } from "react";
import type { ChangeEvent } from "react";
import { useNavigate } from "react-router-dom";
import JSZip from "jszip";
import {
  AppShell, Group, Title, Text, Button, SimpleGrid, Card, ActionIcon, Anchor,
  Modal, TextInput, Badge, Container, Center, Loader, Stack as MStack, ThemeIcon, Menu, Tooltip, Select,
} from "@mantine/core";
import {
  IconPlus, IconTrash, IconLayoutGrid, IconChevronDown, IconSparkles,
  IconUpload, IconFileZip, IconFolder, IconSettings, IconDots, IconCopy, IconPencil, IconSearch,
  IconPlayerPlay, IconPlayerStop, IconExternalLink, IconBookmark, IconUser,
} from "@tabler/icons-react";
import { pickAppHost, APP_VERSION, BUILD_INFO, runStateColor, type Stack, type RunStatus } from "../model";
import { ResourceGlyph } from "../resourceIcons";
import * as api from "../api";
import logo from "../assets/logo.svg";
import type { TemplateInfo, BundleFile } from "../api";
import { HelpButton } from "../HelpButton";
import { UserMenu } from "../auth/UserMenu";
import { ThemeMenu } from "../ThemeMenu";
import { GitHubLink } from "../GitHubLink";
import { confirmDelete, toastOk, toastErr, promptText } from "../ui";
import "./StacksOverview.css";

const isImportable = (path: string) => /\.(cs|csproj)$/i.test(path);

// The File System Access API (window.showDirectoryPicker) isn't in TS's DOM
// lib yet; treat the directory handle as `any` rather than hand-rolling types
// for an API surface this narrow.
async function walkDirectory(dir: any, prefix = ""): Promise<BundleFile[]> {
  const files: BundleFile[] = [];
  for await (const entry of dir.values()) {
    const path = prefix ? `${prefix}/${entry.name}` : entry.name;
    if (entry.kind === "directory") files.push(...await walkDirectory(entry, path));
    else if (isImportable(entry.name)) files.push({ path, content: await (await entry.getFile()).text() });
  }
  return files;
}

export function StacksOverview() {
  const nav = useNavigate();
  const [stacks, setStacks] = useState<Stack[]>([]);
  const [loading, setLoading] = useState(true);
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [templates, setTemplates] = useState<TemplateInfo[]>([]);
  const [query, setQuery] = useState("");
  const [creatorFilter, setCreatorFilter] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState<string | null>(null);
  const [statuses, setStatuses] = useState<Record<string, RunStatus>>({});

  // Poll run status for every stack so the cards show a live traffic light + controls.
  useEffect(() => {
    if (stacks.length === 0) return;
    let cancelled = false;
    const poll = async () => {
      const entries = await Promise.all(stacks.map(async s =>
        [s.id, await api.statusStack(s.id).catch(() => ({ state: "NotRunning", log: [] } as RunStatus))] as const));
      if (!cancelled) setStatuses(Object.fromEntries(entries));
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
  // webkitdirectory isn't a typed React DOM prop; set it imperatively so the
  // fallback <input> picks a whole folder in browsers that support it.
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

  return (
    <AppShell header={{ height: 64 }} footer={{ height: 36 }} padding="lg">
      <AppShell.Header withBorder>
        <Container size="xl" h="100%">
          <Group h="100%" justify="space-between">
            <Group gap="sm">
              <img src={logo} alt="AspireUI" height={60} style={{ display: "block" }} />
            </Group>
            <Group gap="sm">
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
                </Menu.Dropdown>
              </Menu>
              <input ref={zipInputRef} type="file" accept=".zip" hidden onChange={onZipPicked} />
              <input ref={folderInputRef} type="file" multiple hidden onChange={onFolderFallbackPicked} />
              <input ref={composeInputRef} type="file" accept=".yml,.yaml" hidden onChange={onComposePicked} />

              <Tooltip label="Settings" withArrow>
                <ActionIcon variant="default" size="lg" onClick={() => nav("/settings")} aria-label="Settings">
                  <IconSettings size={18} />
                </ActionIcon>
              </Tooltip>
              <HelpButton />
              <GitHubLink />
              <ThemeMenu />
              <UserMenu />
            </Group>
          </Group>
        </Container>
      </AppShell.Header>

      <AppShell.Main>
        <Container size="xl">
          <Group justify="space-between" mb="lg">
            <div>
              <Title order={2} fw={600}>Stacks</Title>
              <Text c="dimmed" size="sm">Your Aspire hosting projects, ready to open or run.</Text>
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
          ) : stacks.length === 0 ? (
            <Center py={80}>
              <MStack align="center" gap="xs">
                <ThemeIcon variant="light" size={48} radius="xl" color="gray">
                  <IconLayoutGrid size={24} />
                </ThemeIcon>
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
              </MStack>
            </Center>
          ) : (
            <SimpleGrid cols={{ base: 1, sm: 2, md: 3 }} spacing="lg">
              {stacks.filter(s => {
                if (!s.name.toLowerCase().includes(query.trim().toLowerCase())) return false;
                if (creatorFilter && s.createdBy !== creatorFilter) return false;
                if (statusFilter) {
                  const running = ["Running", "Starting"].includes(statuses[s.id]?.state ?? "NotRunning");
                  if (statusFilter === "running" && !running) return false;
                  if (statusFilter === "stopped" && running) return false;
                }
                return true;
              }).map(s => {
                const st = statuses[s.id];
                const state = st?.state ?? "NotRunning";
                const dot = runStateColor(state) ?? "gray";
                const failDetail = state === "Failed" ? (st!.log.slice(-6).join("\n") || "Run failed") : null;
                const active = state === "Running" || state === "Starting";
                return (
                <Card
                  key={s.id}
                  withBorder
                  shadow="sm"
                  padding="lg"
                  className="stack-card"
                  style={{ cursor: "pointer" }}
                  onClick={() => nav(`/editor/${s.id}`)}
                >
                  <Group justify="space-between" wrap="nowrap" align="flex-start">
                    <Group gap={8} wrap="nowrap" style={{ minWidth: 0 }}>
                      <Tooltip label={failDetail ?? state} withArrow multiline maw={360}
                        styles={failDetail ? { tooltip: { whiteSpace: "pre-wrap", fontFamily: "monospace", fontSize: 11, textAlign: "left" } } : undefined}>
                        <span style={{ width: 10, height: 10, borderRadius: "50%", background: dot, flexShrink: 0,
                          boxShadow: active ? `0 0 6px ${dot}` : undefined }} />
                      </Tooltip>
                      <Text fw={600} lineClamp={1}>{s.name}</Text>
                    </Group>
                    <Menu position="bottom-end" withArrow>
                      <Menu.Target>
                        <ActionIcon variant="subtle" color="gray" aria-label={`Actions for ${s.name}`}
                          onClick={e => e.stopPropagation()}>
                          <IconDots size={16} />
                        </ActionIcon>
                      </Menu.Target>
                      <Menu.Dropdown onClick={e => e.stopPropagation()}>
                        <Menu.Item leftSection={<IconPencil size={14} />} onClick={() => rename(s)}>Rename</Menu.Item>
                        <Menu.Item leftSection={<IconCopy size={14} />} onClick={() => duplicate(s)}>Duplicate</Menu.Item>
                        <Menu.Divider />
                        <Menu.Item color="red" leftSection={<IconTrash size={14} />}
                          onClick={async () => {
                            if (!(await confirmDelete(`stack "${s.name}"`))) return;
                            await api.deleteStack(s.id); load(); toastOk(`Stack "${s.name}" deleted`);
                          }}>Delete</Menu.Item>
                      </Menu.Dropdown>
                    </Menu>
                  </Group>
                  {s.nodes.length > 0 && (
                    <Group gap={5} mt="sm" wrap="nowrap">
                      {[...new Map(s.nodes.map(n => [n.addMethod, n])).values()].slice(0, 8).map(n => (
                        <Tooltip key={n.addMethod} label={n.addMethod.replace(/^Add/, "")} withArrow>
                          <span style={{ display: "flex" }}><ResourceGlyph addMethod={n.addMethod} size={17} /></span>
                        </Tooltip>
                      ))}
                      {new Set(s.nodes.map(n => n.addMethod)).size > 8 &&
                        <Text size="xs" c="dimmed">+{new Set(s.nodes.map(n => n.addMethod)).size - 8}</Text>}
                    </Group>
                  )}
                  <Group mt="sm" gap="xs" justify="space-between">
                    <Group gap="xs">
                      <Badge variant="light" color="indigo">{s.nodes.length} resource{s.nodes.length === 1 ? "" : "s"}</Badge>
                      <Badge variant="outline" color="gray">{s.targetFramework}</Badge>
                    </Group>
                    <Group gap={4} onClick={e => e.stopPropagation()}>
                      {active ? (
                        <Tooltip label="Stop" withArrow><ActionIcon size="sm" variant="subtle" color="red"
                          onClick={() => api.stopStack(s.id).then(rs => setStatus(s.id, rs)).catch(e => toastErr(e))}><IconPlayerStop size={15} /></ActionIcon></Tooltip>
                      ) : (
                        <Tooltip label="Start" withArrow><ActionIcon size="sm" variant="subtle" color="green"
                          onClick={() => api.runStack(s.id).then(rs => setStatus(s.id, rs)).catch(e => toastErr(e, "Could not start"))}><IconPlayerPlay size={15} /></ActionIcon></Tooltip>
                      )}
                      {state === "Running" && st?.dashboardUrl && (
                        <Tooltip label="Open dashboard" withArrow><ActionIcon size="sm" variant="subtle" component="a"
                          href={st.dashboardUrl} target="_blank"><IconExternalLink size={15} /></ActionIcon></Tooltip>
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
              })}
            </SimpleGrid>
          )}
        </Container>
      </AppShell.Main>

      <AppShell.Footer>
        <Container size="xl" h="100%">
          <Group h="100%" justify="center" gap={6}>
            <Tooltip label={`build ${BUILD_INFO}`} withArrow><Text size="xs" c="dimmed">AspireUI v{APP_VERSION}</Text></Tooltip>
            <Text size="xs" c="dimmed">·</Text>
            <Anchor size="xs" c="dimmed" href="https://www.gilde.org" target="_blank" rel="noreferrer">by gilde.org</Anchor>
          </Group>
        </Container>
      </AppShell.Footer>

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
    </AppShell>
  );
}
