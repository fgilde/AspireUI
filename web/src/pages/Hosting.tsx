import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { Table, Badge, Anchor, ActionIcon, Menu, Text, Loader, Alert, Group } from "@mantine/core";
import { IconDots, IconExternalLink, IconChevronRight, IconChevronDown, IconAlertTriangle, IconFileText } from "@tabler/icons-react";
import { PageShell } from "../components/PageShell";
import type { Deployment, ServiceStatus } from "../model";
import { canOpenEditor } from "../model";
import { useAuth } from "../auth/AuthContext";
import * as api from "../api";
import { useTitle } from "../useTitle";
import { HostingMenuItems, ConfigureModal, LogsModal, BackupsModal, DomainModal, hostingColor } from "../hosting/HostingActions";

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
  const [dashToken, setDashToken] = useState("");
  const load = () => api.listHosting().then(setItems).catch(() => {});
  useEffect(() => { load(); const t = setInterval(load, 4000); return () => clearInterval(t); }, []);
  useEffect(() => { api.getDashboardSettings().then(s => setDashToken(s.dashboardToken)).catch(() => {}); }, []);

  return (
    <PageShell title="Hosting" container="lg">
          {items.length === 0
            ? <Text c="dimmed" size="sm">No stacks deployed to hosting yet. Open a stack and choose <b>Deploy to hosting</b>.</Text>
            : (
            <Table verticalSpacing="sm">
              <Table.Thead><Table.Tr>
                <Table.Th w={30} /><Table.Th>App</Table.Th><Table.Th>Status</Table.Th><Table.Th>URLs</Table.Th><Table.Th /></Table.Tr></Table.Thead>
              <Table.Tbody>
                {items.map(d => (
                  <DeploymentRow key={d.id} d={d} canEdit={canEdit} onChanged={load} dashToken={dashToken}
                    onConfigure={() => setConfigFor(d)} onLogs={(svc) => { setLogsService(svc); setLogsFor(d); }}
                    onBackups={() => setBackupsFor(d)} onDomain={() => setDomainFor(d)}
                    onOpenEditor={() => nav(`/editor/${d.stackId}`)} />
                ))}
              </Table.Tbody>
            </Table>)}
      {configFor && <ConfigureModal d={configFor} onClose={() => setConfigFor(null)} onDone={load} />}
      {logsFor && <LogsModal d={logsFor} service={logsService} onClose={() => setLogsFor(null)} />}
      {backupsFor && <BackupsModal d={backupsFor} onClose={() => setBackupsFor(null)} onChanged={load} />}
      {domainFor && <DomainModal d={domainFor} onClose={() => setDomainFor(null)} />}
    </PageShell>
  );
}

function DeploymentRow({ d, canEdit, onConfigure, onLogs, onBackups, onDomain, onOpenEditor, onChanged, dashToken }: {
  d: Deployment; canEdit: boolean; onConfigure: () => void; onLogs: (service?: string) => void; onBackups: () => void; onDomain: () => void; onOpenEditor: () => void; onChanged: () => void; dashToken: string;
}) {
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
        <Table.Td>{d.name}</Table.Td>
        <Table.Td><Group gap={6} wrap="nowrap">{d.state === "deploying" && <Loader size={12} color="yellow" />}<Badge color={hostingColor(d.state)} variant="light">{d.state}</Badge></Group></Table.Td>
        <Table.Td>{d.urls.map(u => <Anchor key={u} href={u} target="_blank" mr="sm" size="sm">{u} <IconExternalLink size={12} /></Anchor>)}</Table.Td>
        <Table.Td>
          <Menu position="bottom-end" withArrow>
            <Menu.Target><ActionIcon variant="subtle" aria-label={`Actions for ${d.name}`}><IconDots size={16} /></ActionIcon></Menu.Target>
            <Menu.Dropdown>
              <HostingMenuItems d={d} canEdit={canEdit} onConfigure={onConfigure} onLogs={onLogs} onBackups={onBackups} onDomain={onDomain} onOpenEditor={onOpenEditor} onChanged={onChanged} />
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
                        ? (isDash ? `http://${window.location.hostname}:${port}/login${dashToken ? `?t=${encodeURIComponent(dashToken)}` : ""}`
                                  : `http://${window.location.hostname}:${port}`)
                        : null;
                      return (
                      <Table.Tr key={s.name}>
                        <Table.Td w={12}><span style={{ display: "inline-block", width: 8, height: 8, borderRadius: 8, background: `var(--mantine-color-${hostingColor(s.state)}-6)` }} /></Table.Td>
                        <Table.Td fw={500}>{s.service || s.name}</Table.Td>
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
