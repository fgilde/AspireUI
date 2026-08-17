import { useEffect, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { Badge, Anchor, ActionIcon, Menu, Text, Loader, Alert, Group, Table, Button, Tooltip, Stack as MStack, Card, Center, CopyButton, NumberInput, Switch, TextInput, Divider } from "@mantine/core";
import { IconDots, IconExternalLink, IconAlertTriangle, IconFileText, IconPlayerPlay, IconPlayerStop, IconReload, IconServer, IconBrandGithub, IconCopyPlus, IconX, IconWorld } from "@tabler/icons-react";
import { PageShell } from "../components/PageShell";
import type { Deployment, ServiceStatus } from "../model";
import { canOpenEditor, hostingBroken, hostingHealthLabel } from "../model";
import { useAuth } from "../auth/AuthContext";
import * as api from "../api";
import { useTitle } from "../useTitle";
import { Spark } from "../components/Spark";
import { HostingMenuItems, ConfigureModal, LogsModal, BackupsModal, DomainModal, TerminalModal, VolumesModal, hostingColor, deploymentColor } from "../hosting/HostingActions";
import { stackContainers, healthOf, type AppStat } from "./Hosting";
import { toastOk, toastErr } from "../ui";

// Per-app page showing deployment status, containers, health, & actions; deep-linkable.
export function AppDetail() {
  const { id } = useParams();
  const nav = useNavigate();
  const { status } = useAuth();
  const canEdit = canOpenEditor(status?.user);
  const isAdmin = !!status?.user?.isAdmin;
  const [d, setD] = useState<Deployment | null | undefined>(undefined);
  const [svcs, setSvcs] = useState<ServiceStatus[]>([]);
  const [stat, setStat] = useState<AppStat>();
  const [dashToken, setDashToken] = useState("");
  const [pubHost, setPubHost] = useState("");
  const [config, setConfig] = useState(false);
  const [logsSvc, setLogsSvc] = useState<string | undefined>(undefined);
  const [logsOpen, setLogsOpen] = useState(false);
  const [backups, setBackups] = useState(false);
  const [domain, setDomain] = useState(false);
  const [terminal, setTerminal] = useState(false);
  const [files, setFiles] = useState(false);
  const [git, setGit] = useState<{ url: string; branch?: string | null; subdir?: string | null; webhookPath: string } | null>(null);
  const [pulling, setPulling] = useState(false);
  useTitle(d?.name ? `${d.name} · Hosting` : "App");
  useEffect(() => { if (id) api.gitInfo(id).then(setGit).catch(() => setGit(null)); }, [id]);

  const load = () => api.listHosting().then(list => setD(list.find(x => x.stackId === id) ?? null)).catch(() => setD(null));
  useEffect(() => { load(); const t = setInterval(load, 4000); return () => clearInterval(t); /* eslint-disable-next-line */ }, [id]);
  useEffect(() => { api.getDashboardSettings().then(s => { setDashToken(s.dashboardToken); setPubHost(s.publicHost ?? ""); }).catch(() => {}); }, []);
  useEffect(() => {
    if (!d) return;
    const tick = () => api.hostingServices(d.id).then(setSvcs).catch(() => {});
    tick(); const t = setInterval(tick, 4000); return () => clearInterval(t);
  }, [d?.id]);
  useEffect(() => {
    if (!d) return;
    let alive = true;
    const tick = () => api.hostingStats().then(stats => {
      if (!alive) return;
      const mine = stackContainers(stats, d.stackId);
      const cpu = Math.round(mine.reduce((a, s) => a + s.cpu, 0) * 10) / 10;
      const memMb = Math.round(mine.reduce((a, s) => a + s.memMb, 0));
      setStat(prev => ({ cpu, memMb, hist: [...(prev?.hist ?? []), cpu].slice(-40) }));
    }).catch(() => {});
    tick(); const t = setInterval(tick, 3000); return () => { alive = false; clearInterval(t); };
  }, [d?.stackId]);

  if (d === undefined) return <PageShell title="App"><Center py={80}><Loader color="indigo" /></Center></PageShell>;
  if (d === null) return (
    <PageShell title="App">
      <Alert color="gray" icon={<IconServer size={16} />}>This app isn't deployed. <Anchor onClick={() => nav("/hosting")}>Back to hosting</Anchor>.</Alert>
    </PageShell>
  );

  const active = d.state === "running";
  const start = () => { toastOk(`${d.state === "failed" ? "Retrying" : "Starting"} ${d.name}…`); api.startHosting(d.stackId).then(load).catch(toastErr); };
  const stop = () => { toastOk(`Stopping ${d.name}…`); api.stopHosting(d.stackId).then(load).catch(toastErr); };
  const restart = () => { toastOk(`Restarting ${d.name}…`); api.restartHosting(d.stackId).then(load).then(() => toastOk("Restarted")).catch(toastErr); };

  return (
    <PageShell title={d.name} container="lg" actions={
      <Menu position="bottom-end" withArrow>
        <Menu.Target><ActionIcon variant="subtle" aria-label="Actions"><IconDots size={18} /></ActionIcon></Menu.Target>
        <Menu.Dropdown>
          <HostingMenuItems d={d} canEdit={canEdit} onConfigure={() => setConfig(true)} onLogs={(svc?: string) => { setLogsSvc(svc); setLogsOpen(true); }}
            onBackups={() => setBackups(true)} onDomain={() => setDomain(true)}
            onTerminal={isAdmin ? () => setTerminal(true) : undefined} onFiles={isAdmin ? () => setFiles(true) : undefined}
            onOpenEditor={() => nav(`/editor/${d.stackId}`)} onChanged={load} />
        </Menu.Dropdown>
      </Menu>
    }>
      <MStack gap="lg">
        <Card withBorder padding="lg">
          <Group justify="space-between" wrap="wrap">
            <Group gap="md">
              <Group gap={8}>
                {d.state === "deploying" && <Loader size={14} color="yellow" />}
                <Badge size="lg" color={deploymentColor(d)} variant="light">{hostingHealthLabel(d) ?? d.state}</Badge>
              </Group>
              {stat && active && (
                <Tooltip label={`CPU ${stat.cpu}% · ${stat.memMb} MB`} withArrow>
                  <Group gap={8}>
                    <Spark values={stat.hist} color="var(--mantine-color-blue-4)" w={90} h={22} />
                    <Text size="sm" ff="monospace" c="dimmed">{stat.cpu}% · {stat.memMb} MB</Text>
                  </Group>
                </Tooltip>
              )}
            </Group>
            <Group gap="xs">
              {d.state === "deploying" ? <Loader size="sm" color="yellow" />
                : active
                ? <>
                    <Button size="xs" variant="light" color="red" leftSection={<IconPlayerStop size={14} />} onClick={stop}>Stop</Button>
                    <Button size="xs" variant="light" leftSection={<IconReload size={14} />} onClick={restart}>Restart</Button>
                  </>
                : <Button size="xs" variant="light" color="green" leftSection={<IconPlayerPlay size={14} />} onClick={start}>{d.state === "failed" ? "Retry" : "Start"}</Button>}
            </Group>
          </Group>
          {(d.urls.length > 0 || (d.domains ?? []).length > 0) && (
            <Group gap="sm" mt="md">
              {(d.domains ?? []).map(u => <Anchor key={u} href={u} target="_blank" size="sm" c="grape">{u} <IconWorld size={12} /></Anchor>)}
              {d.urls.map(u => <Anchor key={u} href={u} target="_blank" size="sm">{u} <IconExternalLink size={12} /></Anchor>)}
            </Group>
          )}
          {hostingBroken(d) && (
            <Alert color={d.health === "failing" ? "red" : "orange"} icon={<IconAlertTriangle size={14} />} p="xs" mt="md"
              title={d.health === "failing" ? "The app is not running properly" : "The app reports unhealthy"}>
              <Text size="xs">{d.healthDetail ?? "One of its containers is not healthy."}</Text>
              <Anchor size="xs" onClick={() => { setLogsSvc(undefined); setLogsOpen(true); }}>Open the logs</Anchor>
            </Alert>
          )}
          {d.state === "failed" && d.lastError && (
            <Alert color="red" icon={<IconAlertTriangle size={14} />} p="xs" mt="md" title="Deploy failed">
              <Text size="xs" style={{ whiteSpace: "pre-wrap", fontFamily: "monospace", maxHeight: 160, overflow: "auto" }}>{d.lastError.trim().split("\n").slice(-12).join("\n")}</Text>
              <Anchor size="xs" onClick={() => { setLogsSvc(undefined); setLogsOpen(true); }}>Full logs</Anchor>
            </Alert>
          )}
        </Card>

        {git && (
          <Card withBorder padding="lg">
            <Group justify="space-between" wrap="wrap">
              <Group gap={8}>
                <IconBrandGithub size={18} />
                <div>
                  <Anchor href={git.url} target="_blank" size="sm" fw={500}>{git.url} <IconExternalLink size={11} /></Anchor>
                  <Text size="xs" c="dimmed">{git.branch ? `branch ${git.branch}` : "default branch"}{git.subdir ? ` · ${git.subdir}` : ""}</Text>
                </div>
              </Group>
              <Button size="xs" variant="light" loading={pulling} leftSection={<IconReload size={14} />}
                onClick={() => { setPulling(true); toastOk("Pulling latest from Git…"); api.gitPull(d.stackId).then(r => { toastOk(r.redeployed ? "Pulled + redeployed" : "Pulled — start the app to apply"); load(); }).catch(toastErr).finally(() => setPulling(false)); }}>
                Redeploy from Git
              </Button>
            </Group>
            <Group gap={6} mt="sm" wrap="nowrap">
              <Text size="xs" c="dimmed" style={{ whiteSpace: "nowrap" }}>Push-to-deploy webhook:</Text>
              <Text size="xs" ff="monospace" truncate style={{ flex: 1 }}>{window.location.origin}{git.webhookPath}</Text>
              <CopyButton value={`${window.location.origin}${git.webhookPath}`}>
                {({ copied, copy }) => <Button size="compact-xs" variant="subtle" onClick={copy}>{copied ? "Copied" : "Copy"}</Button>}
              </CopyButton>
            </Group>
          </Card>
        )}

        <CloneHooksCard stackId={d.stackId} />

        <div>
          <Text fw={600} mb="xs">Containers</Text>
          {svcs.length === 0
            ? <Text size="sm" c="dimmed">No running containers (start the app to see its resources).</Text>
            : (
            <Table verticalSpacing="xs" fz="sm">
              <Table.Thead><Table.Tr>
                <Table.Th w={16} /><Table.Th>Service</Table.Th><Table.Th>Image</Table.Th><Table.Th>Ports</Table.Th><Table.Th>Status</Table.Th><Table.Th w={28} /></Table.Tr></Table.Thead>
              <Table.Tbody>
                {svcs.map(s => {
                  const port = s.ports.split(",")[0]?.trim().split(":")[0];
                  const isDash = s.service.includes("dashboard") || s.name.includes("dashboard");
                  const url = port && /^\d+$/.test(port)
                    ? (isDash ? `http://${pubHost || window.location.hostname}:${port}/login${dashToken ? `?t=${encodeURIComponent(dashToken)}` : ""}`
                              : `http://${pubHost || window.location.hostname}:${port}`)
                    : null;
                  const h = healthOf(s.status);
                  return (
                    <Table.Tr key={s.name}>
                      <Table.Td><span style={{ display: "inline-block", width: 8, height: 8, borderRadius: 8, background: `var(--mantine-color-${hostingColor(s.state)}-6)` }} /></Table.Td>
                      <Table.Td fw={500}><Group gap={6} wrap="nowrap">{s.service || s.name}{h && <Badge size="xs" variant="dot" color={h === "healthy" ? "green" : h === "unhealthy" ? "red" : "yellow"}>{h}</Badge>}</Group></Table.Td>
                      <Table.Td c="dimmed">{s.image}</Table.Td>
                      <Table.Td>{url ? <Anchor href={url} target="_blank">{s.ports} <IconExternalLink size={10} /></Anchor> : <Text c="dimmed" span>{s.ports}</Text>}</Table.Td>
                      <Table.Td c="dimmed">{s.status}</Table.Td>
                      <Table.Td>
                        <ActionIcon variant="subtle" color="gray" size="sm" aria-label={`Logs for ${s.service || s.name}`}
                          onClick={() => { setLogsSvc(s.service || s.name); setLogsOpen(true); }}><IconFileText size={14} /></ActionIcon>
                      </Table.Td>
                    </Table.Tr>
                  );
                })}
              </Table.Tbody>
            </Table>)}
        </div>
      </MStack>

      {config && <ConfigureModal d={d} onClose={() => setConfig(false)} onDone={load} />}
      {logsOpen && <LogsModal d={d} service={logsSvc} onClose={() => setLogsOpen(false)} />}
      {backups && <BackupsModal d={d} onClose={() => setBackups(false)} onChanged={load} />}
      {domain && <DomainModal d={d} onClose={() => setDomain(false)} />}
      {terminal && <TerminalModal d={d} onClose={() => setTerminal(false)} />}
      {files && <VolumesModal d={d} onClose={() => setFiles(false)} />}
    </PageShell>
  );
}

function CloneHooksCard({ stackId }: { stackId: string }) {
  const [data, setData] = useState<{ npmConfigured: boolean; hooks: api.CloneHook[] } | null>(null);
  const [expireDays, setExpireDays] = useState(7);
  const [bindDomain, setBindDomain] = useState(false);
  const [domainFormat, setDomainFormat] = useState("");
  const [busy, setBusy] = useState(false);
  const load = () => api.listCloneHooks(stackId).then(setData).catch(() => {});
  useEffect(() => { load(); }, [stackId]); // eslint-disable-line react-hooks/exhaustive-deps
  const create = async () => {
    setBusy(true);
    try { await api.createCloneHook(stackId, { expireDays, bindDomain, domainFormat: bindDomain ? domainFormat.trim() : undefined }); load(); toastOk("Clone hook created"); }
    catch (e) { toastErr(e); } finally { setBusy(false); }
  };
  const full = (p: string) => `${window.location.origin}${p}`;
  return (
    <Card withBorder padding="lg">
      <Group gap={8} mb={4}><IconCopyPlus size={18} /><Text fw={600}>Clone hooks</Text></Group>
      <Text size="xs" c="dimmed" mb="sm">POST the URL to spin up an auto-expiring copy of this app{data?.npmConfigured ? " (optionally on its own domain)" : ""}. The response contains the new app URL and expiry date.</Text>
      {(data?.hooks ?? []).map(h => (
        <Group key={h.token} gap={6} mb={4} wrap="nowrap">
          <Text size="xs" ff="monospace" truncate style={{ flex: 1 }}>{full(h.webhookPath)}</Text>
          <Badge size="xs" variant="light" color={h.expireDays < 0 ? "gray" : "blue"}>{h.expireDays < 0 ? "never expires" : `${h.expireDays}d`}</Badge>
          {h.bindDomain && <Badge size="xs" variant="light" color="grape">{h.domainFormat}</Badge>}
          <CopyButton value={full(h.webhookPath)}>{({ copied, copy }) => <Button size="compact-xs" variant="subtle" onClick={copy}>{copied ? "Copied" : "Copy"}</Button>}</CopyButton>
          <ActionIcon size="sm" variant="subtle" color="red" onClick={() => api.deleteCloneHook(stackId, h.token).then(load).catch(toastErr)} aria-label="Delete"><IconX size={14} /></ActionIcon>
        </Group>
      ))}
      <Divider my="sm" label="New clone hook" labelPosition="left" />
      <MStack gap="xs" maw={420}>
        <NumberInput label="Auto-delete after (days)" description="-1 = keep forever" value={expireDays}
          onChange={v => setExpireDays(Number(v) ?? 7)} min={-1} max={365} w={220} />
        {data?.npmConfigured && (
          <>
            <Switch label="Bind a domain via Nginx Proxy Manager" checked={bindDomain} onChange={e => setBindDomain(e.currentTarget.checked)} />
            {bindDomain && (
              <TextInput label="Domain pattern" placeholder="my-app-{id}.example.com" value={domainFormat}
                description="{id} and {name} are substituted per clone." onChange={e => setDomainFormat(e.currentTarget.value)} />
            )}
          </>
        )}
        <Group><Button size="xs" loading={busy} disabled={bindDomain && !domainFormat.trim()} onClick={create}>Create hook</Button></Group>
      </MStack>
    </Card>
  );
}
