import { useEffect, useState } from "react";
import { Modal, Stack, Group, Button, TextInput, PasswordInput, Select, NumberInput, Switch, Table, Text, SegmentedControl, Title } from "@mantine/core";
import { IconSearch, IconWebhook } from "@tabler/icons-react";
import * as api from "../api";
import type { ContainerPreset, Stack as StackT, Deployment } from "../model";
import { isSecretName, scanEnv } from "../hosting/GitImportModal";
import { toastErr, toastOk } from "../ui";
import { unmask } from "./curl";

const MODES: { value: api.HookParamMode; label: string }[] = [
  { value: "fixed", label: "Fixed" },
  { value: "required", label: "From call (required)" },
  { value: "optional", label: "From call (optional)" },
  { value: "generated", label: "Generated per call" },
];

export function HookEditModal({ initial, overview, onClose, onSaved }: {
  initial: Partial<api.Hook> & { kind: api.HookKind }; overview: api.HooksOverview; onClose: () => void; onSaved: () => void;
}) {
  const editing = !!initial.token;
  const [h, setH] = useState<Partial<api.Hook> & { kind: api.HookKind }>({ expireDays: 7, bindDomain: false, enabled: true, params: [], ...initial });
  const [presets, setPresets] = useState<ContainerPreset[]>([]);
  const [hosted, setHosted] = useState<{ value: string; label: string }[]>([]);
  const [busy, setBusy] = useState(false);
  const set = (p: Partial<api.Hook>) => setH(s => ({ ...s, ...p }));
  const params = h.params ?? [];
  const setParam = (key: string, p: Partial<api.HookParam>) =>
    set({ params: params.map(x => x.key === key ? { ...x, ...p } : x) });

  useEffect(() => {
    if (h.kind === "store") api.getPresets().then(setPresets).catch(toastErr);
    if (h.kind === "clone")
      Promise.all([api.listStacks() as Promise<StackT[]>, api.listHosting() as Promise<Deployment[]>]).then(([stacks, deps]) =>
        setHosted(stacks.filter(s => deps.some(d => d.stackId === s.id) && !s.hookToken).map(s => ({ value: s.id, label: s.name }))))
        .catch(toastErr);
  }, [h.kind]);
  useEffect(() => {
    if (h.kind === "clone" && h.sourceStackId && !h.name) {
      const label = hosted.find(x => x.value === h.sourceStackId)?.label;
      if (label) setH(s => ({ ...s, name: s.name || label }));
    }
  }, [hosted]); // eslint-disable-line react-hooks/exhaustive-deps

  const preset = presets.find(p => p.id === h.appId);
  const pickApp = (id: string | null) => {
    const p = presets.find(x => x.id === id);
    if (!p) return;
    set({
      appId: p.id, name: !h.name || h.name === preset?.label ? p.label : h.name, sourceId: p.sources?.find(s => s.default)?.id ?? p.sources?.[0]?.id ?? null,
      params: (p.params ?? []).map(x => ({
        key: x.key, secret: !!x.secret,
        mode: x.secret && x.generate !== false ? "generated" : "fixed",
        value: x.secret && x.generate !== false ? "" : x.default ?? "",
      })),
    });
  };

  const inspect = async () => {
    if (!h.repo?.trim()) return;
    setBusy(true);
    try {
      const d = await api.gitInspect({ url: h.repo.trim(), subdir: h.subdir ?? undefined, authToken: h.authToken && h.authToken !== api.MASKED ? h.authToken : undefined });
      const mode = d.manifest ? "manifest" : d.hasCompose ? "compose" : d.hasAppHost ? "apphost" : d.hasDockerfile ? "dockerfile" : "";
      const env = mode === "compose" ? scanEnv(d.composeFiles.map(f => f.content)).map(e => ({ key: e.name, value: e.def, secret: e.secret }))
        : mode === "dockerfile" ? (d.dockerfile?.env ?? []).map(e => ({ key: e.key, value: e.value, secret: isSecretName(e.key) }))
        : [];
      const keep = params.filter(p => p.key === "repo" || p.key === "branch");
      set({
        mode, name: h.name || d.name || "git app", image: d.dockerfile?.suggestedImage ?? h.image, port: d.dockerfile?.port ?? h.port,
        params: [
          ...(keep.some(p => p.key === "branch") ? keep : [...keep, { key: "branch", mode: "optional" as const, value: "" }]),
          ...env.map(e => ({ key: e.key, secret: e.secret, mode: (e.secret && !e.value ? "generated" : "fixed") as api.HookParamMode, value: e.value })),
        ],
      });
      toastOk(`Detected ${mode || "nothing"}`);
    } catch (e) {
      toastErr(h.authToken === api.MASKED ? new Error(`${e instanceof Error ? e.message : e} — re-enter the access token to analyze a private repository.`) : e);
    } finally { setBusy(false); }
  };

  const save = async () => {
    setBusy(true);
    try {
      if (editing) await api.updateHook(h.token!, h as api.Hook);
      else await api.createHook(h as Omit<api.Hook, "token">);
      toastOk(editing ? "Hook saved" : "Hook created");
      onSaved();
    } catch (e) { toastErr(e); } finally { setBusy(false); }
  };

  const target = overview.targets.find(t => t.id === (h.targetId ?? "local"));
  const canDomain = overview.npmConfigured && (target?.domains ?? overview.targets.some(t => t.domains));
  const valid = !!h.name?.trim() && (h.kind === "clone" ? !!h.sourceStackId : h.kind === "store" ? !!h.appId : !!h.repo?.trim() || params.some(p => p.key === "repo" && p.mode !== "fixed"))
    && (!h.bindDomain || !!h.domainFormat?.trim());

  return (
    <Modal opened onClose={onClose} size="xl" title={<Group gap={8}><IconWebhook size={18} /><Title order={5}>{editing ? `Edit ${h.name}` : "New hook"}</Title></Group>}>
      <Stack gap="md">
        {!editing && (
          <SegmentedControl value={h.kind} onChange={v => setH({ kind: v as api.HookKind, expireDays: h.expireDays, bindDomain: false, enabled: true, params: v === "git" ? [{ key: "branch", mode: "optional", value: "" }] : [] })}
            data={[{ value: "clone", label: "Clone an app" }, { value: "store", label: "Install from store" }, { value: "git", label: "Install from Git" }]} />
        )}

        {h.kind === "clone" && (
          <Select label="App to clone" searchable data={hosted} value={h.sourceStackId ?? null} disabled={editing}
            onChange={v => set({ sourceStackId: v, name: h.name || hosted.find(x => x.value === v)?.label || "" })} />
        )}
        {h.kind === "store" && (
          <Group grow align="flex-end">
            <Select label="App" searchable leftSection={<IconSearch size={14} />} value={h.appId ?? null} onChange={pickApp}
              data={presets.map(p => ({ value: p.id, label: p.label }))} />
            {(preset?.sources?.length ?? 0) > 1 && (
              <Select label="Source" value={h.sourceId ?? null} onChange={v => set({ sourceId: v })}
                data={(preset?.sources ?? []).map(s => ({ value: s.id, label: s.label }))} />
            )}
          </Group>
        )}
        {h.kind === "git" && (
          <Stack gap="xs">
            <Group grow align="flex-end">
              <TextInput label="Repository URL" placeholder="https://github.com/acme/app.git" value={h.repo ?? ""} onChange={e => set({ repo: e.currentTarget.value })}
                description="Leave empty if every call names its own repo (parameter repo)." />
              <TextInput label="Subdirectory" value={h.subdir ?? ""} onChange={e => set({ subdir: e.currentTarget.value || null })} />
            </Group>
            <Group grow align="flex-end">
              <PasswordInput label="Access token" description="Only for private repositories." value={h.authToken ?? ""} onChange={e => set({ authToken: unmask(e.currentTarget.value) || null })} />
              <Button variant="light" loading={busy} disabled={!h.repo?.trim()} onClick={inspect}>Analyze repository</Button>
            </Group>
            {h.mode && <Text size="xs" c="dimmed">Mode: <b>{h.mode}</b>{h.mode === "dockerfile" && ` · image ${h.image || "built from Dockerfile"} · port ${h.port ?? "?"}`}</Text>}
            {!params.some(p => p.key === "repo") && (
              <Button size="compact-xs" variant="subtle" w="fit-content" onClick={() => set({ params: [{ key: "repo", mode: "optional", value: h.repo ?? "" }, ...params] })}>
                Let calls override the repository
              </Button>
            )}
          </Stack>
        )}

        {h.kind !== "clone" && params.length > 0 && (
          <Table verticalSpacing={6} fz="sm">
            <Table.Thead><Table.Tr><Table.Th>Parameter</Table.Th><Table.Th w={210}>Value comes from</Table.Th><Table.Th>Value / default</Table.Th></Table.Tr></Table.Thead>
            <Table.Tbody>
              {params.map(p => (
                <Table.Tr key={p.key}>
                  <Table.Td><Text size="sm" ff="monospace">{p.key}</Text></Table.Td>
                  <Table.Td>
                    <Select size="xs" allowDeselect={false} data={MODES} value={p.mode} onChange={v => v && setParam(p.key, { mode: v as api.HookParamMode })} />
                  </Table.Td>
                  <Table.Td>
                    {(p.mode === "fixed" || p.mode === "optional") && (p.secret
                      ? <PasswordInput size="xs" value={p.value ?? ""} onChange={e => setParam(p.key, { value: unmask(e.currentTarget.value) })} />
                      : <TextInput size="xs" value={p.value ?? ""} onChange={e => setParam(p.key, { value: e.currentTarget.value })} />)}
                    {p.mode === "required" && <Text size="xs" c="dimmed">Every call must send it.</Text>}
                    {p.mode === "generated" && <Text size="xs" c="dimmed">A new random value for each instance.</Text>}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}

        <Group grow align="flex-start">
          <TextInput label="Name" description="Instances are named <name>-<id>." value={h.name ?? ""} onChange={e => set({ name: e.currentTarget.value })} />
          <NumberInput label="Delete instances after (days)" description="-1 = keep forever" min={-1} max={365} value={h.expireDays ?? 7}
            onChange={v => set({ expireDays: Number(v) })} />
          {overview.targets.length > 1 && (
            <Select label="Deploy onto" clearable placeholder="Default target" value={h.targetId ?? null} onChange={v => set({ targetId: v })}
              data={overview.targets.map(t => ({ value: t.id, label: t.name }))} />
          )}
        </Group>
        {!canDomain && (
          <Text size="xs" c="dimmed">Binding a domain (e.g. <code>demo-{"{id}"}-{"{name}"}.example.org</code>) needs Nginx Proxy Manager on the target — set it up under Settings → Deploy targets → Domains.</Text>
        )}
        {canDomain && (
          <Group grow align="flex-end">
            <Switch label="Bind a domain via Nginx Proxy Manager" checked={!!h.bindDomain} onChange={e => set({ bindDomain: e.currentTarget.checked })} />
            {h.bindDomain && (
              <TextInput label="Domain pattern" placeholder="preview-{id}.example.com" description="{id} and {name} are replaced per instance."
                value={h.domainFormat ?? ""} onChange={e => set({ domainFormat: e.currentTarget.value })} />
            )}
          </Group>
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>Cancel</Button>
          <Button loading={busy} disabled={!valid} onClick={save}>{editing ? "Save" : "Create hook"}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
