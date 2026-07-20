import { useEffect, useRef, useState } from "react";
import type { ChangeEvent } from "react";
import { useNavigate } from "react-router-dom";
import JSZip from "jszip";
import {
  AppShell, Group, Title, Text, Button, SimpleGrid, Card, ActionIcon, Anchor,
  Modal, TextInput, Badge, Container, Center, Loader, Stack as MStack, ThemeIcon, Menu, Tooltip,
} from "@mantine/core";
import {
  IconPlus, IconTrash, IconStack2, IconLayoutGrid, IconChevronDown, IconSparkles,
  IconUpload, IconFileZip, IconFolder, IconSettings,
} from "@tabler/icons-react";
import { pickAppHost, APP_VERSION, type Stack } from "../model";
import * as api from "../api";
import type { TemplateInfo, BundleFile } from "../api";
import { HelpButton } from "../HelpButton";
import { UserMenu } from "../auth/UserMenu";
import { ThemeMenu } from "../ThemeMenu";
import { GitHubLink } from "../GitHubLink";
import { confirmDelete, toastOk, toastErr } from "../ui";
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
  const zipInputRef = useRef<HTMLInputElement>(null);
  const folderInputRef = useRef<HTMLInputElement>(null);

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
    nav(`/stacks/${s.id}`);
  };

  const createDemo = async (templateId: string) => {
    const s = await api.createFromTemplate(templateId);
    nav(`/stacks/${s.id}`);
  };

  const finishImport = async (bundleName: string, files: BundleFile[]) => {
    if (files.length === 0) { toastErr("No .cs/.csproj files found to import.", "Nothing to import"); return; }
    try {
      const s = await api.importBundle(bundleName, files, pickAppHost(files));
      nav(`/stacks/${s.id}`);
    } catch (e) {
      toastErr(e, "Import failed");
    }
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
              <ThemeIcon variant="light" size={32} radius="md">
                <IconStack2 size={18} />
              </ThemeIcon>
              <Title order={3} fw={700}>AspireUI</Title>
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
                        {templates.map(t => (
                          <Menu.Item key={t.id} leftSection={<IconSparkles size={14} />}
                            onClick={() => createDemo(t.id)}>
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
                </Menu.Dropdown>
              </Menu>
              <input ref={zipInputRef} type="file" accept=".zip" hidden onChange={onZipPicked} />
              <input ref={folderInputRef} type="file" multiple hidden onChange={onFolderFallbackPicked} />

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
              </MStack>
            </Center>
          ) : (
            <SimpleGrid cols={{ base: 1, sm: 2, md: 3 }} spacing="lg">
              {stacks.map(s => (
                <Card
                  key={s.id}
                  withBorder
                  shadow="sm"
                  padding="lg"
                  className="stack-card"
                  style={{ cursor: "pointer" }}
                  onClick={() => nav(`/stacks/${s.id}`)}
                >
                  <Group justify="space-between" wrap="nowrap" align="flex-start">
                    <Text fw={600} lineClamp={1}>{s.name}</Text>
                    <ActionIcon
                      variant="subtle"
                      color="red"
                      onClick={async (e) => {
                        e.stopPropagation();
                        if (!(await confirmDelete(`stack "${s.name}"`))) return;
                        await api.deleteStack(s.id); load(); toastOk(`Stack "${s.name}" deleted`);
                      }}
                      aria-label={`Delete ${s.name}`}
                    >
                      <IconTrash size={16} />
                    </ActionIcon>
                  </Group>
                  <Group mt="sm" gap="xs">
                    <Badge variant="light" color="indigo">{s.nodes.length} resource{s.nodes.length === 1 ? "" : "s"}</Badge>
                    <Badge variant="outline" color="gray">{s.targetFramework}</Badge>
                  </Group>
                </Card>
              ))}
            </SimpleGrid>
          )}
        </Container>
      </AppShell.Main>

      <AppShell.Footer>
        <Container size="xl" h="100%">
          <Group h="100%" justify="center" gap={6}>
            <Text size="xs" c="dimmed">AspireUI v{APP_VERSION}</Text>
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
