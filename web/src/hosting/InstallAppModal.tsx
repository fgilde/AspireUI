import { useEffect, useMemo, useState, type ReactNode } from "react";
import { Modal, Title, TextInput, SimpleGrid, Card, Group, Text, Highlight, Button, Loader, Badge, ActionIcon, Tooltip, ScrollArea, Box, UnstyledButton, Stack as MStack } from "@mantine/core";
import { IconSearch, IconDownload, IconEye, IconEyeOff, IconInfoCircle, IconFlame, IconApps, IconCheck, IconMinus, IconX } from "@tabler/icons-react";
import type { ContainerPreset, Snippet, ResourceType, Node, Edge } from "../model";
import { buildPresetNodes, instantiateSnippet } from "../model";
import { ResourceGlyph, resourceVisual } from "../resourceIcons";
import { AppInfoModal, type AppInfo } from "../components/AppInfoModal";
import { AddResourceDialog } from "../editor/AddResourceDialog";
import { useAuth } from "../auth/AuthContext";
import * as api from "../api";
import { toastOk, toastErr } from "../ui";

// A handful of crowd-pleasers surfaced in the "Popular" band. Ids that don't exist are simply ignored,
// so this list can stay generous.
const FEATURED = new Set([
  "immich", "nextcloud", "vaultwarden", "jellyfin", "plex", "emby", "paperless-ngx", "homeassistant",
  "uptime-kuma", "pihole", "adguard", "gitea", "grafana", "portainer", "it-tools", "linkwarden", "mealie",
  "audiobookshelf", "actualbudget", "stirling-pdf", "excalidraw", "photoprism", "frigate", "mattermost",
  "outline", "penpot", "onlyoffice", "nocodb", "metabase", "searxng", "vikunja", "firefly-iii", "bookstack",
  "wikijs", "homarr", "npm", "nodered", "syncthing", "openwebui", "librechat", "komga", "kavita", "navidrome",
]);

// Catalog packages that aren't installable apps (core primitives / cloud bindings) — never shown in the store.
const PKG_SKIP = new Set(["AddContainer", "AddProject", "AddParameter", "AddConnectionString", "AddDockerfile", "AddExecutable"]);

type Kind = "app" | "package" | "snippet";
interface Item {
  id: string; kind: Kind; label: string; group: string; icon: string; description?: string | null;
  info: AppInfo; featured: boolean;
  rt?: ResourceType;                              // packages: routed through the add dialog
  install?: () => Promise<{ id: string }>;        // apps + snippets: created directly
}

const KIND_LABEL: Record<Kind, string> = { app: "App", package: "Package", snippet: "Snippet" };
const KIND_COLOR: Record<Kind, string> = { app: "blue", package: "teal", snippet: "grape" };

const presetItem = (p: ContainerPreset): Item => ({
  id: `preset:${p.id}`, kind: "app", label: p.label, group: p.group, icon: p.icon || "", description: p.description,
  featured: FEATURED.has(p.id),
  info: { label: p.label, group: p.group, icon: p.icon, description: p.description, website: p.website, image: p.image, port: p.port, screenshots: p.screenshots, tags: p.tags, kindLabel: "App",
    logo: p.logo, card: p.card, github: p.github, stars: p.stars, license: p.license, language: p.language, topics: p.topics },
  install: () => { const { nodes, edges } = buildPresetNodes(p, []); return api.createStack({ name: p.label, targetFramework: "net10.0", nodes, edges, rawStatements: [], extraFiles: p.files ?? [], extraPackages: [], hostingUrlPath: p.urlPath ?? null }); },
});
const snippetItem = (s: Snippet): Item => ({
  id: `snippet:${s.id}`, kind: "snippet", label: s.name, group: s.group || "Custom", icon: s.icon || (s.nodes[0]?.icon ?? s.nodes[0]?.addMethod ?? ""), featured: false,
  info: { label: s.name, group: s.group || "Custom", icon: s.icon, description: `A saved snippet with ${s.nodes.length} resource${s.nodes.length === 1 ? "" : "s"}.`, custom: true, kindLabel: "Snippet" },
  install: () => { const { nodes, edges } = instantiateSnippet(s, [], 0, 0); return api.createStack({ name: s.name, targetFramework: "net10.0", nodes, edges, rawStatements: [], extraFiles: s.files ?? [], extraPackages: [] }); },
});
const packageItem = (rt: ResourceType): Item => ({
  id: `pkg:${rt.addMethod}`, kind: "package", label: rt.label, group: rt.group || "Integrations", icon: rt.icon || rt.addMethod, description: rt.description, featured: false,
  info: { label: rt.label, group: rt.group || "Integrations", icon: rt.icon || rt.addMethod, description: rt.description, tags: rt.package ? [rt.package] : null, kindLabel: "Package" },
  rt,
});

export function InstallAppModal({ onClose, onInstalled }: { onClose: () => void; onInstalled: () => void }) {
  const { status } = useAuth();
  const isAdmin = !!status?.user?.isAdmin;
  const [items, setItems] = useState<Item[] | null>(null);
  const [excluded, setExcluded] = useState<Set<string>>(new Set());
  const [q, setQ] = useState("");
  // Tri-state filters (like the palette): a tag is "in" (must match one), "ex" (must match none), or absent.
  const [tagState, setTagState] = useState<Record<string, "in" | "ex">>({});
  const cycleTag = (t: string) => setTagState(s => {
    const next = { ...s };
    if (!s[t]) next[t] = "in"; else if (s[t] === "in") next[t] = "ex"; else delete next[t];
    return next;
  });
  const [installing, setInstalling] = useState<string | null>(null);
  const [infoItem, setInfoItem] = useState<Item | null>(null);
  const [pkgItem, setPkgItem] = useState<Item | null>(null);    // package awaiting the add dialog

  useEffect(() => {
    Promise.all([
      api.getPresets().catch(() => []),
      api.getSnippets().catch(() => []),
      (api.getCatalog() as Promise<ResourceType[]>).catch(() => []),
      api.getStoreExclusions().catch(() => []),
    ]).then(([presets, snippets, catalog, ex]) => {
      const pkgs = catalog.filter(rt => rt.package && rt.package !== "Aspire.Hosting" && !PKG_SKIP.has(rt.addMethod)
        && !rt.addMethod.startsWith("AddAzure") && !rt.addMethod.startsWith("AddAws"));
      setItems([...snippets.map(snippetItem), ...presets.map(presetItem), ...pkgs.map(packageItem)]);
      setExcluded(new Set(ex));
    });
  }, []);

  const toggleExclude = async (id: string) => {
    const next = new Set(excluded);
    next.has(id) ? next.delete(id) : next.add(id);
    setExcluded(next);
    try { await api.setStoreExclusions([...next]); } catch (e) { toastErr(e, "Could not save"); }
  };

  const finishInstall = async (label: string, mk: () => Promise<{ id: string }>) => {
    try {
      const stack = await mk();
      await api.hostingDeploy(stack.id);
      toastOk(`Installing ${label}…`);
      onInstalled(); onClose();
    } catch (e) { toastErr(e, "Install failed"); }
  };

  const install = async (it: Item) => {
    if (it.kind === "package") { setPkgItem(it); return; }   // configure via the add dialog first
    setInstalling(it.id);
    await finishInstall(it.label, it.install!);
    setInstalling(null);
  };

  // Package add dialog confirmed → build a stack from the node and deploy it.
  const onPackageCreate = async (node: Node, _refs: string[], _usedBy: string[], extra?: { nodes: Node[]; edges: Edge[] }) => {
    const rt = pkgItem!.rt!;
    setInstalling(pkgItem!.id); setPkgItem(null);
    const extraPackages = rt.package ? [{ id: rt.package, version: rt.packageVersion || "" }] : [];
    await finishInstall(rt.label, () => api.createStack({
      name: rt.label, targetFramework: "net10.0",
      nodes: [node, ...(extra?.nodes ?? [])], edges: extra?.edges ?? [],
      rawStatements: [], extraFiles: [], extraPackages,
    }));
    setInstalling(null);
  };

  const visible = (items ?? []).filter(it => isAdmin || !excluded.has(it.id));   // non-admins never see excluded
  const groups = useMemo(() =>
    [...new Set(visible.map(i => i.group))].sort((a, b) => a === "Custom" ? 1 : b === "Custom" ? -1 : a.localeCompare(b)),
    [visible]);
  const kinds = useMemo(() => (["app", "package", "snippet"] as Kind[]).filter(k => visible.some(i => i.kind === k)), [visible]);
  const featuredCount = visible.filter(i => i.featured).length;

  const ql = q.toLowerCase();
  const matchesQ = (it: Item) => !ql || it.label.toLowerCase().includes(ql) || it.group.toLowerCase().includes(ql) || (it.description ?? "").toLowerCase().includes(ql);
  // Tags an item carries for the tri-state filters: its kind, its group, and "popular" when featured.
  const itemTags = (it: Item) => [it.kind, it.group, ...(it.featured ? ["popular"] : [])];

  const inc = Object.keys(tagState).filter(t => tagState[t] === "in");
  const exc = Object.keys(tagState).filter(t => tagState[t] === "ex");
  const passTags = (it: Item) => { const t = itemTags(it); return (inc.length === 0 || inc.some(x => t.includes(x))) && !exc.some(x => t.includes(x)); };

  const searched = visible.filter(matchesQ);                       // for stable filter counts
  const filtered = searched.filter(passTags);
  const groupsWithItems = groups.filter(g => filtered.some(i => i.group === g));
  const tagCount = (t: string) => searched.filter(i => itemTags(i).includes(t)).length;

  const renderCard = (it: Item) => {
    const hidden = excluded.has(it.id);
    const color = resourceVisual(it.icon || "").color;
    return (
      <Card key={it.id} withBorder padding="md" radius="md" className="store-card"
        style={{ opacity: hidden ? 0.5 : 1, display: "flex", flexDirection: "column" }}>
        <Group gap={10} wrap="nowrap" align="flex-start">
          <div style={{ width: 38, height: 38, borderRadius: 9, flexShrink: 0, display: "grid", placeItems: "center",
            background: `${color}1f`, border: `1px solid ${color}44` }}>
            <ResourceGlyph addMethod={it.icon} iconKey={it.icon} size={22} />
          </div>
          <div style={{ minWidth: 0, flex: 1 }}>
            <Highlight component={Text} highlight={q} fw={600} size="sm" truncate>{it.label}</Highlight>
            <Group gap={5} mt={2}>
              <Badge size="xs" variant="light" color={KIND_COLOR[it.kind]}>{KIND_LABEL[it.kind]}</Badge>
              <Text size="10px" c="dimmed" truncate>{it.group}</Text>
              {hidden && <Badge size="xs" variant="light" color="gray">Hidden</Badge>}
            </Group>
          </div>
          {isAdmin && (
            <Tooltip label={hidden ? "Hidden from the store for other users — click to show" : "Hide from the store for other users"} withArrow multiline w={220}>
              <ActionIcon variant="subtle" color={hidden ? "orange" : "gray"} size="sm" onClick={() => toggleExclude(it.id)} aria-label="Toggle store visibility">
                {hidden ? <IconEyeOff size={15} /> : <IconEye size={15} />}
              </ActionIcon>
            </Tooltip>
          )}
        </Group>
        <Text size="xs" c="dimmed" mt={8} lineClamp={2} style={{ flex: 1, minHeight: 32 }}>
          {it.description || " "}
        </Text>
        <Group gap={6} mt="sm" wrap="nowrap">
          <Button flex={1} size="xs" leftSection={<IconDownload size={14} />}
            loading={installing === it.id} onClick={() => install(it)}>Install</Button>
          <Tooltip label="Details" withArrow>
            <ActionIcon variant="light" color="gray" size="lg" onClick={() => setInfoItem(it)} aria-label={`Details for ${it.label}`}>
              <IconInfoCircle size={17} />
            </ActionIcon>
          </Tooltip>
        </Group>
      </Card>
    );
  };

  return (
    <Modal opened onClose={onClose} size="85%" title={<Group gap={8}><IconApps size={18} /><Title order={5}>App store</Title></Group>}
      styles={{ body: { display: "flex", flexDirection: "column", minHeight: "68vh" } }}>
      <TextInput mb="md" placeholder="Search apps, packages, snippets…" value={q} onChange={e => setQ(e.currentTarget.value)}
        leftSection={<IconSearch size={14} />} autoFocus />

      <Group align="stretch" wrap="nowrap" gap={0} style={{ flex: 1, minHeight: 0 }}>
        {/* Left filter column — tri-state (click: include → exclude → clear) */}
        <ScrollArea w={196} style={{ flexShrink: 0, borderRight: "1px solid var(--mantine-color-default-border)" }} type="hover">
          <MStack gap={1} pr="md">
            {featuredCount > 0 && <FilterRow label="Popular" icon={<IconFlame size={13} />} count={tagCount("popular")} state={tagState["popular"]} onClick={() => cycleTag("popular")} />}
            {kinds.length > 1 && <>
              <FilterHeading>Type</FilterHeading>
              {kinds.map(k => <FilterRow key={k} label={KIND_LABEL[k]} count={tagCount(k)} state={tagState[k]} onClick={() => cycleTag(k)} />)}
            </>}
            <FilterHeading>Category</FilterHeading>
            {groups.map(g => <FilterRow key={g} label={g} count={tagCount(g)} state={tagState[g]} onClick={() => cycleTag(g)} />)}
            {(inc.length > 0 || exc.length > 0) && (
              <UnstyledButton onClick={() => setTagState({})} mt="xs"
                style={{ display: "flex", alignItems: "center", gap: 5, padding: "5px 8px", fontSize: 12, color: "var(--mantine-color-dimmed)" }}>
                <IconX size={12} /> Clear filters
              </UnstyledButton>
            )}
          </MStack>
        </ScrollArea>

        {/* Results */}
        <ScrollArea style={{ flex: 1 }} offsetScrollbars pl="md">
          {items === null ? <Loader size="sm" /> : filtered.length === 0 ? (
            <Text c="dimmed" size="sm">No matches{q ? ` for “${q}”` : ""}.</Text>
          ) : (
            <MStackSections groups={groupsWithItems} items={filtered} renderCard={renderCard} />
          )}
        </ScrollArea>
      </Group>

      {infoItem && (
        <AppInfoModal info={infoItem.info} onClose={() => setInfoItem(null)}
          onAction={() => { const it = infoItem; setInfoItem(null); install(it); }}
          actionLabel="Install" actionIcon={<IconDownload size={14} />} actionLoading={installing === infoItem.id} />
      )}
      {pkgItem && (
        <AddResourceDialog rt={pkgItem.rt!} existingCount={0} totalCount={0} nodes={[]}
          onCreate={onPackageCreate} onClose={() => setPkgItem(null)} />
      )}

      <style>{`
        .store-card{transition:transform .15s ease,box-shadow .15s ease,border-color .15s ease}
        .store-card:hover{transform:translateY(-3px);box-shadow:var(--mantine-shadow-md);border-color:var(--mantine-primary-color-filled)}
        .store-grid>*{animation:storeIn .28s ease both}
        @keyframes storeIn{from{opacity:0;transform:translateY(6px)}to{opacity:1;transform:none}}
        .store-filter:hover{background:var(--mantine-color-default-hover)}
      `}</style>
    </Modal>
  );
}

const FilterHeading = ({ children }: { children: ReactNode }) =>
  <Text fw={700} size="10px" tt="uppercase" c="dimmed" mt="sm" mb={2} style={{ letterSpacing: 0.5 }}>{children}</Text>;

// One tri-state filter row in the left column: include (primary) → exclude (red, struck) → off.
function FilterRow({ label, icon, count, state, onClick }: {
  label: string; icon?: ReactNode; count: number; state?: "in" | "ex"; onClick: () => void;
}) {
  const bg = state === "in" ? "var(--mantine-primary-color-light)" : state === "ex" ? "var(--mantine-color-red-light)" : "transparent";
  const col = state === "in" ? "var(--mantine-primary-color-light-color)" : state === "ex" ? "var(--mantine-color-red-light-color)" : "var(--mantine-color-text)";
  return (
    <UnstyledButton onClick={onClick} className="store-filter"
      title={state === "in" ? "Included — click to exclude" : state === "ex" ? "Excluded — click to clear" : "Click to include"}
      style={{ display: "flex", alignItems: "center", gap: 6, width: "100%", padding: "5px 8px", borderRadius: 6, fontSize: 13, background: bg, color: col, transition: "background .15s, color .15s" }}>
      <span style={{ width: 14, display: "grid", placeItems: "center", flexShrink: 0 }}>
        {state === "ex" ? <IconMinus size={12} /> : state === "in" ? <IconCheck size={12} /> : icon}
      </span>
      <span style={{ flex: 1, textAlign: "left", whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis", textDecoration: state === "ex" ? "line-through" : undefined }}>{label}</span>
      <Text component="span" size="10px" c="dimmed">{count}</Text>
    </UnstyledButton>
  );
}

// Grouped grids with a small header per group (used for the default "All" browse view).
function MStackSections({ groups, items, renderCard }: {
  groups: string[]; items: Item[]; renderCard: (it: Item) => ReactNode;
}) {
  return (
    <Box>
      {groups.map(g => {
        const inG = items.filter(i => i.group === g);
        if (inG.length === 0) return null;
        return (
          <Box key={g} mb="lg">
            <Group gap={8} mb={8}>
              <Text fw={700} size="xs" tt="uppercase" c="dimmed" style={{ letterSpacing: 0.5 }}>{g}</Text>
              <Badge size="xs" variant="default" c="dimmed">{inG.length}</Badge>
            </Group>
            <SimpleGrid cols={{ base: 1, sm: 2, md: 3, lg: 4 }} spacing="sm" className="store-grid">
              {inG.map(renderCard)}
            </SimpleGrid>
          </Box>
        );
      })}
    </Box>
  );
}
