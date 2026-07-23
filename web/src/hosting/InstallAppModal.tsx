import { useEffect, useState } from "react";
import { Modal, Title, TextInput, SimpleGrid, Card, Group, Text, Button, Loader, Badge, ActionIcon, Tooltip } from "@mantine/core";
import { IconSearch, IconDownload, IconEye, IconEyeOff } from "@tabler/icons-react";
import type { ContainerPreset, Snippet } from "../model";
import { buildPresetNodes, instantiateSnippet } from "../model";
import { ResourceGlyph } from "../resourceIcons";
import { useAuth } from "../auth/AuthContext";
import * as api from "../api";
import { toastOk, toastErr } from "../ui";

// A store entry — a curated preset OR a user's saved snippet. Both install the same way: create a stack
// from it, then deploy that stack to hosting.
interface Item { id: string; label: string; group: string; icon: string; description?: string | null; custom: boolean; install: () => Promise<{ id: string }> }

const presetItem = (p: ContainerPreset): Item => ({
  id: `preset:${p.id}`, label: p.label, group: p.group, icon: p.icon || "", description: p.description, custom: false,
  install: () => { const { nodes, edges } = buildPresetNodes(p, []); return api.createStack({ name: p.label, targetFramework: "net10.0", nodes, edges, rawStatements: [], extraFiles: p.files ?? [], extraPackages: [], hostingUrlPath: p.urlPath ?? null }); },
});
const snippetItem = (s: Snippet): Item => ({
  id: `snippet:${s.id}`, label: s.name, group: s.group || "Custom", icon: s.icon || (s.nodes[0]?.icon ?? s.nodes[0]?.addMethod ?? ""), custom: true,
  install: () => { const { nodes, edges } = instantiateSnippet(s, [], 0, 0); return api.createStack({ name: s.name, targetFramework: "net10.0", nodes, edges, rawStatements: [], extraFiles: s.files ?? [], extraPackages: [] }); },
});

export function InstallAppModal({ onClose, onInstalled }: { onClose: () => void; onInstalled: () => void }) {
  const { status } = useAuth();
  const isAdmin = !!status?.user?.isAdmin;
  const [items, setItems] = useState<Item[] | null>(null);
  const [excluded, setExcluded] = useState<Set<string>>(new Set());
  const [q, setQ] = useState("");
  const [installing, setInstalling] = useState<string | null>(null);
  useEffect(() => {
    Promise.all([api.getPresets().catch(() => []), api.getSnippets().catch(() => []), api.getStoreExclusions().catch(() => [])])
      .then(([presets, snippets, ex]) => { setItems([...snippets.map(snippetItem), ...presets.map(presetItem)]); setExcluded(new Set(ex)); });
  }, []);

  // Admin toggles an item's app-store visibility (persisted globally).
  const toggleExclude = async (id: string) => {
    const next = new Set(excluded);
    next.has(id) ? next.delete(id) : next.add(id);
    setExcluded(next);
    try { await api.setStoreExclusions([...next]); } catch (e) { toastErr(e, "Could not save"); }
  };

  const install = async (it: Item) => {
    setInstalling(it.id);
    try {
      const stack = await it.install();
      await api.hostingDeploy(stack.id);
      toastOk(`Installing ${it.label}…`);
      onInstalled(); onClose();
    } catch (e) { toastErr(e, "Install failed"); }
    finally { setInstalling(null); }
  };

  const ql = q.toLowerCase();
  const shown = (items ?? [])
    .filter(it => isAdmin || !excluded.has(it.id))   // non-admins never see excluded apps
    .filter(it => !ql || it.label.toLowerCase().includes(ql) || it.group.toLowerCase().includes(ql));

  return (
    <Modal opened onClose={onClose} size="xl" title={<Title order={5}>App store</Title>}>
      <TextInput mb="md" placeholder="Search apps…" value={q} onChange={e => setQ(e.currentTarget.value)} leftSection={<IconSearch size={14} />} autoFocus />
      {items === null ? <Loader size="sm" /> : (
        <SimpleGrid cols={{ base: 1, sm: 2, md: 3 }}>
          {shown.map(it => {
            const hidden = excluded.has(it.id);
            return (
            <Card key={it.id} withBorder padding="md" radius="md" style={{ opacity: hidden ? 0.5 : 1 }}>
              <Group gap={10} wrap="nowrap" align="flex-start">
                <div style={{ width: 34, height: 34, borderRadius: 8, display: "grid", placeItems: "center", background: "var(--mantine-color-default)" }}>
                  <ResourceGlyph addMethod={it.icon} iconKey={it.icon} size={20} />
                </div>
                <div style={{ minWidth: 0, flex: 1 }}>
                  <Group gap={6} wrap="nowrap">
                    <Text fw={600} size="sm" truncate>{it.label}</Text>
                    {it.custom && <Badge size="xs" variant="light" color="grape">Custom</Badge>}
                    {hidden && <Badge size="xs" variant="light" color="gray">Hidden</Badge>}
                  </Group>
                  <Text size="10px" c="dimmed">{it.group}</Text>
                </div>
                {isAdmin && (
                  <Tooltip label={hidden ? "Hidden from the store for all other users — click to show it again" : "Hide from the store — other users won't see or be able to install this (you still see it here, dimmed)"} withArrow multiline w={240}>
                    <ActionIcon variant="subtle" color={hidden ? "orange" : "gray"} size="sm" onClick={() => toggleExclude(it.id)} aria-label="Toggle store visibility">
                      {hidden ? <IconEyeOff size={15} /> : <IconEye size={15} />}
                    </ActionIcon>
                  </Tooltip>
                )}
              </Group>
              {it.description && <Text size="xs" c="dimmed" mt={8} lineClamp={2}>{it.description}</Text>}
              <Button fullWidth mt="sm" size="xs" leftSection={<IconDownload size={14} />}
                loading={installing === it.id} onClick={() => install(it)}>Install</Button>
            </Card>
            );
          })}
          {shown.length === 0 && <Text c="dimmed" size="sm">No apps match “{q}”.</Text>}
        </SimpleGrid>
      )}
    </Modal>
  );
}
