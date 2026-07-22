import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { AppShell, Group, Title, Button, Container, TextInput, PasswordInput, Stack as MStack, Text, Alert, SegmentedControl, Select } from "@mantine/core";
import { IconArrowLeft, IconCheck, IconPlugConnected, IconAlertCircle } from "@tabler/icons-react";
import type { AppSettings } from "../model";
import * as api from "../api";

const EMPTY: AppSettings = { aiBaseUrl: "", aiApiKey: "", aiModel: "", aiProviderLabel: "" };

export function Settings() {
  const nav = useNavigate();
  const [settings, setSettings] = useState<AppSettings>(EMPTY);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [testing, setTesting] = useState(false);
  const [testResult, setTestResult] = useState<{ ok: boolean; model?: string; ms?: number; error?: string } | null>(null);
  const [cliTools, setCliTools] = useState<string[]>([]);
  const kind = settings.aiKind === "cli" ? "cli" : "http";

  useEffect(() => { api.getSettings().then(setSettings); api.getAiCliTools().then(setCliTools).catch(() => {}); }, []);

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
    <AppShell header={{ height: 56 }} padding="lg">
      <AppShell.Header>
        <Group h="100%" px="md">
          <Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={() => nav("/")}>Stacks</Button>
          <Title order={4}>Settings</Title>
        </Group>
      </AppShell.Header>

      <AppShell.Main>
        <Container size="sm">
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
                <TextInput
                  label="Model"
                  placeholder="gpt-4o-mini"
                  value={settings.aiModel ?? ""}
                  onChange={e => setSettings({ ...settings, aiModel: e.currentTarget.value })}
                />
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
                <TextInput
                  label="Model"
                  description="Required for ollama/llm; ignored by claude/gemini/codex."
                  placeholder="e.g. llama3.2"
                  value={settings.aiModel ?? ""}
                  onChange={e => setSettings({ ...settings, aiModel: e.currentTarget.value })}
                />
              </>
            )}

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
        </Container>
      </AppShell.Main>
    </AppShell>
  );
}
