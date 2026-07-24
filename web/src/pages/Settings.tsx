import { useEffect, useState } from "react";
import { Group, Button, TextInput, PasswordInput, Stack as MStack, Text, Alert, SegmentedControl, Select, Autocomplete, Tabs, Badge, Loader, Switch, Code, CopyButton, ActionIcon, Anchor, Table, ScrollArea } from "@mantine/core";
import { IconCheck, IconPlugConnected, IconAlertCircle, IconRobot, IconServer2, IconLayoutDashboard, IconTrash, IconPlus, IconCopy, IconBrandDocker } from "@tabler/icons-react";
import { PageShell } from "../components/PageShell";
import { confirmDelete, toastOk, toastErr } from "../ui";
import type { AppSettings, EnvHealth, ApiToken, DockerImage, DockerVolume, DockerContainer } from "../model";
import { APP_VERSION, BUILD_INFO } from "../model";
import * as api from "../api";
import { useTitle } from "../useTitle";
import { useAuth } from "../auth/AuthContext";

// Admin: control whether the Aspire dashboard ships with hosting deployments, and set a browser token so
// AspireUI can hand out a one-click login link (works everywhere — no reverse proxy needed).
function HostingTab() {
  const [host, setHost] = useState(true);
  const [token, setToken] = useState("");
  const [publicHost, setPublicHost] = useState("");
  const [reqHost, setReqHost] = useState("");
  const [saved, setSaved] = useState(false);
  useEffect(() => { api.getDashboardSettings().then(s => { setHost(s.hostDashboard); setToken(s.dashboardToken); setPublicHost(s.publicHostSetting ?? ""); setReqHost(s.requestHost ?? ""); }).catch(() => {}); }, []);
  const save = async () => { await api.setDashboardSettings(host, token.trim(), publicHost.trim()); setSaved(true); setTimeout(() => setSaved(false), 2000); };
  return (
    <MStack gap="xl" maw={560}>
      <MStack gap="md">
        <Text fw={600}>Reachable host</Text>
        <TextInput label="Public host / IP" value={publicHost} onChange={e => setPublicHost(e.currentTarget.value)}
          placeholder={reqHost ? `blank = ${reqHost} (how you reached this page)` : "e.g. 192.168.1.50"}
          description="Host/IP the browser should use for direct app + dashboard port links. Set this to the machine's LAN IP when you reach AspireUI through a domain (proxies forward :443, not app ports). Blank = the host you opened AspireUI with." />
        <Text fw={600} mt="sm">Aspire dashboard</Text>
        <Switch checked={host} onChange={e => setHost(e.currentTarget.checked)}
          label="Include the Aspire dashboard in hosting deployments"
          description="Off = the dashboard container isn't published (deployed apps still run; you see resources/logs here in AspireUI)." />
        <TextInput label="Dashboard browser token" value={token} onChange={e => setToken(e.currentTarget.value)}
          placeholder="leave empty for Aspire's random per-start token"
          description="Set a fixed token and AspireUI shows a one-click dashboard login link. Anyone with this token can open the dashboard — treat it like a password." />
        <Group>
          <Button onClick={save} leftSection={saved ? <IconCheck size={16} /> : undefined}>{saved ? "Saved" : "Save"}</Button>
        </Group>
        <Text size="xs" c="dimmed">Changes apply on the next deploy/re-deploy of a stack.</Text>
      </MStack>
      <NpmSettingsSection />
    </MStack>
  );
}

// Connect AspireUI to the user's own Nginx Proxy Manager so hosted apps can get a real domain
// (managed from the hosting Domain dialog) without leaving AspireUI.
function NpmSettingsSection() {
  const [enabled, setEnabled] = useState(false);
  const [baseUrl, setBaseUrl] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [hasPassword, setHasPassword] = useState(false);
  const [forwardHost, setForwardHost] = useState("");
  const [detectedHost, setDetectedHost] = useState("");
  const [saved, setSaved] = useState(false);
  const [test, setTest] = useState<{ ok: boolean; error?: string | null } | null>(null);
  const [testing, setTesting] = useState(false);
  useEffect(() => { api.getNpmSettings().then(s => {
    setEnabled(s.enabled); setBaseUrl(s.baseUrl); setEmail(s.email); setForwardHost(s.forwardHost); setHasPassword(s.hasPassword); setDetectedHost(s.detectedHost ?? "");
  }).catch(() => {}); }, []);
  const body = () => ({ enabled, baseUrl: baseUrl.trim(), email: email.trim(), password: password || undefined, forwardHost: forwardHost.trim() });
  const save = async () => { await api.setNpmSettings(body()); setHasPassword(hasPassword || !!password); setPassword(""); setSaved(true); setTimeout(() => setSaved(false), 2000); };
  const runTest = async () => { setTesting(true); setTest(null); try { setTest(await api.testNpm(body())); } catch (e) { setTest({ ok: false, error: e instanceof Error ? e.message : String(e) }); } finally { setTesting(false); } };
  return (
    <MStack gap="md">
      <Group gap={10}>
        <img src="https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons/png/nginx-proxy-manager.png" width={22} height={22} alt="" style={{ display: "block" }} />
        <Text fw={600}>Nginx Proxy Manager</Text>
      </Group>
      <Text size="xs" c="dimmed">
        Point AspireUI at your <a href="https://nginxproxymanager.com" target="_blank" rel="noreferrer">Nginx Proxy Manager</a> and
        you can give any hosted app a real domain right from its <b>Domain</b> dialog — AspireUI prefills the
        forward host + port and can update an existing proxy entry.
      </Text>
      <Switch checked={enabled} onChange={e => setEnabled(e.currentTarget.checked)} label="Enable" />
      <TextInput label="NPM URL" placeholder="http://npm-host:81" value={baseUrl} onChange={e => setBaseUrl(e.currentTarget.value)}
        description="Your Nginx Proxy Manager admin URL (the login page)." disabled={!enabled} />
      <TextInput label="Email" placeholder="admin@example.com" value={email} onChange={e => setEmail(e.currentTarget.value)} disabled={!enabled} />
      <PasswordInput label="Password" placeholder={hasPassword ? "•••••••• (stored — leave blank to keep)" : "NPM admin password"}
        value={password} onChange={e => setPassword(e.currentTarget.value)} disabled={!enabled} />
      <TextInput label="Apps reachable at (forward host)" placeholder={detectedHost ? `${detectedHost} (detected — leave blank to use it)` : "e.g. 192.168.1.50"}
        value={forwardHost} onChange={e => setForwardHost(e.currentTarget.value)} disabled={!enabled}
        description={`The host/IP your NPM uses to reach the machine AspireUI publishes apps on. Blank = AspireUI uses ${detectedHost || "this server's detected LAN IP"} (never localhost).`} />
      {test && <Alert color={test.ok ? "green" : "red"} p="xs" icon={test.ok ? <IconCheck size={16} /> : <IconAlertCircle size={16} />}>
        {test.ok ? "Connected — NPM reachable and credentials valid." : `Failed: ${test.error}`}
      </Alert>}
      <Group>
        <Button onClick={save} leftSection={saved ? <IconCheck size={16} /> : undefined}>{saved ? "Saved" : "Save"}</Button>
        <Button variant="default" loading={testing} onClick={runTest} disabled={!enabled || !baseUrl.trim()}>Test connection</Button>
      </Group>
    </MStack>
  );
}

// API & Agents tab — personal access tokens + how to reach the REST API and the MCP server.
function ApiTab() {
  const [tokens, setTokens] = useState<ApiToken[] | null>(null);
  const [name, setName] = useState("");
  const [created, setCreated] = useState<string | null>(null);   // plaintext shown once
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const load = () => api.listApiTokens().then(setTokens).catch(() => setTokens([]));
  useEffect(() => { load(); }, []);
  const origin = window.location.origin;
  const create = async () => {
    setBusy(true); setErr(null);
    try { const r = await api.createApiToken(name.trim() || "token"); setCreated(r.token); setName(""); load(); }
    catch (e) { setErr(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  };
  const mcpJson = `{
  "mcpServers": {
    "aspireui": {
      "url": "${origin}/api/mcp",
      "headers": { "Authorization": "Bearer <your-token>" }
    }
  }
}`;
  return (
    <MStack gap="xl" maw={640}>
      <MStack gap="xs">
        <Text fw={600}>REST API &amp; MCP</Text>
        <Text size="sm" c="dimmed">
          Everything AspireUI does is a REST API. Browse it in the <Anchor href="/scalar" target="_blank">API reference</Anchor> (spec
          at <Anchor href="/openapi/v1.json" target="_blank">/openapi/v1.json</Anchor>). Agents can drive AspireUI over the
          built-in <b>MCP</b> server. Both accept a personal access token as <Code>Authorization: Bearer &lt;token&gt;</Code>.
        </Text>
        <Group gap="xs"><Text size="sm">API base:</Text><Code>{origin}/api</Code></Group>
        <Group gap="xs"><Text size="sm">MCP endpoint:</Text><Code>{origin}/api/mcp</Code><Text size="xs" c="dimmed">(for MCP clients — opening it in a browser shows nothing)</Text></Group>
      </MStack>

      <MStack gap="sm">
        <Text fw={600}>Personal access tokens</Text>
        <Group gap="xs">
          <TextInput style={{ flex: 1 }} placeholder="Token name (e.g. my-agent)" value={name} onChange={e => setName(e.currentTarget.value)}
            onKeyDown={e => { if (e.key === "Enter") create(); }} />
          <Button leftSection={<IconPlus size={16} />} loading={busy} onClick={create}>Create</Button>
        </Group>
        {err && <Alert color="red" p="xs" icon={<IconAlertCircle size={16} />}>{err}</Alert>}
        {created && (
          <Alert color="green" icon={<IconCheck size={16} />} title="Token created — copy it now, it won't be shown again">
            <Group gap="xs" wrap="nowrap">
              <Code style={{ flex: 1, wordBreak: "break-all" }}>{created}</Code>
              <CopyButton value={created}>{({ copied, copy }) => (
                <Button size="xs" variant={copied ? "filled" : "light"} color={copied ? "green" : "blue"} leftSection={<IconCopy size={14} />} onClick={copy}>{copied ? "Copied" : "Copy"}</Button>)}
              </CopyButton>
            </Group>
          </Alert>
        )}
        {tokens === null ? <Loader size="sm" /> : tokens.length === 0 ? <Text size="sm" c="dimmed">No tokens yet.</Text> : (
          <MStack gap={4}>
            {tokens.map(t => (
              <Group key={t.id} justify="space-between" wrap="nowrap" p="xs" style={{ border: "1px solid var(--mantine-color-default-border)", borderRadius: 8 }}>
                <div style={{ minWidth: 0 }}>
                  <Text size="sm" fw={600} truncate>{t.name} <Code>{t.prefix}…</Code></Text>
                  <Text size="xs" c="dimmed">created {new Date(t.createdAt).toLocaleDateString()}{t.lastUsed ? ` · last used ${new Date(t.lastUsed).toLocaleDateString()}` : " · never used"}</Text>
                </div>
                <ActionIcon variant="subtle" color="red" onClick={() => api.deleteApiToken(t.id).then(load)} aria-label="Revoke"><IconTrash size={16} /></ActionIcon>
              </Group>
            ))}
          </MStack>
        )}
      </MStack>

      <MStack gap="xs">
        <Text fw={600}>Connect an agent (MCP)</Text>
        <Text size="sm" c="dimmed">Add AspireUI to an MCP-capable agent (Claude, etc.). Replace the token with one from above:</Text>
        <div style={{ position: "relative" }}>
          <CopyButton value={mcpJson}>{({ copied, copy }) => (
            <Button size="compact-xs" variant="subtle" style={{ position: "absolute", top: 6, right: 6, zIndex: 1 }} onClick={copy}>{copied ? "Copied" : "Copy"}</Button>)}
          </CopyButton>
          <Code block style={{ fontSize: 12 }}>{mcpJson}</Code>
        </div>
      </MStack>
    </MStack>
  );
}

// Docker housekeeping (admin) — see + clean up the images/containers/volumes AspireUI created via the
// socket (dev-run + hosting). AspireUI's own container + data volume are protected (no remove button).
function DockerTab() {
  const [containers, setContainers] = useState<DockerContainer[] | null>(null);
  const [images, setImages] = useState<DockerImage[] | null>(null);
  const [volumes, setVolumes] = useState<DockerVolume[] | null>(null);
  const load = () => {
    api.dockerContainers().then(setContainers).catch(() => setContainers([]));
    api.dockerImages().then(setImages).catch(() => setImages([]));
    api.dockerVolumes().then(setVolumes).catch(() => setVolumes([]));
  };
  useEffect(() => { load(); }, []);
  const rm = (kind: "images" | "containers" | "volumes", id: string, label: string, warn?: string) =>
    confirmDelete(label, warn ?? "").then(ok => { if (ok) api.dockerRemove(kind, id).then(load).catch(e => toastErr(e, "Remove failed")); });
  const prune = (kind: "images" | "containers") =>
    confirmDelete(`unused ${kind}`, kind === "images" ? "Removes dangling images (not used by any container)." : "Removes all stopped containers.")
      .then(ok => { if (ok) api.dockerPrune(kind).then(() => { toastOk("Pruned"); load(); }).catch(e => toastErr(e, "Prune failed")); });
  const del = (protectedRow: boolean, onClick: () => void) =>
    <ActionIcon variant="subtle" color="red" size="sm" disabled={protectedRow} onClick={onClick} aria-label="Remove"><IconTrash size={15} /></ActionIcon>;

  return (
    <MStack gap="xl" maw={640}>
      <Text size="xs" c="dimmed">Everything Docker built through AspireUI (dev runs + hosting pull images and create containers/volumes). Clean up here. AspireUI's own container and <Code>aspireui-data</Code> volume are protected.</Text>

      <MStack gap={6}>
        <Group justify="space-between"><Text fw={600}>Containers</Text><Button size="compact-xs" variant="light" color="orange" onClick={() => prune("containers")}>Prune stopped</Button></Group>
        {containers === null ? <Loader size="sm" /> : containers.length === 0 ? <Text size="sm" c="dimmed">None.</Text> : (
          <ScrollArea.Autosize mah={220}><Table fz="xs" verticalSpacing={4}><Table.Tbody>
            {containers.map(c => (
              <Table.Tr key={c.id}>
                <Table.Td><span style={{ display: "inline-block", width: 8, height: 8, borderRadius: 8, marginRight: 6, background: c.state === "running" ? "var(--mantine-color-green-6)" : "var(--mantine-color-gray-5)" }} />{c.name}</Table.Td>
                <Table.Td c="dimmed">{c.image}</Table.Td>
                <Table.Td c="dimmed">{c.ports || c.status}</Table.Td>
                <Table.Td w={32}>{del(c.protected, () => rm("containers", c.id, `container "${c.name}"`))}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody></Table></ScrollArea.Autosize>
        )}
      </MStack>

      <MStack gap={6}>
        <Group justify="space-between"><Text fw={600}>Images</Text><Button size="compact-xs" variant="light" color="orange" onClick={() => prune("images")}>Prune dangling</Button></Group>
        {images === null ? <Loader size="sm" /> : images.length === 0 ? <Text size="sm" c="dimmed">None.</Text> : (
          <ScrollArea.Autosize mah={220}><Table fz="xs" verticalSpacing={4}><Table.Tbody>
            {images.map(i => (
              <Table.Tr key={i.id + i.tag}>
                <Table.Td>{i.repository === "<none>" ? <Text c="dimmed" span>&lt;none&gt;</Text> : i.repository}<Text c="dimmed" span>:{i.tag}</Text></Table.Td>
                <Table.Td c="dimmed">{i.size}</Table.Td>
                <Table.Td c="dimmed">{i.created}</Table.Td>
                <Table.Td w={32}>{del(false, () => rm("images", i.id, `image ${i.repository}:${i.tag}`))}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody></Table></ScrollArea.Autosize>
        )}
      </MStack>

      <MStack gap={6}>
        <Text fw={600}>Volumes</Text>
        {volumes === null ? <Loader size="sm" /> : volumes.length === 0 ? <Text size="sm" c="dimmed">None.</Text> : (
          <ScrollArea.Autosize mah={220}><Table fz="xs" verticalSpacing={4}><Table.Tbody>
            {volumes.map(v => (
              <Table.Tr key={v.name}>
                <Table.Td style={{ wordBreak: "break-all" }}>{v.name}{v.protected && <Badge size="xs" variant="light" color="gray" ml={6}>protected</Badge>}</Table.Td>
                <Table.Td w={32}>{del(v.protected, () => rm("volumes", v.name, `volume "${v.name}" and its data`, "This deletes the volume's data (database/files) — cannot be undone."))}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody></Table></ScrollArea.Autosize>
        )}
      </MStack>
    </MStack>
  );
}

// Environment/about tab — server-side health of the tools a run needs, plus the build version.
function EnvTab() {
  const [env, setEnv] = useState<EnvHealth | null>(null);
  useEffect(() => { api.envHealth().then(setEnv).catch(() => {}); }, []);
  const row = (label: string, ok: boolean, detail: string) => (
    <Group justify="space-between">
      <Text size="sm">{label}</Text>
      <Group gap={8}>
        <Text size="xs" c="dimmed">{detail}</Text>
        <Badge size="sm" variant="light" color={ok ? "green" : "red"}>{ok ? "OK" : "missing"}</Badge>
      </Group>
    </Group>
  );
  return (
    <MStack gap="sm">
      <Text size="sm" c="dimmed">Tools the machine hosting AspireUI needs to run stacks.</Text>
      {!env ? <Loader size="sm" /> : (
        <>
          {row(".NET SDK", env.dotnet.ok, env.dotnet.version)}
          {row("Docker", env.docker.ok, env.docker.detail)}
          {row("Git", env.git.ok, env.git.detail)}
        </>
      )}
      <Group justify="space-between" mt="md">
        <Text size="sm">Version</Text>
        <Text size="xs" c="dimmed">v{APP_VERSION} · {BUILD_INFO}</Text>
      </Group>
    </MStack>
  );
}

const EMPTY: AppSettings = { aiBaseUrl: "", aiApiKey: "", aiModel: "", aiProviderLabel: "" };

export function Settings() {
  useTitle("Settings");
  const [settings, setSettings] = useState<AppSettings>(EMPTY);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [testing, setTesting] = useState(false);
  const [testResult, setTestResult] = useState<{ ok: boolean; model?: string; ms?: number; error?: string } | null>(null);
  const [cliTools, setCliTools] = useState<string[]>([]);
  const [models, setModels] = useState<string[]>([]);
  const [detecting, setDetecting] = useState(false);
  const [detectMsg, setDetectMsg] = useState<string | null>(null);
  const kind = settings.aiKind === "cli" ? "cli" : "http";
  const { status } = useAuth();
  const isAdmin = !!status?.user?.isAdmin;

  useEffect(() => { api.getSettings().then(setSettings); api.getAiCliTools().then(setCliTools).catch(() => {}); }, []);

  const detect = async () => {
    setDetecting(true); setDetectMsg(null);
    try {
      const r = await api.detectAiModels(settings);
      setModels(r.models);
      setDetectMsg(r.error ? `Couldn't detect: ${r.error}` : r.models.length ? `Found ${r.models.length} model(s).` : "No models reported — enter one manually.");
    } catch (e) { setDetectMsg(e instanceof Error ? e.message : String(e)); }
    finally { setDetecting(false); }
  };
  const modelField = (
    <Group gap="xs" align="end" wrap="nowrap">
      <Autocomplete style={{ flex: 1 }} label="Model" data={models}
        placeholder={kind === "cli" ? "e.g. llama3.2 (ollama/llm)" : "gpt-4o-mini"}
        value={settings.aiModel ?? ""} onChange={v => setSettings({ ...settings, aiModel: v })} />
      <Button variant="default" onClick={detect} loading={detecting}>Detect</Button>
    </Group>
  );

  const save = async () => {
    setSaving(true);
    setSaved(false);
    try {
      await api.saveSettings(settings);
      setSaved(true);
    } finally {
      setSaving(false);
    }
  };

  const test = async () => {
    setTesting(true);
    setTestResult(null);
    try { setTestResult(await api.testAi(settings)); }
    catch (e) { setTestResult({ ok: false, error: e instanceof Error ? e.message : String(e) }); }
    finally { setTesting(false); }
  };

  return (
    <PageShell title="Settings" container="md">
          <Tabs defaultValue="ai" orientation="vertical" variant="pills">
            <Tabs.List mr="lg">
              <Tabs.Tab value="ai" leftSection={<IconRobot size={15} />}>AI assistant</Tabs.Tab>
              {isAdmin && <Tabs.Tab value="hosting" leftSection={<IconLayoutDashboard size={15} />}>Hosting</Tabs.Tab>}
              {isAdmin && <Tabs.Tab value="docker" leftSection={<IconBrandDocker size={15} />}>Docker</Tabs.Tab>}
              <Tabs.Tab value="api" leftSection={<IconPlugConnected size={15} />}>API &amp; Agents</Tabs.Tab>
              <Tabs.Tab value="env" leftSection={<IconServer2 size={15} />}>Environment</Tabs.Tab>
            </Tabs.List>
            <Tabs.Panel value="ai" style={{ flex: 1 }}>
          <Text c="dimmed" size="sm" mb="lg">
            Configure the AI backend used by the assistant. Either an OpenAI-compatible HTTP endpoint
            (LocalAI, Ollama, OpenAI, …) or a locally-installed agent CLI on this server.
          </Text>

          <MStack gap="md">
            <SegmentedControl
              value={kind}
              onChange={v => setSettings({ ...settings, aiKind: v })}
              data={[{ label: "HTTP endpoint", value: "http" }, { label: "Local CLI", value: "cli" }]}
            />

            {kind === "http" ? (
              <>
                <TextInput
                  label="Base URL"
                  placeholder="http://localhost:8080 (your LocalAI/Ollama/OpenAI base URL, without /v1)"
                  value={settings.aiBaseUrl ?? ""}
                  onChange={e => setSettings({ ...settings, aiBaseUrl: e.currentTarget.value })}
                />
                <PasswordInput
                  label="API key"
                  placeholder="sk-..."
                  value={settings.aiApiKey ?? ""}
                  onChange={e => setSettings({ ...settings, aiApiKey: e.currentTarget.value })}
                />
                {modelField}
              </>
            ) : (
              <>
                <Select
                  label="CLI tool"
                  description="Must be installed and on PATH on the server running AspireUI."
                  placeholder="Pick an installed agent CLI"
                  data={cliTools}
                  value={settings.aiCliTool ?? null}
                  onChange={v => setSettings({ ...settings, aiCliTool: v })}
                />
                {modelField}
                <Text size="xs" c="dimmed">Model required for ollama/llm; ignored by claude/gemini/codex.</Text>
              </>
            )}
            {detectMsg && <Text size="xs" c="dimmed">{detectMsg}</Text>}

            <TextInput
              label="Provider label"
              placeholder="e.g. OpenAI, Ollama, Claude CLI"
              value={settings.aiProviderLabel ?? ""}
              onChange={e => setSettings({ ...settings, aiProviderLabel: e.currentTarget.value })}
            />

            {saved && (
              <Alert color="green" icon={<IconCheck size={16} />} variant="light">
                Settings saved.
              </Alert>
            )}
            {testResult && (
              <Alert color={testResult.ok ? "green" : "red"} variant="light"
                icon={testResult.ok ? <IconCheck size={16} /> : <IconAlertCircle size={16} />}>
                {testResult.ok
                  ? `Connection OK${testResult.model ? ` — model "${testResult.model}"` : ""}${testResult.ms != null ? ` (${testResult.ms} ms)` : ""}.`
                  : `Test failed: ${testResult.error}`}
              </Alert>
            )}

            <Group justify="space-between">
              <Button variant="default" leftSection={<IconPlugConnected size={16} />} onClick={test} loading={testing}>
                Test connection
              </Button>
              <Button onClick={save} loading={saving}>Save</Button>
            </Group>
          </MStack>
            </Tabs.Panel>
            {isAdmin && (
              <Tabs.Panel value="hosting" style={{ flex: 1 }}>
                <HostingTab />
              </Tabs.Panel>
            )}
            {isAdmin && (
              <Tabs.Panel value="docker" style={{ flex: 1 }}>
                <DockerTab />
              </Tabs.Panel>
            )}
            <Tabs.Panel value="api" style={{ flex: 1 }}>
              <ApiTab />
            </Tabs.Panel>
            <Tabs.Panel value="env" style={{ flex: 1 }}>
              <EnvTab />
            </Tabs.Panel>
          </Tabs>
    </PageShell>
  );
}
