import { useEffect, useMemo, useState } from "react";
import { Button, Center, Stack as MStack, Text, Loader, Group, ActionIcon, Tooltip, Badge, ScrollArea, Anchor, Menu } from "@mantine/core";
import { IconPlayerPlay, IconPlayerStop, IconExternalLink, IconRefresh, IconTerminal2, IconDots } from "@tabler/icons-react";
import { useEditor } from "./DockLayout";
import { liveStateColor, type LiveResource, type LiveCommand } from "../model";
import { ResourceLogDrawer } from "./ResourceLogDrawer";
import { confirmDelete, toastErr, toastOk } from "../ui";
import * as api from "../api";

const dot = (state?: string | null) => `var(--mantine-color-${liveStateColor(state)}-filled)`;
const primaryUrl = (r: LiveResource) => r.urls.find(u => !u.isInternal && !u.isInactive)?.url;

// Tiny inline sparkline (0..max normalized) — no chart lib.
function Spark({ values, color }: { values: number[]; color: string }) {
  if (values.length < 2) return null;
  const w = 46, h = 14, max = Math.max(1, ...values);
  const pts = values.map((v, i) => `${(i / (values.length - 1)) * w},${h - (v / max) * h}`).join(" ");
  return <svg width={w} height={h} style={{ display: "block" }}><polyline points={pts} fill="none" stroke={color} strokeWidth={1.5} /></svg>;
}

// A built-in mini dashboard: instead of embedding the fragile Blazor dashboard in an iframe, we render
// our own from the Aspire resource-service feed we already consume — live per-resource state, endpoint
// links and console-log streaming. The full Aspire dashboard (traces/metrics) stays one click away.
export function DashboardPanel() {
  const { stack, runStatus, setRunStatus, showPanel } = useEditor();
  const [live, setLive] = useState<LiveResource[]>([]);
  const [logTarget, setLogTarget] = useState<{ name: string; display: string } | null>(null);
  const url = runStatus.dashboardUrl;
  const state = runStatus.state;
  const active = state === "Running" || state === "Starting";

  useEffect(() => {
    if (!active) { setLive([]); return; }
    let alive = true;
    const tick = () => api.stackResources(stack.id).then(r => { if (alive) setLive(r); }).catch(() => {});
    tick();
    const iv = setInterval(tick, 2500);
    return () => { alive = false; clearInterval(iv); };
  }, [active, stack.id]);

  // Container CPU/mem via docker stats — keep a short per-container history for sparklines.
  const [cpuHist, setCpuHist] = useState<Record<string, number[]>>({});
  const [memNow, setMemNow] = useState<Record<string, number>>({});
  useEffect(() => {
    if (!active) { setCpuHist({}); setMemNow({}); return; }
    let alive = true;
    const tick = () => api.stackStats(stack.id).then(stats => {
      if (!alive) return;
      setCpuHist(prev => {
        const next: Record<string, number[]> = { ...prev };
        for (const s of stats) next[s.name] = [...(prev[s.name] ?? []), s.cpu].slice(-20);
        return next;
      });
      setMemNow(Object.fromEntries(stats.map(s => [s.name, s.memMb])));
    }).catch(() => {});
    tick();
    const iv = setInterval(tick, 3000);
    return () => { alive = false; clearInterval(iv); };
  }, [active, stack.id]);
  // A live resource's docker name isn't its exact resource name; match loosely by containment.
  const statKey = (r: LiveResource) => Object.keys(memNow).find(n => n === r.name || n.includes(r.displayName) || r.name.includes(n));

  // Order parents before their children (nesting from Aspire relationships), children indented.
  const rows = useMemo(() => {
    const visible = live.filter(r => !r.hidden);
    const byName = new Map(visible.map(r => [r.name, r]));
    const kids = new Map<string, LiveResource[]>();
    const roots: LiveResource[] = [];
    for (const r of visible) {
      if (r.parent && byName.has(r.parent)) {
        const arr = kids.get(r.parent) ?? [];
        arr.push(r); kids.set(r.parent, arr);
      } else roots.push(r);
    }
    const out: { r: LiveResource; depth: number }[] = [];
    const walk = (r: LiveResource, depth: number) => {
      out.push({ r, depth });
      for (const c of kids.get(r.name) ?? []) walk(c, depth + 1);
    };
    roots.forEach(r => walk(r, 0));
    return out;
  }, [live]);

  const runningCount = live.filter(r => (r.state ?? "").toLowerCase().includes("running")).length;
  const start = () => api.runStack(stack.id).then(setRunStatus).catch(e => toastErr(e, "Could not start"));
  const stop = () => api.stopStack(stack.id).then(r => { setRunStatus(r); toastOk("Stopped"); }).catch(e => toastErr(e, "Could not stop"));
  const refresh = () => api.stackResources(stack.id).then(setLive).catch(() => {});
  const runCommand = async (r: LiveResource, c: LiveCommand) => {
    if (c.confirmationMessage && !(await confirmDelete(c.displayName, c.confirmationMessage))) return;
    api.runResourceCommand(stack.id, r.name, c.name, r.type)
      .then(res => { res.ok ? toastOk(res.message || `${c.displayName} — done`) : toastErr(res.message || "command failed"); refresh(); })
      .catch(e => toastErr(e, "Command failed"));
  };

  if (!active) {
    return (
      <Center h="100%">
        <MStack align="center" gap="sm" maw={340} px="md">
          <Text fw={600}>Dashboard</Text>
          <Text size="sm" c="dimmed" ta="center">
            Start the stack to see live per-resource status, endpoints and logs here.
          </Text>
          <Button leftSection={<IconPlayerPlay size={16} />} color="green" onClick={start}>Start stack</Button>
          {state === "Failed" && (
            <Text size="xs" c="red">
              Last run failed — check the <Anchor size="xs" c="red" fw={600} onClick={() => showPanel("logs")}>Logs panel</Anchor>.
            </Text>
          )}
        </MStack>
      </Center>
    );
  }

  return (
    <div style={{ height: "100%", display: "flex", flexDirection: "column" }}>
      <Group justify="space-between" px="sm" py={6} wrap="nowrap">
        <Group gap={8} wrap="nowrap">
          <Button size="xs" color="red" variant="light" leftSection={<IconPlayerStop size={14} />} onClick={stop}>Stop</Button>
          <Text size="xs" c="dimmed">{runningCount}/{live.filter(r => !r.hidden).length} running</Text>
        </Group>
        <Group gap={6} wrap="nowrap">
          <Tooltip label="Refresh" withArrow><ActionIcon size="sm" variant="subtle" onClick={refresh}><IconRefresh size={14} /></ActionIcon></Tooltip>
          {url && (
            <Button size="xs" variant="subtle" leftSection={<IconExternalLink size={14} />} component="a" href={url} target="_blank">
              Aspire dashboard
            </Button>
          )}
        </Group>
      </Group>

      <ScrollArea style={{ flex: 1 }} px="xs">
        {rows.length === 0 ? (
          <Center py="xl"><Group gap="xs"><Loader size="xs" /><Text size="sm" c="dimmed">Waiting for resources…</Text></Group></Center>
        ) : (
          <MStack gap={2} py="xs">
            {rows.map(({ r, depth }) => {
              const u = primaryUrl(r);
              return (
                <Group key={r.name} gap={8} wrap="nowrap" px={6} py={4}
                  style={{ paddingLeft: 6 + depth * 18, borderRadius: 4 }} className="ctx-item">
                  <Tooltip label={r.state ?? "…"} withArrow>
                    <span style={{ width: 8, height: 8, borderRadius: "50%", background: dot(r.state), flexShrink: 0 }} />
                  </Tooltip>
                  <Text size="sm" fw={depth === 0 ? 600 : 400} truncate style={{ flex: 1, minWidth: 0 }} title={r.name}>
                    {r.displayName}
                  </Text>
                  <Badge size="xs" variant="light" color="gray">{r.type}</Badge>
                  {(() => {
                    const k = statKey(r);
                    if (!k) return null;
                    const cpu = cpuHist[k]?.at(-1) ?? 0;
                    return (
                      <Tooltip label={`CPU ${cpu.toFixed(1)}% · ${memNow[k] ?? 0} MB`} withArrow>
                        <Group gap={5} wrap="nowrap">
                          <Spark values={cpuHist[k] ?? []} color="var(--mantine-color-blue-4)" />
                          <Text size="10px" c="dimmed" ff="monospace" style={{ width: 66, textAlign: "right" }}>{cpu.toFixed(0)}% · {memNow[k] ?? 0}MB</Text>
                        </Group>
                      </Tooltip>
                    );
                  })()}
                  {u && (
                    <Tooltip label={u} withArrow><Anchor href={u} target="_blank" rel="noreferrer" style={{ display: "flex" }}><IconExternalLink size={14} /></Anchor></Tooltip>
                  )}
                  <Tooltip label="Stream logs" withArrow>
                    <ActionIcon size="sm" variant="subtle" onClick={() => setLogTarget({ name: r.name, display: r.displayName })}>
                      <IconTerminal2 size={14} />
                    </ActionIcon>
                  </Tooltip>
                  {r.commands.length > 0 && (
                    <Menu position="bottom-end" withArrow>
                      <Menu.Target>
                        <ActionIcon size="sm" variant="subtle"><IconDots size={14} /></ActionIcon>
                      </Menu.Target>
                      <Menu.Dropdown>
                        <Menu.Label>{r.displayName}</Menu.Label>
                        {r.commands.map(c => (
                          <Menu.Item key={c.name} disabled={!c.enabled} onClick={() => runCommand(r, c)}>
                            {c.displayName}
                          </Menu.Item>
                        ))}
                      </Menu.Dropdown>
                    </Menu>
                  )}
                </Group>
              );
            })}
          </MStack>
        )}
      </ScrollArea>

      <ResourceLogDrawer stackId={stack.id} target={logTarget} onClose={() => setLogTarget(null)} />
    </div>
  );
}
