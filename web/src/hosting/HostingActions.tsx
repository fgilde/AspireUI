import { useEffect, useMemo, useRef, useState } from "react";
import { Menu, Modal, Title, Stack, TextInput, NumberInput, Switch, Select, Loader, Divider, Alert, ScrollArea, Group, Button, ActionIcon, Text, Tooltip, CopyButton, Badge, Anchor } from "@mantine/core";
import { IconPlayerPlay, IconPlayerStop, IconTrash, IconPencil, IconRefresh, IconReload, IconArchive, IconAdjustments, IconPlus, IconX, IconAlertTriangle, IconFileText, IconSearch, IconDownload, IconUpload, IconCopy, IconCheck, IconMaximize, IconMinimize, IconArrowBackUp, IconWorld, IconTerminal2, IconFolder, IconFolderOpen, IconFile, IconDatabase, IconArrowsExchange } from "@tabler/icons-react";
import type { Deployment, NodeConfig, PortMapping, BackupInfo, DomainInfo } from "../model";
import * as api from "../api";
import { confirmDelete, toastOk, toastErr } from "../ui";

// Border/chip color for a stack's hosting state; shared so cards & badges stay consistent.
export const hostingColor = (s: string) => s === "running" ? "green" : s === "failed" ? "red" : s === "deploying" ? "yellow" : "gray";

// Colour for a deployment as a whole: a running app whose containers crash-loop is not green.
export const deploymentColor = (d?: { state: string; health?: string | null } | null) =>
  !d ? "gray"
    : d.state === "running" && d.health === "failing" ? "red"
    : d.state === "running" && d.health === "unhealthy" ? "orange"
    : d.state === "running" && d.health === "starting" ? "yellow"
    : hostingColor(d.state);

// Menu items shown everywhere; onChanged reloads caller; onConfigure/onLogs open shared modals.
export function HostingMenuItems({ d, canEdit, onConfigure, onLogs, onBackups, onDomain, onTerminal, onFiles, onOpenEditor, onMove, onChanged }: {
  d: Deployment; canEdit: boolean; onConfigure: () => void; onLogs: () => void; onBackups?: () => void; onDomain?: () => void; onTerminal?: () => void; onFiles?: () => void; onOpenEditor?: () => void; onMove?: () => void; onChanged: () => void;
}) {
  const stop = () => { toastOk(`Stopping ${d.name}…`); api.stopHosting(d.stackId).then(onChanged).catch(toastErr); };
  const start = () => { toastOk(`${d.state === "failed" ? "Retrying" : "Starting"} ${d.name}…`); api.startHosting(d.stackId).then(onChanged).catch(toastErr); };
  const restart = () => { toastOk(`Restarting ${d.name}…`); api.restartHosting(d.stackId).then(onChanged).then(() => toastOk("Restarted")).catch(toastErr); };
  const update = () => { toastOk(`Updating ${d.name}…`); api.updateHosting(d.stackId).then(onChanged).then(() => toastOk("Updated")).catch(toastErr); };
  const checkUpdates = () => { toastOk(`Checking ${d.name} for updates…`); api.checkUpdates(d.stackId)
    .then(r => toastOk(r.anyUpdate ? `Update available (${r.images.filter(i => i.updateAvailable).map(i => i.image).join(", ")}) — use Update to apply` : "Everything up to date"))
    .catch(toastErr); };
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
      {d.state === "running" && <Menu.Item leftSection={<IconReload size={14} />} onClick={restart}>Restart</Menu.Item>}
      <Menu.Item leftSection={<IconAdjustments size={14} />} onClick={onConfigure}>Configure (env vars)</Menu.Item>
      <Menu.Item leftSection={<IconFileText size={14} />} onClick={() => onLogs()}>View logs</Menu.Item>
      {/* Also when stopped or crash-looping: that is exactly when a repair command is needed. */}
      {onTerminal && <Menu.Item leftSection={<IconTerminal2 size={14} />} onClick={onTerminal}>Terminal…</Menu.Item>}
      <Menu.Item leftSection={<IconSearch size={14} />} onClick={checkUpdates}>Check for updates</Menu.Item>
      <Menu.Item leftSection={<IconRefresh size={14} />} onClick={update}>Update (pull &amp; recreate)</Menu.Item>
      {/* A target without a docker socket has no volumes to browse and no compose shell. */}
      {onFiles && d.targetCompose !== false && <Menu.Item leftSection={<IconDatabase size={14} />} onClick={onFiles}>Files (volumes)…</Menu.Item>}
      {onMove && <Menu.Item leftSection={<IconArrowsExchange size={14} />} onClick={onMove}>Move or copy to another target…</Menu.Item>}
      {onBackups && <Menu.Item leftSection={<IconArchive size={14} />} onClick={onBackups}>Backups…</Menu.Item>}
      {onDomain && <Menu.Item leftSection={<IconWorld size={14} />} onClick={onDomain}>Domain (proxy)…</Menu.Item>}
      {onOpenEditor && canEdit && <Menu.Item leftSection={<IconPencil size={14} />} onClick={onOpenEditor}>Open in editor</Menu.Item>}
      <Menu.Divider />
      <Menu.Item color="red" leftSection={<IconTrash size={14} />} onClick={undeploy}>Undeploy</Menu.Item>
      <Menu.Item color="red" leftSection={<IconTrash size={14} />} onClick={wipe}>Undeploy + delete data</Menu.Item>
    </>
  );
}

// Edit resources' environment variables; stops & redeploys. Parameter-backed secrets live in builder.
export function ConfigureModal({ d, onClose, onDone }: { d: Deployment; onClose: () => void; onDone: () => void }) {
  const [cfg, setCfg] = useState<NodeConfig[] | null>(null);
  const [env, setEnv] = useState<Record<string, string[][]>>({});
  const [saving, setSaving] = useState(false);
  const [q, setQ] = useState("");
  const [ports, setPorts] = useState<PortMapping[]>(d.ports ?? []);
  const [domainOpen, setDomainOpen] = useState(false);
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

  const exportEnv = () => {
    const cfgList = cfg ?? [];
    const text = cfgList.map(n => `# ${n.name}\n` +
      (env[n.nodeId] ?? []).filter(p => p[0].trim()).map(([k, v]) => `${k}=${v}`).join("\n")).join("\n\n") + "\n";
    const url = URL.createObjectURL(new Blob([text], { type: "text/plain" }));
    const a = document.createElement("a"); a.href = url; a.download = `${d.name}.env`; a.click(); URL.revokeObjectURL(url);
  };
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
                      onChange={e => { const on = e.currentTarget.checked; setPorts(ps => ps.map((p, j) => j === i ? { ...p, public: on } : p)); }} />
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
          <Group justify="space-between">
            <Button variant="subtle" color="gray" leftSection={<IconWorld size={15} />} onClick={() => setDomainOpen(true)}>Domain / reverse proxy…</Button>
            <Group>
              <Button variant="default" onClick={onClose}>Cancel</Button>
              <Button loading={saving} onClick={save}>Save &amp; redeploy</Button>
            </Group>
          </Group>
        </Stack>
      )}
      {domainOpen && <DomainModal d={d} onClose={() => setDomainOpen(false)} />}
    </Modal>
  );
}

// Streams docker compose logs (all services w/ name prefix) over SSE; searchable, copyable, fullscreen.
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

// Volume-backup manager: snapshots as per-volume .tgz archives in AspireUI workspace.
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

// Configure real domain via Nginx Proxy Manager; shows/edits existing proxy-host entry.
export function DomainModal({ d, onClose }: { d: Deployment; onClose: () => void }) {
  const [info, setInfo] = useState<DomainInfo | null>(null);
  const [domains, setDomains] = useState("");
  const [scheme, setScheme] = useState("http");
  const [host, setHost] = useState("");
  const [port, setPort] = useState(0);
  const [ws, setWs] = useState(true);
  const [ssl, setSsl] = useState(false);
  const [enabled, setEnabled] = useState(true);
  const [saving, setSaving] = useState(false);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    api.getDomain(d.stackId).then(i => {
      setInfo(i);
      if (i.existing) { setDomains(i.existing.domainNames.join(", ")); setScheme(i.existing.forwardScheme); setHost(i.existing.forwardHost); setPort(i.existing.forwardPort); setWs(i.existing.websockets); setEnabled(i.existing.enabled); setSsl(!!i.existing.sslForced); }
      else if (i.proposal) { setScheme(i.proposal.scheme); setHost(i.proposal.forwardHost); setPort(i.proposal.forwardPort); setWs(i.proposal.websockets); }
    }).catch(() => setInfo({ configured: false }));
  }, [d.stackId]);

  const toggleEnabled = async (v: boolean) => {
    if (!info?.existing) return;
    setBusy(true);
    try { await api.setDomainEnabled(d.stackId, info.existing.id, v); setEnabled(v); toastOk(v ? "Proxy host enabled" : "Proxy host disabled"); }
    catch (e) { toastErr(e, "Could not change state"); } finally { setBusy(false); }
  };
  const del = async () => {
    if (!info?.existing) return;
    if (!await confirmDelete(`the proxy host for ${info.existing.domainNames.join(", ")}`, "Removes it from Nginx Proxy Manager.")) return;
    setBusy(true);
    try { await api.deleteDomain(d.stackId, info.existing.id); toastOk("Proxy host deleted"); onClose(); }
    catch (e) { toastErr(e, "Could not delete"); } finally { setBusy(false); }
  };

  const save = async () => {
    const list = domains.split(/[\s,]+/).map(x => x.trim()).filter(Boolean);
    if (list.length === 0) { toastErr("Enter at least one domain name"); return; }
    setSaving(true);
    try {
      if (ssl) toastOk("Requesting Let's Encrypt certificate — this can take a moment…");
      await api.setDomain(d.stackId, { id: info?.existing?.id, domainNames: list, scheme, forwardHost: host, forwardPort: port, websockets: ws, ssl, certificateId: info?.existing?.certificateId });
      toastOk(info?.existing ? "Proxy host updated" : "Proxy host created");
      onClose();
    } catch (e) { toastErr(e, "Could not save proxy host"); } finally { setSaving(false); }
  };

  return (
    <Modal opened onClose={onClose} size="lg" title={<Group gap={8}><IconWorld size={18} /><Title order={5}>Domain · {d.name}</Title></Group>}>
      {info === null ? <Loader size="sm" /> : !info.configured ? (
        <Alert color="blue" icon={<IconWorld size={16} />}>
          Connect your Nginx Proxy Manager first under <b>Settings → Hosting</b>. Then this dialog can create or
          edit the proxy host that points your domain at this app.
        </Alert>
      ) : (
        <Stack gap="md">
          {info.error && <Alert color="orange" p="xs" icon={<IconAlertTriangle size={16} />}>Couldn't read existing entries from NPM: {info.error}. You can still create one below.</Alert>}
          {info.existing
            ? <Group justify="space-between">
                <Badge variant="light" color="teal">Editing existing proxy host #{info.existing.id}</Badge>
                <Switch size="xs" checked={enabled} disabled={busy} onChange={e => toggleEnabled(e.currentTarget.checked)} label={enabled ? "Enabled" : "Disabled"} />
              </Group>
            : port > 0 ? <Text size="xs" c="dimmed">No proxy host targets this app yet — creating a new one.</Text>
            : <Alert color="yellow" p="xs" icon={<IconAlertTriangle size={16} />}>This app has no published host port yet (deploy it first), so there's nothing to forward to.</Alert>}
          <TextInput label="Domain names" placeholder="app.example.com, www.example.com" value={domains}
            onChange={e => setDomains(e.currentTarget.value)} description="Comma- or space-separated." data-autofocus />
          <Group grow>
            <Select label="Forward scheme" data={["http", "https"]} value={scheme} onChange={v => setScheme(v ?? "http")} allowDeselect={false} />
            <TextInput label="Forward host" value={host} onChange={e => setHost(e.currentTarget.value)} />
            <NumberInput label="Forward port" value={port || undefined} onChange={v => setPort(Number(v) || 0)} min={1} max={65535} hideControls />
          </Group>
          <Switch checked={ws} onChange={e => setWs(e.currentTarget.checked)} label="Allow WebSocket upgrade" />
          <Switch checked={ssl} onChange={e => setSsl(e.currentTarget.checked)} label="Enable HTTPS (Let's Encrypt)"
            description={ssl && !info.existing?.sslForced
              ? "On save, NPM requests a certificate — the domain must already resolve to this server on port 80."
              : "Provision + force an SSL certificate via Nginx Proxy Manager."} />
          <Text size="xs" c="dimmed">AspireUI prefilled the forward host + port from this deployment.</Text>
          <Group justify="space-between">
            {info.existing
              ? <Button variant="subtle" color="red" leftSection={<IconTrash size={15} />} loading={busy} onClick={del}>Delete</Button>
              : <span />}
            <Group>
              <Button variant="default" onClick={onClose}>Cancel</Button>
              <Button loading={saving} disabled={port <= 0} onClick={save}>{info.existing ? "Update proxy host" : "Create proxy host"}</Button>
            </Group>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}

// Web console: pick container, run one shell command at a time; not a full TTY.
export function TerminalModal({ d, onClose }: { d: Deployment; onClose: () => void }) {
  const [containers, setContainers] = useState<string[]>([]);
  const [services, setServices] = useState<string[]>([]);
  const [container, setContainer] = useState("");
  const [service, setService] = useState("");
  const [fresh, setFresh] = useState(false);
  const [cmd, setCmd] = useState("");
  const [log, setLog] = useState<{ cmd: string; output: string; ok: boolean }[]>([]);
  const [busy, setBusy] = useState(false);
  const [hist, setHist] = useState<string[]>([]);
  const [, setHistIdx] = useState(-1);
  const bottomRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    api.hostingServices(d.id).then(s => {
      const names = s.map(x => x.name).filter(Boolean);
      setContainers(names);
      // default to the app's own container, not our dashboard sidecar
      setContainer(c => c || names.find(n => !n.includes("dashboard")) || names[0] || "");
      // Nothing to exec into (crash loop or stopped) → offer the one-off container instead.
      if (names.length === 0) setFresh(true);
    }).catch(() => setFresh(true));
    api.composeServices(d.id).then(list => {
      setServices(list);
      setService(s => s || list[0] || "");
    }).catch(() => {});
  }, [d.id]);
  useEffect(() => { bottomRef.current?.scrollIntoView({ block: "end" }); }, [log, busy]);

  const target = fresh ? service : container;
  const run = async () => {
    const c = cmd.trim();
    if (!c || !target) return;
    setBusy(true); setCmd(""); setHist(h => [...h, c]); setHistIdx(-1);
    try {
      const r = fresh ? await api.execFreshContainer(d.id, service, c) : await api.execInContainer(d.id, container, c);
      setLog(l => [...l, { cmd: c, output: r.output, ok: r.ok }]);
    }
    catch (e: unknown) { setLog(l => [...l, { cmd: c, output: e instanceof Error ? e.message : String(e), ok: false }]); }
    finally { setBusy(false); }
  };
  const onKey = (e: React.KeyboardEvent) => {
    if (e.key === "Enter") { e.preventDefault(); run(); }
    else if (e.key === "ArrowUp") { e.preventDefault(); setHistIdx(i => { const ni = i < 0 ? hist.length - 1 : Math.max(0, i - 1); setCmd(hist[ni] ?? ""); return ni; }); }
    else if (e.key === "ArrowDown") { e.preventDefault(); setHistIdx(i => { const ni = i < 0 ? -1 : i + 1; if (ni >= hist.length) { setCmd(""); return -1; } setCmd(hist[ni] ?? ""); return ni; }); }
  };

  return (
    <Modal opened onClose={onClose} size="xl" title={<Group gap={8}><IconTerminal2 size={18} /><Title order={5}>Terminal · {d.name}</Title></Group>}>
      <Stack gap="sm">
        <Group gap="xs" align="flex-end">
          {fresh
            ? <Select label="Service (fresh container)" data={services} value={service} onChange={v => setService(v ?? "")} allowDeselect={false} style={{ minWidth: 260 }}
                placeholder={services.length ? undefined : "no services"} />
            : <Select label="Container" data={containers} value={container} onChange={v => setContainer(v ?? "")} allowDeselect={false} style={{ minWidth: 260 }}
                placeholder={containers.length ? undefined : "no running containers"} />}
          <Switch checked={fresh} onChange={e => setFresh(e.currentTarget.checked)} label="fresh container" mb={6} />
          <Text size="xs" c="dimmed" style={{ flex: 1 }}>One command per run — not an interactive shell.</Text>
        </Group>
        {fresh && (
          <Alert color="blue" p="xs" icon={<IconAlertTriangle size={16} />}>
            Runs your command in a <b>new</b> container from that service's image, with its env and volumes but
            without its entrypoint (<code>compose run --rm --no-deps</code>). Use this to repair a service that
            crash-loops or is stopped — <code>docker exec</code> needs a running container. It is removed afterwards.
          </Alert>
        )}
        <ScrollArea h={340} style={{ background: "#0b0e14", borderRadius: 6 }} p="sm">
          {log.length === 0
            ? <Text size="xs" c="dimmed" ff="monospace">Run a command to see its output…</Text>
            : log.map((e, i) => (
              <div key={i} style={{ fontFamily: "monospace", fontSize: 12, whiteSpace: "pre-wrap", marginBottom: 8 }}>
                <Text span c="teal" ff="monospace" fz={12}>$ {e.cmd}</Text>
                {e.output && <div style={{ color: e.ok ? "#c9d1d9" : "#ff7b72" }}>{e.output}</div>}
              </div>
            ))}
          {busy && <Loader size="xs" color="teal" />}
          <div ref={bottomRef} />
        </ScrollArea>
        <TextInput value={cmd} onChange={e => setCmd(e.currentTarget.value)} onKeyDown={onKey} disabled={!target || busy}
          placeholder="e.g. ls -la /  ·  cat /etc/os-release  ·  ps aux" data-autofocus
          leftSection={<Text span c="teal" ff="monospace">$</Text>}
          rightSection={<ActionIcon variant="subtle" onClick={run} disabled={!cmd.trim() || busy} aria-label="Run"><IconPlayerPlay size={16} /></ActionIcon>} />
      </Stack>
    </Modal>
  );
}

// Browse deployment's named volumes: walk directories, download files via read-only mounts.
export function VolumesModal({ d, onClose }: { d: Deployment; onClose: () => void }) {
  const [vols, setVols] = useState<{ name: string; sizeMb: number }[] | null>(null);
  const [vol, setVol] = useState<string | null>(null);
  const [path, setPath] = useState("");
  const [entries, setEntries] = useState<{ name: string; dir: boolean; size: number }[] | null>(null);
  useEffect(() => { api.listVolumes(d.id).then(v => { setVols(v); if (v[0]) setVol(v[0].name); }).catch(() => setVols([])); }, [d.id]);
  useEffect(() => {
    if (!vol) return;
    setEntries(null);
    api.lsVolume(d.id, vol, path).then(setEntries).catch(() => setEntries([]));
  }, [d.id, vol, path]);
  const crumbs = path.split("/").filter(Boolean);
  const goUp = () => setPath(crumbs.slice(0, -1).join("/"));
  const fmt = (n: number) => n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1048576).toFixed(1)} MB`;

  return (
    <Modal opened onClose={onClose} size="xl" title={<Group gap={8}><IconDatabase size={18} /><Title order={5}>Files · {d.name}</Title></Group>}>
      <Stack gap="sm">
        {vols === null ? <Loader size="sm" />
          : vols.length === 0 ? <Alert color="blue" icon={<IconDatabase size={16} />}>This app has no named volumes.</Alert>
          : (
          <>
            <Group gap="xs" align="flex-end">
              <Select label="Volume" data={vols.map(v => ({ value: v.name, label: `${v.name} (${v.sizeMb} MB)` }))}
                value={vol} onChange={v => { setVol(v); setPath(""); }} allowDeselect={false} style={{ minWidth: 300 }} />
            </Group>
            <Group gap={4} wrap="wrap">
              <Anchor size="sm" onClick={() => setPath("")}><IconFolderOpen size={13} /> {vol}</Anchor>
              {crumbs.map((c, i) => (
                <Anchor key={i} size="sm" c={i === crumbs.length - 1 ? "dimmed" : undefined} onClick={() => setPath(crumbs.slice(0, i + 1).join("/"))}>/ {c}</Anchor>
              ))}
              {path && <Button size="compact-xs" variant="subtle" leftSection={<IconArrowBackUp size={13} />} onClick={goUp}>Up</Button>}
            </Group>
            <ScrollArea h={360}>
              {entries === null ? <Loader size="xs" />
                : entries.length === 0 ? <Text size="sm" c="dimmed">Empty.</Text>
                : (
                <Stack gap={0}>
                  {entries.map(e => (
                    <Group key={e.name} gap={8} px={6} py={4} wrap="nowrap" className="ctx-item" style={{ borderRadius: 4 }}>
                      {e.dir ? <IconFolder size={16} color="var(--mantine-color-yellow-6)" /> : <IconFile size={16} color="var(--mantine-color-gray-5)" />}
                      {e.dir
                        ? <Anchor size="sm" style={{ flex: 1 }} onClick={() => setPath(path ? `${path}/${e.name}` : e.name)}>{e.name}</Anchor>
                        : <Text size="sm" style={{ flex: 1 }} truncate>{e.name}</Text>}
                      {!e.dir && <Text size="xs" c="dimmed" ff="monospace">{fmt(e.size)}</Text>}
                      {!e.dir && (
                        <Anchor href={api.volumeFileUrl(d.id, vol!, path ? `${path}/${e.name}` : e.name)} target="_blank" style={{ display: "flex" }}>
                          <IconDownload size={15} />
                        </Anchor>
                      )}
                    </Group>
                  ))}
                </Stack>)}
            </ScrollArea>
          </>)}
      </Stack>
    </Modal>
  );
}
