import { useState } from "react";
import { Stack as MStack, Group, Button, ScrollArea, Text, Code, CopyButton, Alert, Menu, Badge, Box, Card, Radio, Anchor, LoadingOverlay, ThemeIcon, useMantineColorScheme } from "@mantine/core";
import { IconPackageExport, IconDownload, IconRocket, IconPlayerStop, IconInfoCircle, IconChevronDown, IconServer, IconExternalLink } from "@tabler/icons-react";
import { PrismLight as SyntaxHighlighter } from "react-syntax-highlighter";
import yaml from "react-syntax-highlighter/dist/esm/languages/prism/yaml";
import json from "react-syntax-highlighter/dist/esm/languages/prism/json";
import { oneDark, oneLight } from "react-syntax-highlighter/dist/esm/styles/prism";
import JSZip from "jszip";
import { useEditor } from "./DockLayout";
import type { PublishResult, DeployResult } from "../model";
import type { PublishTarget } from "../api";
import * as api from "../api";

SyntaxHighlighter.registerLanguage("yaml", yaml);
SyntaxHighlighter.registerLanguage("json", json);

const TARGETS: { id: PublishTarget; label: string; hint: string }[] = [
  { id: "compose", label: "Docker Compose", hint: "docker-compose.yaml + .env — deploy locally, or drop into Portainer/Coolify." },
  { id: "kubernetes", label: "Kubernetes (Helm)", hint: "A Helm chart (Chart.yaml, values.yaml, templates/*). Uses the preview Kubernetes publisher." },
  { id: "bicep", label: "Azure Bicep", hint: "main.bicep + per-resource modules for Azure Container Apps (azd / az deployment)." },
  { id: "manifest", label: "Aspire Manifest", hint: "aspire-manifest.json — a portable deployment descriptor other tools consume." },
];

function extractMessage(err: unknown): string {
  const msg = err instanceof Error ? err.message : String(err);
  const body = msg.slice(msg.indexOf(": ") + 2);
  try { const p = JSON.parse(body); if (Array.isArray(p)) return p.join("\n"); if (typeof p === "string") return p; if (p?.message) return p.message; } catch { /* raw */ }
  return body;
}
function download(name: string, blob: Blob) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a"); a.href = url; a.download = name; a.click();
  URL.revokeObjectURL(url);
}
function langFor(name: string | null): string {
  if (!name) return "yaml";
  if (name.endsWith(".json")) return "json";
  if (name.endsWith(".bicep")) return "text";
  return "yaml";
}

// Where a hosting deployment runs. Only "local" works today; the rest are placeholders so the picker
// already shows the intended shape (remote hosts / clusters come later).
const DEPLOY_TARGETS = [
  { id: "local", label: "This machine", hint: "Deploy with Docker on the machine AspireUI runs on.", enabled: true },
  { id: "remote", label: "Remote host (SSH)", hint: "A Docker host over SSH — coming soon.", enabled: false },
  { id: "swarm", label: "Cluster / Swarm", hint: "A multi-node cluster — coming soon.", enabled: false },
];

export function PublishPanel() {
  const { stack, deployToHosting, stopHosting, hostingBusy } = useEditor();
  const { colorScheme } = useMantineColorScheme();
  const [result, setResult] = useState<PublishResult | null>(null);
  const [deploy, setDeploy] = useState<DeployResult | null>(null);
  const [busy, setBusy] = useState<null | "publish" | "up" | "down">(null);
  const [target, setTarget] = useState<PublishTarget>("compose");
  const [pickTarget, setPickTarget] = useState(false);   // hosting target-picker overlay open?
  const [deployTarget, setDeployTarget] = useState("local");
  const dep = stack.deployment;
  const depColor = dep?.state === "running" ? "green" : dep?.state === "failed" ? "red" : dep?.state === "deploying" ? "yellow" : "gray";

  const publish = async (t: PublishTarget) => {
    setTarget(t); setBusy("publish"); setDeploy(null);
    try { setResult(await api.publishStack(stack.id, t)); }
    catch (err) { setResult({ ok: false, log: extractMessage(err), artifactName: null, artifact: null, outputDir: "", files: [] }); }
    finally { setBusy(null); }
  };
  const runDeploy = async (kind: "up" | "down", call: () => Promise<DeployResult>) => {
    setBusy(kind);
    try { setDeploy(await call()); }
    catch (err) { setDeploy({ ok: false, log: extractMessage(err) }); }
    finally { setBusy(null); }
  };

  const isCompose = result?.artifactName === "docker-compose.yaml";
  const envFile = result?.files.find(f => f.name === ".env");
  const otherFiles = result?.files.filter(f => f.name !== result.artifactName && f.name !== ".env") ?? [];

  const downloadBundle = async () => {
    if (!result?.files.length) return;
    const zip = new JSZip();
    result.files.forEach(f => zip.file(f.name, f.content));
    download(`${stack.name || "stack"}-${target}.zip`, await zip.generateAsync({ type: "blob" }));
  };

  return (
    <ScrollArea style={{ height: "100%" }} px="sm" py="xs">
      <MStack gap="sm">
        {/* Hosting: the appliance path — deploy this stack as a tracked, persistent deployment. */}
        <Card withBorder padding="sm" radius="md" pos="relative">
          <LoadingOverlay visible={hostingBusy} overlayProps={{ blur: 1 }} loaderProps={{ children: <Text size="sm">Deploying…</Text> }} />
          <Group justify="space-between" mb={dep ? 6 : 0}>
            <Group gap={6}><IconServer size={16} /><Text fw={600} size="sm">Hosting</Text></Group>
            {dep && <Badge color={depColor} variant="light" size="sm">{dep.state}</Badge>}
          </Group>

          {dep ? (
            <MStack gap={6}>
              {dep.urls.length > 0 && (
                <Group gap={8}>{dep.urls.map(u => <Anchor key={u} href={u} target="_blank" size="xs">{u} <IconExternalLink size={10} /></Anchor>)}</Group>
              )}
              {dep.state === "failed" && dep.lastError && (
                <Alert color="red" p="xs" icon={<IconInfoCircle size={14} />}>
                  <Code block style={{ whiteSpace: "pre-wrap", fontSize: 11, maxHeight: 160, overflow: "auto" }}>{dep.lastError.trim().split("\n").slice(-12).join("\n")}</Code>
                </Alert>
              )}
              <Group gap="xs">
                {dep.state === "running" || dep.state === "deploying"
                  ? <Button size="xs" color="orange" variant="light" leftSection={<IconPlayerStop size={14} />} onClick={() => void stopHosting()}>Stop</Button>
                  : <Button size="xs" color="teal" leftSection={<IconRocket size={14} />} onClick={() => void deployToHosting()}>Re-deploy</Button>}
              </Group>
            </MStack>
          ) : pickTarget ? (
            <Box>
              <Text size="xs" c="dimmed" mb={6}>Choose a deployment target:</Text>
              <Radio.Group value={deployTarget} onChange={setDeployTarget}>
                <MStack gap={6}>
                  {DEPLOY_TARGETS.map(t => (
                    <Radio key={t.id} value={t.id} disabled={!t.enabled}
                      label={<span>{t.label}{!t.enabled && <Badge size="xs" variant="light" color="gray" ml={6}>soon</Badge>}<Text size="10px" c="dimmed">{t.hint}</Text></span>} />
                  ))}
                </MStack>
              </Radio.Group>
              <Group gap="xs" mt="sm">
                <Button size="xs" color="teal" leftSection={<IconRocket size={14} />}
                  onClick={() => { setPickTarget(false); void deployToHosting(); }}>Deploy</Button>
                <Button size="xs" variant="default" onClick={() => setPickTarget(false)}>Cancel</Button>
              </Group>
            </Box>
          ) : (
            <Group gap="sm" wrap="nowrap" align="flex-start">
              <ThemeIcon size={38} radius="md" variant="light" color="teal"><IconRocket size={20} /></ThemeIcon>
              <Box style={{ flex: 1 }}>
                <Text size="xs" c="dimmed" mb={8}>Deploy this stack as a persistent, tracked app (install &amp; forget). It gets a URL and can be started/stopped from Hosting.</Text>
                <Button size="xs" color="teal" leftSection={<IconRocket size={14} />} onClick={() => setPickTarget(true)}>Deploy to hosting</Button>
              </Box>
            </Group>
          )}
        </Card>

        <Text size="xs" c="dimmed" fw={600} mt={4}>Or export / publish artifacts</Text>
        <Group gap={0} wrap="nowrap">
          <Button size="xs" leftSection={<IconPackageExport size={14} />} loading={busy === "publish"} disabled={busy !== null}
            onClick={() => void publish(target)} style={{ borderTopRightRadius: 0, borderBottomRightRadius: 0 }}>
            Publish · {TARGETS.find(t => t.id === target)!.label}
          </Button>
          <Menu position="bottom-end" withArrow>
            <Menu.Target>
              <Button size="xs" px={6} disabled={busy !== null} style={{ borderTopLeftRadius: 0, borderBottomLeftRadius: 0 }} aria-label="Choose target">
                <IconChevronDown size={14} />
              </Button>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Label>Publish target</Menu.Label>
              {TARGETS.map(t => (
                <Menu.Item key={t.id} onClick={() => void publish(t.id)}>{t.label}</Menu.Item>
              ))}
            </Menu.Dropdown>
          </Menu>
        </Group>
        <Text size="xs" c="dimmed">{TARGETS.find(t => t.id === target)!.hint}</Text>

        {result && !result.ok && (
          <Alert color="red" title="Publish failed" icon={<IconInfoCircle size={16} />}>
            <Code block style={{ whiteSpace: "pre-wrap", fontSize: 11 }}>{result.log || "unknown error"}</Code>
          </Alert>
        )}

        {result?.ok && result.artifact && (
          <>
            <Group justify="space-between">
              <Text size="xs" fw={600} c="dimmed">{result.artifactName}</Text>
              <Group gap={4}>
                <CopyButton value={result.artifact}>
                  {({ copied, copy }) => <Button size="compact-xs" variant="subtle" onClick={copy}>{copied ? "Copied" : "Copy"}</Button>}
                </CopyButton>
                <Button size="compact-xs" variant="subtle" leftSection={<IconDownload size={12} />}
                  onClick={() => void downloadBundle()}>Download bundle</Button>
              </Group>
            </Group>
            <SyntaxHighlighter language={langFor(result.artifactName)} style={colorScheme === "light" ? oneLight : oneDark}
              customStyle={{ margin: 0, background: "transparent", fontSize: 12 }} wrapLongLines>
              {result.artifact}
            </SyntaxHighlighter>

            {otherFiles.length > 0 && (
              <Text size="xs" c="dimmed">+ {otherFiles.length} more file(s) in the bundle: {otherFiles.slice(0, 6).map(f => <Badge key={f.name} size="xs" variant="light" mr={4}>{f.name}</Badge>)}{otherFiles.length > 6 ? "…" : ""}</Text>
            )}

            {envFile && (
              <>
                <Text size="xs" fw={600} c="dimmed">.env (fill in parameter values before deploying)</Text>
                <Code block style={{ whiteSpace: "pre-wrap", fontSize: 11 }}>{envFile.content}</Code>
              </>
            )}

            {isCompose && (
              <>
                <Text size="xs" c="dimmed">Deploy manually: <Code>cd {result.outputDir} && docker compose up -d</Code></Text>
                <Group gap="xs">
                  <Button size="xs" color="green" leftSection={<IconRocket size={14} />} loading={busy === "up"} disabled={busy !== null}
                    onClick={() => void runDeploy("up", () => api.deployStack(stack.id))}>Deploy now (docker compose up -d)</Button>
                  <Button size="xs" color="red" variant="light" leftSection={<IconPlayerStop size={14} />} loading={busy === "down"} disabled={busy !== null}
                    onClick={() => void runDeploy("down", () => api.deployDown(stack.id))}>Stop (compose down)</Button>
                </Group>
              </>
            )}
          </>
        )}

        {deploy && (
          deploy.ok ? (
            <><Text size="xs" fw={600} c="dimmed">docker compose output</Text>
              <Code block style={{ whiteSpace: "pre-wrap", fontSize: 11 }}>{deploy.log || "(no output)"}</Code></>
          ) : (
            <Alert color="red" title="Deploy failed" icon={<IconInfoCircle size={16} />}>
              <Code block style={{ whiteSpace: "pre-wrap", fontSize: 11 }}>{deploy.log || "unknown error"}</Code>
            </Alert>
          )
        )}
      </MStack>
    </ScrollArea>
  );
}
