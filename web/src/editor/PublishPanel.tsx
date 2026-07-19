import { useState } from "react";
import { Stack as MStack, Group, Button, ScrollArea, Text, Code, CopyButton, Tooltip, Alert } from "@mantine/core";
import { IconPackageExport, IconDownload, IconRocket, IconPlayerStop, IconInfoCircle } from "@tabler/icons-react";
import { PrismLight as SyntaxHighlighter } from "react-syntax-highlighter";
import yaml from "react-syntax-highlighter/dist/esm/languages/prism/yaml";
import { oneDark } from "react-syntax-highlighter/dist/esm/styles/prism";
import JSZip from "jszip";
import { useEditor } from "./DockLayout";
import type { PublishResult, DeployResult } from "../model";
import * as api from "../api";

SyntaxHighlighter.registerLanguage("yaml", yaml);

function extractMessage(err: unknown): string {
  const msg = err instanceof Error ? err.message : String(err);
  const body = msg.slice(msg.indexOf(": ") + 2);
  try {
    const parsed = JSON.parse(body);
    if (parsed && typeof parsed === "object" && typeof parsed.message === "string") return parsed.message;
  } catch { /* not JSON */ }
  return body;
}

function download(name: string, blob: Blob) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  a.click();
  URL.revokeObjectURL(url);
}

// Publish the stack to Docker Compose (via `aspire publish`), view the generated
// docker-compose.yaml/.env, download the bundle, and optionally deploy it locally.
export function PublishPanel() {
  const { stack } = useEditor();
  const [result, setResult] = useState<PublishResult | null>(null);
  const [deployLog, setDeployLog] = useState<string | null>(null);
  const [busy, setBusy] = useState<null | "publish" | "up" | "down">(null);

  const run = async (kind: "publish" | "up" | "down", call: () => Promise<PublishResult | DeployResult>) => {
    setBusy(kind);
    try {
      const r = await call();
      if (kind === "publish") { setResult(r as PublishResult); setDeployLog(null); }
      else setDeployLog(r.log || (r.ok ? "done." : "failed."));
    } catch (err) {
      const m = extractMessage(err);
      if (kind === "publish") setResult({ ok: false, log: m, composeYaml: null, envFile: null, outputDir: "" });
      else setDeployLog(m);
    } finally {
      setBusy(null);
    }
  };

  const downloadBundle = async () => {
    if (!result?.composeYaml) return;
    const zip = new JSZip();
    zip.file("docker-compose.yaml", result.composeYaml);
    if (result.envFile) zip.file(".env", result.envFile);
    download(`${stack.name || "stack"}-compose.zip`, await zip.generateAsync({ type: "blob" }));
  };

  return (
    <ScrollArea style={{ height: "100%" }} px="sm" py="xs">
      <MStack gap="sm">
        <Group gap="xs">
          <Button size="xs" leftSection={<IconPackageExport size={14} />}
            loading={busy === "publish"} disabled={busy !== null}
            onClick={() => void run("publish", () => api.publishStack(stack.id))}>
            Publish (Docker Compose)
          </Button>
          <Tooltip withArrow multiline w={260}
            label="Runs `aspire publish` to generate docker-compose.yaml. Deploying it locally needs Docker running on this host.">
            <IconInfoCircle size={16} style={{ opacity: 0.6 }} />
          </Tooltip>
        </Group>

        {result && !result.ok && (
          <Alert color="red" title="Publish failed" icon={<IconInfoCircle size={16} />}>
            <Code block style={{ whiteSpace: "pre-wrap", fontSize: 11 }}>{result.log || "unknown error"}</Code>
          </Alert>
        )}

        {result?.ok && result.composeYaml && (
          <>
            <Group justify="space-between">
              <Text size="xs" fw={600} c="dimmed">docker-compose.yaml</Text>
              <Group gap={4}>
                <CopyButton value={result.composeYaml}>
                  {({ copied, copy }) => <Button size="compact-xs" variant="subtle" onClick={copy}>{copied ? "Copied" : "Copy"}</Button>}
                </CopyButton>
                <Button size="compact-xs" variant="subtle" leftSection={<IconDownload size={12} />}
                  onClick={() => void downloadBundle()}>Download bundle</Button>
              </Group>
            </Group>
            <SyntaxHighlighter language="yaml" style={oneDark}
              customStyle={{ margin: 0, background: "transparent", fontSize: 12 }} wrapLongLines>
              {result.composeYaml}
            </SyntaxHighlighter>

            {result.envFile && (
              <>
                <Text size="xs" fw={600} c="dimmed">.env (fill in parameter values before deploying)</Text>
                <Code block style={{ whiteSpace: "pre-wrap", fontSize: 11 }}>{result.envFile}</Code>
              </>
            )}

            <Text size="xs" c="dimmed">Deploy manually: <Code>cd {result.outputDir} && docker compose up -d</Code></Text>

            <Group gap="xs">
              <Button size="xs" color="green" leftSection={<IconRocket size={14} />}
                loading={busy === "up"} disabled={busy !== null}
                onClick={() => void run("up", () => api.deployStack(stack.id))}>
                Deploy now (docker compose up -d)
              </Button>
              <Button size="xs" color="red" variant="light" leftSection={<IconPlayerStop size={14} />}
                loading={busy === "down"} disabled={busy !== null}
                onClick={() => void run("down", () => api.deployDown(stack.id))}>
                Stop (compose down)
              </Button>
            </Group>
          </>
        )}

        {deployLog !== null && (
          <>
            <Text size="xs" fw={600} c="dimmed">docker compose output</Text>
            <Code block style={{ whiteSpace: "pre-wrap", fontSize: 11 }}>{deployLog || "(no output)"}</Code>
          </>
        )}
      </MStack>
    </ScrollArea>
  );
}
