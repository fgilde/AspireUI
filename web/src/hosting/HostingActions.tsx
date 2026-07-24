import { useEffect, useMemo, useRef, useState } from "react";
import { Menu, Modal, Title, Stack, TextInput, NumberInput, Switch, Loader, Divider, Alert, ScrollArea, Group, Button, ActionIcon, Text, Tooltip, CopyButton } from "@mantine/core";
import { IconPlayerPlay, IconPlayerStop, IconTrash, IconPencil, IconRefresh, IconArchive, IconAdjustments, IconPlus, IconX, IconAlertTriangle, IconFileText, IconSearch, IconDownload, IconUpload, IconCopy, IconCheck, IconMaximize, IconMinimize, IconArrowBackUp } from "@tabler/icons-react";
import type { Deployment, NodeConfig, PortMapping, BackupInfo } from "../model";
import * as api from "../api";
import { confirmDelete, toastOk, toastErr } from "../ui";

// Border/chip color for a stack's hosting state — shared so cards + badges stay consistent.
export const hostingColor = (s: string) => s === "running" ? "green" : s === "failed" ? "red" : s === "deploying" ? "yellow" : "gray";

// The hosting action menu items — identical everywhere (the /Hosting rows AND the stack cards) so a
// change here shows up in both. `onChanged` re-loads the caller after an action; `onConfigure`/`onLogs`
// open the shared modals (owned by the caller so the menu can unmount).
export function HostingMenuItems({ d, canEdit, onConfigure, onLogs, onBackups, onOpenEditor, onChanged }: {
  d: Deployment; canEdit: boolean; onConfigure: () => void; onLogs: () => void; onBackups?: () => void; onOpenEditor?: () => void; onChanged: () => void;
}) {
  const stop = () => { toastOk(`Stopping ${d.name}…`); api.stopHosting(d.stackId).then(onChanged).catch(toastErr); };
  const start = () => { toastOk(`${d.state === "failed" ? "Retrying" : "Starting"} ${d.name}…`); api.startHosting(d.stackId).then(onChanged).catch(toastErr); };
  const update = () => { toastOk(`Updating ${d.name}…`); api.updateHosting(d.stackId).then(onChanged).then(() => toastOk("Updated")).catch(toastErr); };
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
      {onBackups && <Menu.Item leftSection={<IconArchive size={14} />} onClick={onBackups}>Backups…</Menu.Item>}
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
  const [q, setQ] = useState("");
  const [ports, setPorts] = useState<PortMapping[]>(d.ports ?? []);
  const fileRef = useRef<HTMLInputElement>(null);

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
      const portChanged = JSON.stringify(ports) !== JSON.stringify(d.ports ?? []);
      await api.reconfigureHosting(d.stackId, clean, portChanged ? ports : undefined);
      toastOk("Saved — redeploying…");
      onDone(); onClose();
    } catch (e) { toastErr(e, "Save failed"); }
    finally { setSaving(false); }
  };

  // Export all resources' env as a `.env` with `# <resource>` section headers (round-trips with import).
  const exportEnv = () => {
    const cfgList = cfg ?? [];
    const text = cfgList.map(n => `# ${n.name}\n` +
      (env[n.nodeId] ?? []).filter(p => p[0].trim()).map(([k, v]) => `${k}=${v}`).join("\n")).join("\n\n") + "\n";
    const url = URL.createObjectURL(new Blob([text], { type: "text/plain" }));
    const a = document.createElement("a"); a.href = url; a.download = `${d.name}.env`; a.click(); URL.revokeObjectURL(url);
  };
  // Import a `.env`: lines routed to the resource named by the nearest `# <resource>` header (a flat
  // file with a single resource applies to it). Existing keys are updated, new ones appended.
  const importEnv = (text: string) => {
    const cfgList = cfg ?? [];
    const nameToId = Object.fromEntries(cfgList.map(n => [n.name.toLowerCase(), n.nodeId]));
    let curId: string | null = cfgList.length === 1 ? cfgList[0].nodeId : null;
    let applied = 0;
    setEnv(prev => {
      const next: Record<string, string[][]> = Object.fromEntries(Object.entries(prev).map(([k, v]) => [k, v.map(p => [...p])]));
      for (const raw of text.split(/\r?\n/)) {
        const line = raw.trim();
        if (!line) continue;
        const h = line.match(/^#\s*(.+)$/);
        if (h) { curId = nameToId[h[1].trim().toLowerCase()] ?? curId; continue; }
        const m = line.match(/^(?:export\s+)?([A-Za-z_][A-Za-z0-9_.]*)\s*=\s*(.*)$/);
        if (!m || !curId) continue;
        const key = m[1], val = m[2].replace(/^["']|["']$/g, "");
        const arr = next[curId] ??= [];
        const i = arr.findIndex(p => p[0] === key);
        if (i >= 0) arr[i] = [key, val]; else arr.push([key, val]);
        applied++;
      }
      return next;
    });
    toastOk(applied ? `Imported ${applied} variable(s) — review, then Save` : "No variables found in file");
  };
  const onFile = (e: React.ChangeEvent<HTMLInputElement>) => {
    const f = e.target.files?.[0]; e.target.value = "";
    if (f) f.text().then(importEnv).catch(err => toastErr(err, "Could not read file"));
  };

  return (
    <Modal opened onClose={onClose} size="lg" title={<Title order={5}>Configure {d.name}</Title>}>
      {cfg === null ? <Loader size="sm" /> : (
        <Stack gap="md">
          <Alert color="yellow" icon={<IconAlertTriangle size={16} />} p="xs">
            Saving stops the app, applies your changes and redeploys it. Brief downtime.
          </Alert>
          <Group gap="xs" wrap="nowrap">
            <TextInput size="xs" style={{ flex: 1 }} placeholder="Filter variables…" value={q}
              onChange={e => setQ(e.currentTarget.value)} leftSection={<IconSearch size={13} />} />
            <Tooltip label="Import .env" withArrow><ActionIcon variant="default" onClick={() => fileRef.current?.click()} aria-label="Import .env"><IconUpload size={15} /></ActionIcon></Tooltip>
            <Tooltip label="Export .env" withArrow><ActionIcon variant="default" onClick={exportEnv} aria-label="Export .env"><IconDownload size={15} /></ActionIcon></Tooltip>
            <input ref={fileRef} type="file" accept=".env,text/plain,.txt" hidden onChange={onFile} />
          </Group>
          {ports.length > 0 && !q && (
            <div>
              <Text fw={600} size="sm" mb={4}>Ports</Text>
              <Stack gap={6}>
                {ports.map((pm, i) => (
                  <Group key={pm.container} gap={10} wrap="nowrap">
                    <Text size="xs" ff="monospace" style={{ width: 96 }}>:{pm.container}</Text>
                    <NumberInput size="xs" style={{ width: 120 }} value={pm.public ? (pm.host || undefined) : undefined}
                      placeholder={pm.public ? "auto" : "—"} disabled={!pm.public} min={1} max={65535} hideControls
                      onChange={v => setPorts(ps => ps.map((p, j) => j === i ? { ...p, host: Number(v) || 0 } : p))} />
                    <Switch size="xs" checked={pm.public} label={pm.public ? "public" : "internal"}
                      onChange={e => setPorts(ps => ps.map((p, j) => j === i ? { ...p, public: e.currentTarget.checked } : p))} />
                  </Group>
                ))}
              </Stack>
              <Text size="10px" c="dimmed" mt={4}>Pin a host port (blank = auto), or make a port <b>internal</b> — reachable only inside the app, not from the host. Applied on save. A taken port falls back to auto.</Text>
              <Divider mt="sm" />
            </div>
          )}
          <ScrollArea.Autosize mah={420}>
            <Stack gap="lg">
              {cfg.map(n => {
                const ql = q.toLowerCase();
                const rows = (env[n.nodeId] ?? []).map((p, i) => ({ p, i }))
                  .filter(({ p }) => !ql || `${p[0]} ${p[1]}`.toLowerCase().includes(ql));
                if (ql && rows.length === 0) return null;
                return (
                <div key={n.nodeId}>
                  <Group gap={6} mb={4}>
                    <Text fw={600} size="sm">{n.name}</Text>
                    {n.image && <Text size="xs" c="dimmed">{n.image}</Text>}
                  </Group>
                  <Stack gap={6}>
                    {rows.map(({ p, i }) => (
                      <Group key={i} gap={6} wrap="nowrap">
                        <TextInput size="xs" placeholder="KEY" value={p[0]} onChange={e => setPair(n.nodeId, i, 0, e.currentTarget.value)} style={{ flex: "0 0 40%" }} styles={{ input: { fontFamily: "monospace" } }} />
                        <TextInput size="xs" placeholder="value" value={p[1]} onChange={e => setPair(n.nodeId, i, 1, e.currentTarget.value)} style={{ flex: 1 }} styles={{ input: { fontFamily: "monospace" } }} />
                        <ActionIcon variant="subtle" color="red" size="sm" onClick={() => delPair(n.nodeId, i)} aria-label="Remove"><IconX size={14} /></ActionIcon>
                      </Group>
                    ))}
                    {!ql && <Button variant="subtle" size="compact-xs" leftSection={<IconPlus size={12} />} onClick={() => addPair(n.nodeId)} style={{ alignSelf: "flex-start" }}>Add variable</Button>}
                  </Stack>
                  <Divider mt="sm" />
                </div>
                );
              })}
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

const fmtSize = (n: number) => n >= 1048576 ? `${(n / 1048576).toFixed(1)} MB` : n >= 1024 ? `${Math.round(n / 1024)} KB` : `${n} B`;

// Volume-backup manager: create a snapshot, then list / restore / download / delete existing ones.
// Backups are per-volume .tgz archives kept in the AspireUI workspace (server-side).
export function BackupsModal({ d, onClose, onChanged }: { d: Deployment; onClose: () => void; onChanged?: () => void }) {
  const [list, setList] = useState<BackupInfo[] | null>(null);
  const [busy, setBusy] = useState(false);
  const load = () => api.listBackups(d.stackId).then(setList).catch(() => setList([]));
  useEffect(() => { load(); }, [d.stackId]);   // eslint-disable-line react-hooks/exhaustive-deps

  const create = async () => {
    setBusy(true);
    try { const r = await api.backupHosting(d.stackId); toastOk(r.dir ? "Backup created" : "Nothing to back up — this app has no named volumes"); load(); }
    catch (e) { toastErr(e, "Backup failed"); } finally { setBusy(false); }
  };
  const restore = (stamp: string) =>
    confirmDelete(`restore "${d.name}" to this snapshot`, "The app stops, its current volume data is REPLACED with the snapshot, then it restarts. Current data is overwritten — cannot be undone.")
      .then(okd => { if (okd) { toastOk("Restoring…"); api.restoreBackup(d.stackId, stamp).then(() => { onChanged?.(); toastOk("Restored"); load(); }).catch(e => toastErr(e, "Restore failed")); } });
  const del = (stamp: string) =>
    confirmDelete(`this backup (${stamp})`, "Deletes the snapshot archives from disk.")
      .then(okd => { if (okd) api.deleteBackup(d.stackId, stamp).then(load).catch(toastErr); });

  return (
    <Modal opened onClose={onClose} size="lg" title={<Title order={5}>Backups · {d.name}</Title>}>
      <Stack gap="md">
        <Group justify="space-between">
          <Text size="xs" c="dimmed">Snapshots of this app's named volumes (database, files). Kept on the AspireUI host.</Text>
          <Button size="xs" leftSection={<IconArchive size={14} />} loading={busy} onClick={create}>Back up now</Button>
        </Group>
        {list === null ? <Loader size="sm" /> : list.length === 0 ? (
          <Text size="sm" c="dimmed">No backups yet. Use “Back up now” to create one.</Text>
        ) : (
          <ScrollArea.Autosize mah={420}>
            <Stack gap="xs">
              {list.map(b => (
                <Group key={b.stamp} justify="space-between" wrap="nowrap" p="xs"
                  style={{ border: "1px solid var(--mantine-color-default-border)", borderRadius: 8 }}>
                  <div style={{ minWidth: 0 }}>
                    <Text size="sm" fw={600}>{new Date(b.createdAt).toLocaleString()}</Text>
                    <Text size="xs" c="dimmed" truncate>
                      {b.volumes.map(v => `${v.name} (${fmtSize(v.size)})`).join(" · ")}
                    </Text>
                  </div>
                  <Group gap={4} wrap="nowrap">
                    <Tooltip label="Restore this snapshot" withArrow><ActionIcon variant="subtle" color="orange" onClick={() => restore(b.stamp)} aria-label="Restore"><IconArrowBackUp size={16} /></ActionIcon></Tooltip>
                    <Tooltip label="Download (.zip)" withArrow><ActionIcon variant="subtle" color="gray" component="a" href={api.backupDownloadUrl(d.stackId, b.stamp)} aria-label="Download"><IconDownload size={16} /></ActionIcon></Tooltip>
                    <Tooltip label="Delete" withArrow><ActionIcon variant="subtle" color="red" onClick={() => del(b.stamp)} aria-label="Delete"><IconTrash size={16} /></ActionIcon></Tooltip>
                  </Group>
                </Group>
              ))}
            </Stack>
          </ScrollArea.Autosize>
        )}
      </Stack>
    </Modal>
  );
}
