import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { AppShell, Container, Group, Title, Text, SimpleGrid, Card, Button, Badge, TextInput, Anchor, Loader } from "@mantine/core";
import { IconSearch, IconExternalLink, IconDownload } from "@tabler/icons-react";
import type { ContainerPreset, Deployment } from "../model";
import { buildPresetNodes } from "../model";
import { ResourceGlyph } from "../resourceIcons";
import * as api from "../api";
import logo from "../assets/logo.svg";
import { UserMenu } from "../auth/UserMenu";
import { useTitle } from "../useTitle";
import { toastOk, toastErr } from "../ui";

// Appliance / "app-store" home: browse curated apps, one-click install (= create a stack from the
// preset + deploy it to hosting), and see what's already running.
export function SimpleHome() {
  const nav = useNavigate();
  useTitle("Apps");
  const [presets, setPresets] = useState<ContainerPreset[]>([]);
  const [hosted, setHosted] = useState<Deployment[]>([]);
  const [q, setQ] = useState("");
  const [installing, setInstalling] = useState<string | null>(null);

  const loadHosted = () => api.listHosting().then(setHosted).catch(() => {});
  useEffect(() => { api.getPresets().then(setPresets).catch(() => {}); loadHosted(); const t = setInterval(loadHosted, 4000); return () => clearInterval(t); }, []);

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
      loadHosted();
    } catch (e) { toastErr(e, "Install failed"); }
    finally { setInstalling(null); }
  };

  const ql = q.toLowerCase();
  const shown = presets.filter(p => !ql || p.label.toLowerCase().includes(ql) || (p.group ?? "").toLowerCase().includes(ql));

  return (
    <AppShell header={{ height: 64 }} padding="lg">
      <AppShell.Header withBorder>
        <Container size="xl" h="100%">
          <Group h="100%" justify="space-between">
            <img src={logo} alt="AspireUI" height={40} />
            <Group>
              <Button variant="light" onClick={() => nav("/hosting")}>My apps</Button>
              <UserMenu />
            </Group>
          </Group>
        </Container>
      </AppShell.Header>
      <AppShell.Main>
        <Container size="xl">
          {hosted.length > 0 && (
            <>
              <Title order={5} mb="xs">Running</Title>
              <SimpleGrid cols={{ base: 1, sm: 2, md: 3 }} mb="xl">
                {hosted.map(d => (
                  <Card key={d.id} withBorder padding="sm" radius="md">
                    <Group justify="space-between">
                      <Text fw={600} size="sm" truncate>{d.name}</Text>
                      <Badge size="xs" variant="light" color={d.state === "running" ? "green" : d.state === "failed" ? "red" : "gray"}>{d.state}</Badge>
                    </Group>
                    <Group gap={6} mt={6}>
                      {d.urls[0] && <Anchor href={d.urls[0]} target="_blank" size="xs">Open <IconExternalLink size={11} /></Anchor>}
                      <Anchor size="xs" onClick={() => nav("/hosting")}>Manage</Anchor>
                    </Group>
                  </Card>
                ))}
              </SimpleGrid>
            </>
          )}

          <Group justify="space-between" mb="xs">
            <Title order={5}>App store</Title>
            <TextInput w={240} size="xs" placeholder="Search apps…" value={q} onChange={e => setQ(e.currentTarget.value)} leftSection={<IconSearch size={13} />} />
          </Group>
          {presets.length === 0 ? <Loader size="sm" /> : (
            <SimpleGrid cols={{ base: 1, sm: 2, md: 3, lg: 4 }}>
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
            </SimpleGrid>
          )}
        </Container>
      </AppShell.Main>
    </AppShell>
  );
}
