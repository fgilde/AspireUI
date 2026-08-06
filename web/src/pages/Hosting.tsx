import { useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { Table, Badge, Anchor, ActionIcon, Menu, Text, Loader, Alert, Group, Tooltip } from "@mantine/core";
import { IconDots, IconExternalLink, IconChevronRight, IconChevronDown, IconAlertTriangle, IconFileText, IconWorld } from "@tabler/icons-react";
import { PageShell } from "../components/PageShell";
import type { Deployment, ServiceStatus } from "../model";
import { canOpenEditor } from "../model";
import { useAuth } from "../auth/AuthContext";
import * as api from "../api";
import type { ContainerStat } from "../api";
import { useTitle } from "../useTitle";
import { Spark } from "../components/Spark";
import { HostingMenuItems, ConfigureModal, LogsModal, BackupsModal, DomainModal, TerminalModal, VolumesModal, hostingColor } from "../hosting/HostingActions";

// Containers named `aspireui-<stackId[..8]>-<service>-N` by compose.
export const projectPrefix = (stackId: string) => `aspireui-${stackId.slice(0, 8)}`;
// This stack's own containers, excluding the bundled aspire-dashboard sidecar (not part of the app's load).
export const stackContainers = (stats: ContainerStat[], stackId: string) =>
  stats.filter(s => s.name.startsWith(projectPrefix(stackId)) && !s.name.includes("dashboard"));
export type AppStat = { cpu: number; memMb: number; hist: number[] };
// Health hint from docker compose ps status text.
export const healthOf = (status: string): "healthy" | "unhealthy" | "starting" | null =>
  /\(healthy\)/i.test(status) ? "healthy" : /\(unhealthy\)/i.test(status) ? "unhealthy"
    : /\(health: starting\)/i.test(status) ? "starting" : null;

export function Hosting() {
  const nav = useNavigate();
  const { status } = useAuth();
  const canEdit = canOpenEditor(status?.user);
  useTitle("Hosting");
  const [items, setItems] = useState<Deployment[]>([]);
  const [configFor, setConfigFor] = useState<Deployment | null>(null);
  const [logsFor, setLogsFor] = useState<Deployment | null>(null);
  const [logsService, setLogsService] = useState<string | undefined>(undefined);
  const [backupsFor, setBackupsFor] = useState<Deployment | null>(null);
  const [domainFor, setDomainFor] = useState<Deployment | null>(null);
  const [terminalFor, setTerminalFor] = useState<Deployment | null>(null);
  const [filesFor, setFilesFor] = useState<Deployment | null>(null);
  const isAdmin = !!status?.user?.isAdmin;
  const [dashToken, setDashToken] = useState("");
  const [pubHost, setPubHost] = useState("");
  const [appStats, setAppStats] = useState<Record<string, AppStat>>({});
  const [summary, setSummary] = useState<{ diskFreeGb: number; diskTotalGb: number } | null>(null);
  const load = () => api.listHosting().then(setItems).catch(() => {});
  useEffect(() => { load(); const t = setInterval(load, 4000); return () => clearInterval(t); }, []);
  useEffect(() => { api.getDashboardSettings().then(s => { setDashToken(s.dashboardToken); setPubHost(s.publicHost ?? ""); }).catch(() => {}); }, []);
  useEffect(() => { const tick = () => api.hostingSummary().then(setSummary).catch(() => {}); tick(); const t = setInterval(tick, 30000); return () => clearInterval(t); }, []);

  const itemsRef = useRef<Deployment[]>([]);
  itemsRef.current = items;
  useEffect(() => {
    let alive = true;
    const tick = () => api.hostingStats().then((stats: ContainerStat[]) => {
      if (!alive) return;
      setAppStats(prev => {
        const next: Record<string, AppStat> = {};
        for (const d of itemsRef.current) {
          const mine = stackContainers(stats, d.stackId);
          const cpu = Math.round(mine.reduce((a, s) => a + s.cpu, 0) * 10) / 10;
          const memMb = Math.round(mine.reduce((a, s) => a + s.memMb, 0));
          next[d.stackId] = { cpu, memMb, hist: [...(prev[d.stackId]?.hist ?? []), cpu].slice(-20) };
        }
        return next;
      });
    }).catch(() => {});
    tick();
    const t = setInterval(tick, 3000);
    return () => { alive = false; clearInterval(t); };
  }, []);

  const runningCount = items.filter(d => d.state === "running").length;
  const diskPct = summary && summary.diskTotalGb > 0 ? Math.round((1 - summary.diskFreeGb / summary.diskTotalGb) * 100) : null;

  return (
    <PageShell title="Hosting" container="lg">
          {items.length > 0 && (
            <Group gap="lg" mb="md" wrap="wrap">
              <Text size="sm"><b>{runningCount}</b>/{items.length} running</Text>
              {summary && summary.diskTotalGb > 0 && (
                <Text size="sm" c={diskPct !== null && diskPct >= 90 ? "red" : "dimmed"}>
                  Disk: <b>{summary.diskFreeGb} GB</b> free of {summary.diskTotalGb} GB{diskPct !== null ? ` (${diskPct}% used)` : ""}
                </Text>
              )}
            </Group>
          )}
          {items.length === 0
            ? <Text c="dimmed" size="sm">No stacks deployed to hosting yet. Open a stack and choose <b>Deploy to hosting</b>.</Text>
            : (
            <Table verticalSpacing="sm">
              <Table.Thead><Table.Tr>
                <Table.Th w={30} /><Table.Th>App</Table.Th><Table.Th>Status</Table.Th><Table.Th>CPU · Mem</Table.Th><Table.Th>URLs</Table.Th><Table.Th /></Table.Tr></Table.Thead>
              <Table.Tbody>
                {items.map(d => (
                  <DeploymentRow key={d.id} d={d} canEdit={canEdit} onChanged={load} dashToken={dashToken} pubHost={pubHost} stat={appStats[d.stackId]}
                    onConfigure={() => setConfigFor(d)} onLogs={(svc) => { setLogsService(svc); setLogsFor(d); }}
                    onBackups={() => setBackupsFor(d)} onDomain={() => setDomainFor(d)} onTerminal={isAdmin ? () => setTerminalFor(d) : undefined} onFiles={isAdmin ? () => setFilesFor(d) : undefined}
                    onOpenEditor={() => nav(`/editor/${d.stackId}`)} />
                ))}
              </Table.Tbody>
            </Table>)}
      {configFor && <ConfigureModal d={configFor} onClose={() => setConfigFor(null)} onDone={load} />}
      {logsFor && <LogsModal d={logsFor} service={logsService} onClose={() => setLogsFor(null)} />}
      {backupsFor && <BackupsModal d={backupsFor} onClose={() => setBackupsFor(null)} onChanged={load} />}
      {domainFor && <DomainModal d={domainFor} onClose={() => setDomainFor(null)} />}
      {terminalFor && <TerminalModal d={terminalFor} onClose={() => setTerminalFor(null)} />}
      {filesFor && <VolumesModal d={filesFor} onClose={() => setFilesFor(null)} />}
    </PageShell>
  );
}

function DeploymentRow({ d, canEdit, onConfigure, onLogs, onBackups, onDomain, onTerminal, onFiles, onOpenEditor, onChanged, dashToken, pubHost, stat }: {
  d: Deployment; canEdit: boolean; onConfigure: () => void; onLogs: (service?: string) => void; onBackups: () => void; onDomain: () => void; onTerminal?: () => void; onFiles?: () => void; onOpenEditor: () => void; onChanged: () => void; dashToken: string; pubHost: string; stat?: AppStat;
}) {
  const nav = useNavigate();
  const [open, setOpen] = useState(false);
  const [svcs, setSvcs] = useState<ServiceStatus[] | null>(null);
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
        <Table.Td><Anchor fw={500} onClick={() => nav(`/app/${d.stackId}`)}>{d.name}</Anchor></Table.Td>
        <Table.Td><Group gap={6} wrap="nowrap">{d.state === "deploying" && <Loader size={12} color="yellow" />}<Badge color={hostingColor(d.state)} variant="light">{d.state}</Badge></Group></Table.Td>
        <Table.Td>
          {d.state === "running" && stat ? (
            <Tooltip label={`CPU ${stat.cpu}% · ${stat.memMb} MB`} withArrow>
              <Group gap={6} wrap="nowrap">
                <Spark values={stat.hist} color="var(--mantine-color-blue-4)" />
                <Text size="xs" c="dimmed" ff="monospace" style={{ whiteSpace: "nowrap" }}>{stat.cpu}% · {stat.memMb}MB</Text>
              </Group>
            </Tooltip>
          ) : <Text size="xs" c="dimmed">—</Text>}
        </Table.Td>
        <Table.Td>
          {(d.domains ?? []).map(u => <Anchor key={u} href={u} target="_blank" mr="sm" size="sm" c="grape">{u} <IconWorld size={12} /></Anchor>)}
          {d.urls.map(u => <Anchor key={u} href={u} target="_blank" mr="sm" size="sm">{u} <IconExternalLink size={12} /></Anchor>)}
        </Table.Td>
        <Table.Td>
          <Menu position="bottom-end" withArrow>
            <Menu.Target><ActionIcon variant="subtle" aria-label={`Actions for ${d.name}`}><IconDots size={16} /></ActionIcon></Menu.Target>
            <Menu.Dropdown>
              <HostingMenuItems d={d} canEdit={canEdit} onConfigure={onConfigure} onLogs={onLogs} onBackups={onBackups} onDomain={onDomain} onTerminal={onTerminal} onFiles={onFiles} onOpenEditor={onOpenEditor} onChanged={onChanged} />
            </Menu.Dropdown>
          </Menu>
        </Table.Td>
      </Table.Tr>
      {open && (
        <Table.Tr>
          <Table.Td colSpan={6} p={0}>
            <div style={{ padding: "8px 16px 12px 46px" }}>
              {d.state === "failed" && d.lastError && (
                <Alert color="red" icon={<IconAlertTriangle size={14} />} p="xs" mb="xs" title="Deploy failed">
                  <Text size="xs" style={{ whiteSpace: "pre-wrap", fontFamily: "monospace", maxHeight: 160, overflow: "auto" }}>{d.lastError.trim().split("\n").slice(-12).join("\n")}</Text>
                  <Anchor size="xs" onClick={() => onLogs()}>Full logs</Anchor>
                </Alert>
              )}
              {svcs === null ? <Loader size="xs" />
                : svcs.length === 0 ? <Text size="xs" c="dimmed">No running containers (start the app to see its resources).</Text>
                : (
                <Table withRowBorders={false} verticalSpacing={4} fz="xs">
                  <Table.Tbody>
                    {svcs.map(s => {
                      const port = s.ports.split(",")[0]?.trim().split(":")[0];
                      const isDash = s.service.includes("dashboard") || s.name.includes("dashboard");
                      const url = port && /^\d+$/.test(port)
                        ? (isDash ? `http://${pubHost || window.location.hostname}:${port}/login${dashToken ? `?t=${encodeURIComponent(dashToken)}` : ""}`
                                  : `http://${pubHost || window.location.hostname}:${port}`)
                        : null;
                      return (
                      <Table.Tr key={s.name}>
                        <Table.Td w={12}><span style={{ display: "inline-block", width: 8, height: 8, borderRadius: 8, background: `var(--mantine-color-${hostingColor(s.state)}-6)` }} /></Table.Td>
                        <Table.Td fw={500}>
                          <Group gap={6} wrap="nowrap">
                            {s.service || s.name}
                            {(() => { const h = healthOf(s.status); return h ? <Badge size="xs" variant="dot" color={h === "healthy" ? "green" : h === "unhealthy" ? "red" : "yellow"}>{h}</Badge> : null; })()}
                          </Group>
                        </Table.Td>
                        <Table.Td c="dimmed">{s.image}</Table.Td>
                        <Table.Td>{url ? <Anchor href={url} target="_blank">{s.ports} <IconExternalLink size={10} /></Anchor> : <Text c="dimmed" span>{s.ports}</Text>}</Table.Td>
                        <Table.Td c="dimmed">{s.status}</Table.Td>
                        <Table.Td w={28}>
                          <ActionIcon variant="subtle" color="gray" size="xs" aria-label={`Logs for ${s.service || s.name}`}
                            onClick={() => onLogs(s.service || s.name)}><IconFileText size={13} /></ActionIcon>
                        </Table.Td>
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
