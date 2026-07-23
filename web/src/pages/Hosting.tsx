import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { AppShell, Group, Title, Button, Container, Table, Badge, Anchor, ActionIcon, Menu, Text, Modal, Stack, TextInput, Loader, Divider, Alert, ScrollArea } from "@mantine/core";
import { IconArrowLeft, IconDots, IconPlayerPlay, IconPlayerStop, IconTrash, IconExternalLink, IconPencil, IconRefresh, IconArchive, IconChevronRight, IconChevronDown, IconAdjustments, IconPlus, IconX, IconAlertTriangle, IconFileText } from "@tabler/icons-react";
import type { Deployment, ServiceStatus, NodeConfig } from "../model";
import * as api from "../api";
import { useTitle } from "../useTitle";
import { confirmDelete, toastOk, toastErr } from "../ui";

const color = (s: string) => s === "running" ? "green" : s === "failed" ? "red" : s === "deploying" ? "yellow" : "gray";

export function Hosting() {
  const nav = useNavigate();
  useTitle("Hosting");
  const [items, setItems] = useState<Deployment[]>([]);
  const [configFor, setConfigFor] = useState<Deployment | null>(null);
  const [logsFor, setLogsFor] = useState<Deployment | null>(null);
  const load = () => api.listHosting().then(setItems).catch(() => {});
  useEffect(() => { load(); const t = setInterval(load, 4000); return () => clearInterval(t); }, []);

  const stop = (d: Deployment) => api.stopHosting(d.stackId).then(load).catch(toastErr);
  const start = (d: Deployment) => api.startHosting(d.stackId).then(load).catch(toastErr);
  const update = (d: Deployment) => { toastOk(`Updating ${d.name}…`); api.updateHosting(d.stackId).then(load).then(() => toastOk("Updated")).catch(toastErr); };
  const backup = (d: Deployment) => { toastOk(`Backing up ${d.name}…`); api.backupHosting(d.stackId).then(r => toastOk(r.dir ? "Backup written" : "Nothing to back up")).catch(toastErr); };
  const undeploy = (d: Deployment) => confirmDelete(`"${d.name}"`, "This runs docker compose down (named volumes are kept).")
    .then(okd => { if (okd) api.undeployHosting(d.stackId).then(load).then(() => toastOk("Undeployed")).catch(toastErr); });

  return (
    <AppShell header={{ height: 56 }} padding="lg">
      <AppShell.Header>
        <Group h="100%" px="md">
          <Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={() => nav("/")}>Stacks</Button>
          <Title order={4}>Hosting</Title>
        </Group>
      </AppShell.Header>
      <AppShell.Main>
        <Container size="lg">
          {items.length === 0
            ? <Text c="dimmed" size="sm">No stacks deployed to hosting yet. Open a stack and choose <b>Deploy to hosting</b>.</Text>
            : (
            <Table verticalSpacing="sm">
              <Table.Thead><Table.Tr>
                <Table.Th w={30} /><Table.Th>App</Table.Th><Table.Th>Status</Table.Th><Table.Th>URLs</Table.Th><Table.Th /></Table.Tr></Table.Thead>
              <Table.Tbody>
                {items.map(d => (
                  <DeploymentRow key={d.id} d={d}
                    onStop={() => stop(d)} onStart={() => start(d)} onUpdate={() => update(d)}
                    onBackup={() => backup(d)} onUndeploy={() => undeploy(d)}
                    onConfigure={() => setConfigFor(d)} onOpenEditor={() => nav(`/editor/${d.stackId}`)}
                    onLogs={() => setLogsFor(d)} />
                ))}
              </Table.Tbody>
            </Table>)}
        </Container>
      </AppShell.Main>
      {configFor && <ConfigureModal d={configFor} onClose={() => setConfigFor(null)} onDone={load} />}
      {logsFor && <LogsModal d={logsFor} onClose={() => setLogsFor(null)} />}
    </AppShell>
  );
}

function DeploymentRow({ d, onStop, onStart, onUpdate, onBackup, onUndeploy, onConfigure, onOpenEditor, onLogs }: {
  d: Deployment; onStop: () => void; onStart: () => void; onUpdate: () => void;
  onBackup: () => void; onUndeploy: () => void; onConfigure: () => void; onOpenEditor: () => void; onLogs: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [svcs, setSvcs] = useState<ServiceStatus[] | null>(null);
  // Load (and keep fresh while open) the per-service status.
  useEffect(() => {
    if (!open) return;
    const load = () => api.hostingServices(d.id).then(setSvcs).catch(() => setSvcs([]));
    load(); const t = setInterval(load, 4000); return () => clearInterval(t);
  }, [open, d.id]);

  return (
    <>
      <Table.Tr>
        <Table.Td>
          <ActionIcon variant="subtle" size="sm" onClick={() => setOpen(o => !o)} aria-label="Expand resources">
            {open ? <IconChevronDown size={16} /> : <IconChevronRight size={16} />}
          </ActionIcon>
        </Table.Td>
        <Table.Td>{d.name}</Table.Td>
        <Table.Td><Badge color={color(d.state)} variant="light">{d.state}</Badge></Table.Td>
        <Table.Td>{d.urls.map(u => <Anchor key={u} href={u} target="_blank" mr="sm" size="sm">{u} <IconExternalLink size={12} /></Anchor>)}</Table.Td>
        <Table.Td>
          <Menu position="bottom-end" withArrow>
            <Menu.Target><ActionIcon variant="subtle" aria-label={`Actions for ${d.name}`}><IconDots size={16} /></ActionIcon></Menu.Target>
            <Menu.Dropdown>
              {d.state === "running"
                ? <Menu.Item leftSection={<IconPlayerStop size={14} />} onClick={onStop}>Stop</Menu.Item>
                : <Menu.Item leftSection={<IconPlayerPlay size={14} />} onClick={onStart}>Start</Menu.Item>}
              <Menu.Item leftSection={<IconAdjustments size={14} />} onClick={onConfigure}>Configure (env vars)</Menu.Item>
              <Menu.Item leftSection={<IconFileText size={14} />} onClick={onLogs}>View logs</Menu.Item>
              <Menu.Item leftSection={<IconRefresh size={14} />} onClick={onUpdate}>Update (pull &amp; recreate)</Menu.Item>
              <Menu.Item leftSection={<IconArchive size={14} />} onClick={onBackup}>Back up volumes</Menu.Item>
              <Menu.Item leftSection={<IconPencil size={14} />} onClick={onOpenEditor}>Open in editor</Menu.Item>
              <Menu.Divider />
              <Menu.Item color="red" leftSection={<IconTrash size={14} />} onClick={onUndeploy}>Undeploy</Menu.Item>
            </Menu.Dropdown>
          </Menu>
        </Table.Td>
      </Table.Tr>
      {open && (
        <Table.Tr>
          <Table.Td colSpan={5} p={0}>
            <div style={{ padding: "8px 16px 12px 46px" }}>
              {d.state === "failed" && d.lastError && (
                <Alert color="red" icon={<IconAlertTriangle size={14} />} p="xs" mb="xs" title="Deploy failed">
                  <Text size="xs" style={{ whiteSpace: "pre-wrap", fontFamily: "monospace", maxHeight: 160, overflow: "auto" }}>{d.lastError.trim().split("\n").slice(-12).join("\n")}</Text>
                  <Anchor size="xs" onClick={onLogs}>Full logs</Anchor>
                </Alert>
              )}
              {svcs === null ? <Loader size="xs" />
                : svcs.length === 0 ? <Text size="xs" c="dimmed">No running containers (start the app to see its resources).</Text>
                : (
                <Table withRowBorders={false} verticalSpacing={4} fz="xs">
                  <Table.Tbody>
                    {svcs.map(s => {
                      const port = s.ports.split(",")[0]?.trim().split(":")[0];
                      const url = port && /^\d+$/.test(port) && !s.service.includes("dashboard") ? `http://${window.location.hostname}:${port}` : null;
                      return (
                      <Table.Tr key={s.name}>
                        <Table.Td w={12}><span style={{ display: "inline-block", width: 8, height: 8, borderRadius: 8, background: `var(--mantine-color-${color(s.state)}-6)` }} /></Table.Td>
                        <Table.Td fw={500}>{s.service || s.name}</Table.Td>
                        <Table.Td c="dimmed">{s.image}</Table.Td>
                        <Table.Td>{url ? <Anchor href={url} target="_blank">{s.ports} <IconExternalLink size={10} /></Anchor> : <Text c="dimmed" span>{s.ports}</Text>}</Table.Td>
                        <Table.Td c="dimmed">{s.status}</Table.Td>
                      </Table.Tr>
                      );
                    })}
                  </Table.Tbody>
                </Table>)}
            </div>
          </Table.Td>
        </Table.Tr>
      )}
    </>
  );
}

// Edit each resource's (literal) environment variables, then stop → redeploy. Parameter-backed secrets
// aren't shown here (they live in the builder); this is the simple "app settings" surface.
function ConfigureModal({ d, onClose, onDone }: { d: Deployment; onClose: () => void; onDone: () => void }) {
  const [cfg, setCfg] = useState<NodeConfig[] | null>(null);
  const [env, setEnv] = useState<Record<string, string[][]>>({});
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    api.hostingConfig(d.stackId).then(c => {
      setCfg(c);
      setEnv(Object.fromEntries(c.map(n => [n.nodeId, n.env.map(p => [...p])])));
    }).catch(() => setCfg([]));
  }, [d.stackId]);

  const setPair = (id: string, i: number, which: 0 | 1, val: string) =>
    setEnv(e => ({ ...e, [id]: e[id].map((p, j) => j === i ? (which === 0 ? [val, p[1]] : [p[0], val]) : p) }));
  const addPair = (id: string) => setEnv(e => ({ ...e, [id]: [...(e[id] ?? []), ["", ""]] }));
  const delPair = (id: string, i: number) => setEnv(e => ({ ...e, [id]: e[id].filter((_, j) => j !== i) }));

  const save = async () => {
    setSaving(true);
    try {
      // drop empty keys
      const clean = Object.fromEntries(Object.entries(env).map(([k, v]) => [k, v.filter(p => p[0].trim())]));
      await api.reconfigureHosting(d.stackId, clean);
      toastOk("Saved — redeploying…");
      onDone(); onClose();
    } catch (e) { toastErr(e, "Save failed"); }
    finally { setSaving(false); }
  };

  return (
    <Modal opened onClose={onClose} size="lg" title={<Title order={5}>Configure {d.name}</Title>}>
      {cfg === null ? <Loader size="sm" /> : (
        <Stack gap="md">
          <Alert color="yellow" icon={<IconAlertTriangle size={16} />} p="xs">
            Saving stops the app, applies your changes and redeploys it. Brief downtime.
          </Alert>
          <ScrollArea.Autosize mah={420}>
            <Stack gap="lg">
              {cfg.map(n => (
                <div key={n.nodeId}>
                  <Group gap={6} mb={4}>
                    <Text fw={600} size="sm">{n.name}</Text>
                    {n.image && <Text size="xs" c="dimmed">{n.image}</Text>}
                  </Group>
                  <Stack gap={6}>
                    {(env[n.nodeId] ?? []).map((p, i) => (
                      <Group key={i} gap={6} wrap="nowrap">
                        <TextInput size="xs" placeholder="KEY" value={p[0]} onChange={e => setPair(n.nodeId, i, 0, e.currentTarget.value)} style={{ flex: "0 0 40%" }} styles={{ input: { fontFamily: "monospace" } }} />
                        <TextInput size="xs" placeholder="value" value={p[1]} onChange={e => setPair(n.nodeId, i, 1, e.currentTarget.value)} style={{ flex: 1 }} styles={{ input: { fontFamily: "monospace" } }} />
                        <ActionIcon variant="subtle" color="red" size="sm" onClick={() => delPair(n.nodeId, i)} aria-label="Remove"><IconX size={14} /></ActionIcon>
                      </Group>
                    ))}
                    <Button variant="subtle" size="compact-xs" leftSection={<IconPlus size={12} />} onClick={() => addPair(n.nodeId)} style={{ alignSelf: "flex-start" }}>Add variable</Button>
                  </Stack>
                  <Divider mt="sm" />
                </div>
              ))}
              {cfg.length === 0 && <Text size="sm" c="dimmed">No configurable resources.</Text>}
            </Stack>
          </ScrollArea.Autosize>
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose}>Cancel</Button>
            <Button loading={saving} onClick={save}>Save &amp; redeploy</Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}

// Streams `docker compose logs` for the deployment (the server pushes them over SSE).
function LogsModal({ d, onClose }: { d: Deployment; onClose: () => void }) {
  const [lines, setLines] = useState<string[]>([]);
  useEffect(() => {
    const es = new EventSource(api.hostingLogsUrl(d.id));
    es.onmessage = e => setLines(l => (l.length > 2000 ? l.slice(-2000) : l).concat(e.data));
    es.onerror = () => es.close();
    return () => es.close();
  }, [d.id]);
  return (
    <Modal opened onClose={onClose} size="xl" title={<Title order={5}>Logs · {d.name}</Title>}>
      <ScrollArea.Autosize mah={520}>
        <pre style={{ margin: 0, fontSize: 11, whiteSpace: "pre-wrap", wordBreak: "break-all" }}>
          {lines.length ? lines.join("\n") : "…"}
        </pre>
      </ScrollArea.Autosize>
    </Modal>
  );
}
