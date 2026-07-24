import { useEffect, useMemo, useState } from "react";
import { Menu, Modal, Title, Stack, TextInput, Loader, Divider, Alert, ScrollArea, Group, Button, ActionIcon, Text, Tooltip, CopyButton } from "@mantine/core";
import { IconPlayerPlay, IconPlayerStop, IconTrash, IconPencil, IconRefresh, IconArchive, IconAdjustments, IconPlus, IconX, IconAlertTriangle, IconFileText, IconSearch, IconDownload, IconCopy, IconCheck, IconMaximize, IconMinimize } from "@tabler/icons-react";
import type { Deployment, NodeConfig } from "../model";
import * as api from "../api";
import { confirmDelete, toastOk, toastErr } from "../ui";

// Border/chip color for a stack's hosting state — shared so cards + badges stay consistent.
export const hostingColor = (s: string) => s === "running" ? "green" : s === "failed" ? "red" : s === "deploying" ? "yellow" : "gray";

// The hosting action menu items — identical everywhere (the /Hosting rows AND the stack cards) so a
// change here shows up in both. `onChanged` re-loads the caller after an action; `onConfigure`/`onLogs`
// open the shared modals (owned by the caller so the menu can unmount).
export function HostingMenuItems({ d, canEdit, onConfigure, onLogs, onOpenEditor, onChanged }: {
  d: Deployment; canEdit: boolean; onConfigure: () => void; onLogs: () => void; onOpenEditor?: () => void; onChanged: () => void;
}) {
  const stop = () => { toastOk(`Stopping ${d.name}…`); api.stopHosting(d.stackId).then(onChanged).catch(toastErr); };
  const start = () => { toastOk(`${d.state === "failed" ? "Retrying" : "Starting"} ${d.name}…`); api.startHosting(d.stackId).then(onChanged).catch(toastErr); };
  const update = () => { toastOk(`Updating ${d.name}…`); api.updateHosting(d.stackId).then(onChanged).then(() => toastOk("Updated")).catch(toastErr); };
  const backup = () => { toastOk(`Backing up ${d.name}…`); api.backupHosting(d.stackId).then(r => toastOk(r.dir ? "Backup written" : "Nothing to back up")).catch(toastErr); };
  const undeploy = () => confirmDelete(`"${d.name}"`, "This runs docker compose down (named volumes are KEPT — data survives).")
    .then(okd => { if (okd) api.undeployHosting(d.stackId).then(onChanged).then(() => toastOk("Undeployed")).catch(toastErr); });
  const wipe = () => confirmDelete(`"${d.name}" AND its data`, "This runs docker compose down -v — the app's named volumes (database, files) are DELETED. Use this to cleanly reinstall an app that got stuck half-initialized. Cannot be undone.")
    .then(okd => { if (okd) api.undeployHosting(d.stackId, true).then(onChanged).then(() => toastOk("Undeployed + data wiped")).catch(toastErr); });
  return (
    <>
      {d.state === "running"
        ? <Menu.Item leftSection={<IconPlayerStop size={14} />} onClick={stop}>Stop</Menu.Item>
        : d.state === "deploying"
        ? <Menu.Item leftSection={<Loader size={12} />} disabled>Deploying…</Menu.Item>
        : <Menu.Item leftSection={<IconPlayerPlay size={14} />} onClick={start}>{d.state === "failed" ? "Retry" : "Start"}</Menu.Item>}
      <Menu.Item leftSection={<IconAdjustments size={14} />} onClick={onConfigure}>Configure (env vars)</Menu.Item>
      <Menu.Item leftSection={<IconFileText size={14} />} onClick={() => onLogs()}>View logs</Menu.Item>
      <Menu.Item leftSection={<IconRefresh size={14} />} onClick={update}>Update (pull &amp; recreate)</Menu.Item>
      <Menu.Item leftSection={<IconArchive size={14} />} onClick={backup}>Back up volumes</Menu.Item>
      {onOpenEditor && canEdit && <Menu.Item leftSection={<IconPencil size={14} />} onClick={onOpenEditor}>Open in editor</Menu.Item>}
      <Menu.Divider />
      <Menu.Item color="red" leftSection={<IconTrash size={14} />} onClick={undeploy}>Undeploy</Menu.Item>
      <Menu.Item color="red" leftSection={<IconTrash size={14} />} onClick={wipe}>Undeploy + delete data</Menu.Item>
    </>
  );
}

// Edit each resource's (literal) environment variables, then stop → redeploy. Parameter-backed secrets
// aren't shown here (they live in the builder); this is the simple "app settings" surface.
export function ConfigureModal({ d, onClose, onDone }: { d: Deployment; onClose: () => void; onDone: () => void }) {
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

// Streams `docker compose logs` for the deployment (all services / children, prefixed with the service
// name) over SSE. Searchable, copyable, downloadable, and can go fullscreen. `service` pre-filters to
// one child.
export function LogsModal({ d, onClose, service }: { d: Deployment; onClose: () => void; service?: string }) {
  const [lines, setLines] = useState<string[]>([]);
  const [q, setQ] = useState(typeof service === "string" ? service : "");
  const [full, setFull] = useState(false);
  useEffect(() => {
    const es = new EventSource(api.hostingLogsUrl(d.id));
    es.onmessage = e => setLines(l => (l.length > 5000 ? l.slice(-5000) : l).concat(e.data));
    es.onerror = () => es.close();
    return () => es.close();
  }, [d.id]);
  const ql = q.toLowerCase();
  const shown = useMemo(() => ql ? lines.filter(l => l.toLowerCase().includes(ql)) : lines, [lines, ql]);
  const text = shown.join("\n");
  const download = () => {
    const url = URL.createObjectURL(new Blob([text], { type: "text/plain" }));
    const a = document.createElement("a"); a.href = url; a.download = `${d.name}-logs.txt`; a.click(); URL.revokeObjectURL(url);
  };
  return (
    <Modal opened onClose={onClose} fullScreen={full} size={full ? undefined : "80%"}
      title={<Title order={5}>Logs · {d.name}</Title>}>
      <Stack gap="xs">
        <Group gap="xs" wrap="nowrap">
          <TextInput size="xs" placeholder="Filter (e.g. a service name)…" value={q} onChange={e => setQ(e.currentTarget.value)}
            leftSection={<IconSearch size={13} />} style={{ flex: 1 }} />
          <Text size="xs" c="dimmed" style={{ whiteSpace: "nowrap" }}>{shown.length}/{lines.length}</Text>
          <CopyButton value={text}>{({ copied, copy }) => (
            <Tooltip label={copied ? "Copied" : "Copy"} withArrow><ActionIcon variant="subtle" color={copied ? "green" : "gray"} onClick={copy} aria-label="Copy logs">{copied ? <IconCheck size={16} /> : <IconCopy size={16} />}</ActionIcon></Tooltip>)}
          </CopyButton>
          <Tooltip label="Download" withArrow><ActionIcon variant="subtle" color="gray" onClick={download} aria-label="Download logs"><IconDownload size={16} /></ActionIcon></Tooltip>
          <Tooltip label={full ? "Exit fullscreen" : "Fullscreen"} withArrow><ActionIcon variant="subtle" color="gray" onClick={() => setFull(f => !f)} aria-label="Toggle fullscreen">{full ? <IconMinimize size={16} /> : <IconMaximize size={16} />}</ActionIcon></Tooltip>
        </Group>
        <ScrollArea.Autosize mah={full ? "calc(100vh - 160px)" : 520} style={{ background: "var(--mantine-color-default)", borderRadius: 6 }}>
          <pre style={{ margin: 0, padding: 10, fontSize: 11, whiteSpace: "pre-wrap", wordBreak: "break-all" }}>
            {shown.length ? text : "…"}
          </pre>
        </ScrollArea.Autosize>
      </Stack>
    </Modal>
  );
}
