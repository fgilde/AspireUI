import { useEffect, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { Alert, Anchor, ActionIcon, Badge, Button, Card, Code, CopyButton, Group, Menu, NumberInput, Stack, Switch, Text, Title, Tooltip, Center, Loader } from "@mantine/core";
import { IconDots, IconPencil, IconPlayerPlay, IconPlus, IconRefresh, IconTrash, IconWebhook, IconAlertTriangle, IconClockHour4 } from "@tabler/icons-react";
import * as api from "../api";
import { PageShell } from "../components/PageShell";
import { useTitle } from "../useTitle";
import { HookEditModal } from "../hooks/HookEditModal";
import { HookCallModal } from "../hooks/HookCallModal";
import { curlFor } from "../hooks/curl";
import { confirmDelete, toastErr, toastOk } from "../ui";
import { hostingColor } from "../hosting/HostingActions";

const KIND: Record<api.HookKind, { label: string; color: string }> = {
  clone: { label: "Clone", color: "blue" }, store: { label: "Store", color: "grape" }, git: { label: "Git", color: "dark" },
};

const left = (iso?: string | null) => {
  if (!iso) return "never expires";
  const h = Math.round((new Date(iso).getTime() - Date.now()) / 3_600_000);
  return h <= 0 ? "expiring" : h < 48 ? `${h}h left` : `${Math.round(h / 24)}d left`;
};

function CurlBlock({ text }: { text: string }) {
  return (
    <Group gap={6} align="flex-start" wrap="nowrap">
      <Code block style={{ flex: 1, whiteSpace: "pre-wrap", wordBreak: "break-all" }}>{text}</Code>
      <CopyButton value={text}>{({ copied, copy }) => <Button size="compact-xs" variant="subtle" onClick={copy}>{copied ? "Copied" : "Copy"}</Button>}</CopyButton>
    </Group>
  );
}

export default function Hooks() {
  useTitle("Webhooks");
  const nav = useNavigate();
  const { hash } = useLocation();
  const [data, setData] = useState<api.HooksOverview | null>(null);
  const [settings, setSettings] = useState<api.HookSettings | null>(null);
  const [edit, setEdit] = useState<(Partial<api.Hook> & { kind: api.HookKind }) | null>(null);
  const [calling, setCalling] = useState<api.HookRow | null>(null);
  const load = () => api.listHooks().then(d => { setData(d); setSettings(d.settings); }).catch(toastErr);
  useEffect(() => { load(); }, []);
  useEffect(() => {
    if (!data || !hash) return;
    document.getElementById(hash.slice(1))?.scrollIntoView({ behavior: "smooth", block: "center" });
  }, [data, hash]);

  const saveSettings = (s: api.HookSettings) => { setSettings(s); api.saveHookSettings(s).catch(toastErr); };
  const targetName = (id?: string | null) => data?.targets.find(t => t.id === (id ?? "local"))?.name ?? id ?? "local";
  const origin = window.location.origin;

  if (!data || !settings) return <PageShell title="Webhooks"><Center py={60}><Loader /></Center></PageShell>;

  return (
    <PageShell title="Webhooks" actions={<Button leftSection={<IconPlus size={16} />} onClick={() => setEdit({ kind: "store" })}>New hook</Button>}>
      <Stack gap="lg">
        <Card withBorder padding="lg">
          <Group justify="space-between" align="flex-end">
            <Switch size="md" label="Hooks enabled" description="Off: every hook call is refused with 503."
              checked={settings.enabled} onChange={e => saveSettings({ ...settings, enabled: e.currentTarget.checked })} />
            <Group gap="md">
              <NumberInput label="Min. free disk (GB)" min={0} step={1} decimalScale={1} w={160} value={settings.minDiskGb}
                onChange={v => saveSettings({ ...settings, minDiskGb: Number(v) || 0 })} />
              <NumberInput label="Min. free memory (GB)" min={0} step={0.5} decimalScale={1} w={180} value={settings.minRamGb}
                onChange={v => saveSettings({ ...settings, minRamGb: Number(v) || 0 })} />
            </Group>
          </Group>
          {!settings.enabled && <Alert mt="sm" color="red" icon={<IconAlertTriangle size={16} />}>All hooks are disabled — every call is refused with 503.</Alert>}
        </Card>

        {data.hooks.length === 0 && <Text c="dimmed" size="sm">No hooks yet. A hook is a URL that creates a new, auto-expiring instance on each POST.</Text>}

        {data.hooks.map(row => {
          const h = row.hook;
          const highlighted = hash === `#${h.token}`;
          return (
            <Card key={h.token} id={h.token} withBorder padding="lg" style={{ borderColor: highlighted ? "var(--mantine-color-blue-6)" : undefined, borderWidth: highlighted ? 2 : undefined, opacity: h.enabled ? 1 : 0.7 }}>
              <Group justify="space-between" wrap="nowrap" mb={6}>
                <Group gap={8} wrap="nowrap">
                  <IconWebhook size={18} />
                  <Text fw={600} truncate>{h.name}</Text>
                  <Badge size="sm" variant="light" color={KIND[h.kind].color}>{KIND[h.kind].label}</Badge>
                  {!h.enabled && <Badge size="sm" variant="light" color="gray">disabled</Badge>}
                </Group>
                <Group gap={6} wrap="nowrap">
                  <Switch checked={h.enabled} aria-label="Enabled"
                    onChange={e => api.setHookEnabled(h.token, e.currentTarget.checked).then(load).catch(toastErr)} />
                  <Button size="xs" variant="light" color="orange" leftSection={<IconPlayerPlay size={14} />}
                    disabled={!h.enabled || !settings.enabled} onClick={() => setCalling(row)}>Call now</Button>
                  <Menu position="bottom-end" withArrow>
                    <Menu.Target><ActionIcon variant="subtle" color="gray" aria-label="Actions"><IconDots size={16} /></ActionIcon></Menu.Target>
                    <Menu.Dropdown>
                      <Menu.Item leftSection={<IconPencil size={14} />} onClick={() => setEdit(h)}>Edit</Menu.Item>
                      <Menu.Item leftSection={<IconRefresh size={14} />} onClick={async () => {
                        if (!await confirmDelete(`New URL for "${h.name}"?`, "The current URL stops working immediately.")) return;
                        api.regenerateHook(h.token).then(() => { toastOk("New URL created"); load(); }).catch(toastErr);
                      }}>New URL (token)</Menu.Item>
                      <Menu.Item color="red" leftSection={<IconTrash size={14} />} onClick={async () => {
                        if (!await confirmDelete(`Delete hook "${h.name}"?`, "Running instances stay and expire as planned.")) return;
                        api.deleteHook(h.token).then(load).catch(toastErr);
                      }}>Delete</Menu.Item>
                    </Menu.Dropdown>
                  </Menu>
                </Group>
              </Group>
              <Text size="xs" c="dimmed" mb="sm">
                {row.source} · onto {targetName(h.targetId)} · {h.expireDays < 0 ? "instances are kept" : `instances deleted after ${h.expireDays}d`}
                {h.bindDomain && h.domainFormat ? ` · ${h.domainFormat}` : ""}
              </Text>
              <CurlBlock text={curlFor(origin, row.webhookPath, h.params)} />
              {row.instances.length > 0 && (
                <Stack gap={4} mt="sm">
                  <Text size="xs" fw={600}>Instances ({row.instances.length})</Text>
                  {row.instances.map(i => (
                    <Group key={i.stackId} gap={8} wrap="nowrap">
                      <span style={{ width: 8, height: 8, borderRadius: "50%", flexShrink: 0, background: `var(--mantine-color-${hostingColor(i.state ?? "stopped")}-6)` }} />
                      <Anchor size="sm" onClick={() => nav(`/app/${i.stackId}`)}>{i.name}</Anchor>
                      <Text size="xs" c="dimmed">{i.state ?? "not deployed"}</Text>
                      <Tooltip label={i.expireAt ? new Date(i.expireAt).toLocaleString() : "no expiry"} withArrow>
                        <Badge size="xs" variant="light" color="orange" leftSection={<IconClockHour4 size={10} />}>{left(i.expireAt)}</Badge>
                      </Tooltip>
                    </Group>
                  ))}
                </Stack>
              )}
            </Card>
          );
        })}

        {data.readonly.length > 0 && (
          <Stack gap="sm">
            <Title order={5} fw={600}>Other webhooks</Title>
            <Text size="xs" c="dimmed">Managed where they belong — shown here so every URL is in one place.</Text>
            {data.readonly.map(r => (
              <Card key={r.webhookPath} withBorder padding="md">
                <Group justify="space-between" mb={6}>
                  <Group gap={8}><Text fw={600} size="sm">{r.name}</Text><Badge size="xs" variant="default">{r.kind === "git-push" ? "Push to deploy" : "Image update"}</Badge></Group>
                  <Anchor size="sm" onClick={() => nav(r.link)}>Manage →</Anchor>
                </Group>
                <CurlBlock text={`curl -X POST "${origin}${r.webhookPath}"`} />
              </Card>
            ))}
          </Stack>
        )}
      </Stack>

      {edit && <HookEditModal initial={edit} overview={data} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); load(); }} />}
      {calling && <HookCallModal row={calling} targetName={targetName(calling.hook.targetId)} onClose={() => setCalling(null)} onDone={load} />}
    </PageShell>
  );
}
