import { useEffect, useState } from "react";
import { Modal, Title, TextInput, SimpleGrid, Card, Group, Text, Button, Loader } from "@mantine/core";
import { IconSearch, IconDownload } from "@tabler/icons-react";
import type { ContainerPreset } from "../model";
import { buildPresetNodes } from "../model";
import { ResourceGlyph } from "../resourceIcons";
import * as api from "../api";
import { toastOk, toastErr } from "../ui";

// The app-store, as a modal. Installing = create a stack from the preset + deploy it to hosting.
export function InstallAppModal({ onClose, onInstalled }: { onClose: () => void; onInstalled: () => void }) {
  const [presets, setPresets] = useState<ContainerPreset[]>([]);
  const [q, setQ] = useState("");
  const [installing, setInstalling] = useState<string | null>(null);
  useEffect(() => { api.getPresets().then(setPresets).catch(() => setPresets([])); }, []);

  const install = async (p: ContainerPreset) => {
    setInstalling(p.id);
    try {
      const { nodes, edges } = buildPresetNodes(p, []);
      const stack = await api.createStack({
        name: p.label, targetFramework: "net10.0",
        nodes, edges, rawStatements: [], extraFiles: p.files ?? [], extraPackages: [],
      });
      await api.hostingDeploy(stack.id);
      toastOk(`Installing ${p.label}…`);
      onInstalled(); onClose();
    } catch (e) { toastErr(e, "Install failed"); }
    finally { setInstalling(null); }
  };

  const ql = q.toLowerCase();
  const shown = presets.filter(p => !ql || p.label.toLowerCase().includes(ql) || (p.group ?? "").toLowerCase().includes(ql));

  return (
    <Modal opened onClose={onClose} size="xl" title={<Title order={5}>App store</Title>}>
      <TextInput mb="md" placeholder="Search apps…" value={q} onChange={e => setQ(e.currentTarget.value)} leftSection={<IconSearch size={14} />} autoFocus />
      {presets.length === 0 ? <Loader size="sm" /> : (
        <SimpleGrid cols={{ base: 1, sm: 2, md: 3 }}>
          {shown.map(p => (
            <Card key={p.id} withBorder padding="md" radius="md">
              <Group gap={10} wrap="nowrap" align="flex-start">
                <div style={{ width: 34, height: 34, borderRadius: 8, display: "grid", placeItems: "center", background: "var(--mantine-color-default)" }}>
                  <ResourceGlyph addMethod={p.icon || ""} size={20} />
                </div>
                <div style={{ minWidth: 0, flex: 1 }}>
                  <Text fw={600} size="sm" truncate>{p.label}</Text>
                  <Text size="10px" c="dimmed">{p.group}</Text>
                </div>
              </Group>
              {p.description && <Text size="xs" c="dimmed" mt={8} lineClamp={2}>{p.description}</Text>}
              <Button fullWidth mt="sm" size="xs" leftSection={<IconDownload size={14} />}
                loading={installing === p.id} onClick={() => install(p)}>Install</Button>
            </Card>
          ))}
          {shown.length === 0 && <Text c="dimmed" size="sm">No apps match “{q}”.</Text>}
        </SimpleGrid>
      )}
    </Modal>
  );
}
