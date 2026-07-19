import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { AppShell, Group, Title, Button, Container, TextInput, PasswordInput, Stack as MStack, Text, Alert } from "@mantine/core";
import { IconArrowLeft, IconCheck } from "@tabler/icons-react";
import type { AppSettings } from "../model";
import * as api from "../api";

const EMPTY: AppSettings = { aiBaseUrl: "", aiApiKey: "", aiModel: "", aiProviderLabel: "" };

export function Settings() {
  const nav = useNavigate();
  const [settings, setSettings] = useState<AppSettings>(EMPTY);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);

  useEffect(() => { api.getSettings().then(setSettings); }, []);

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
            Configure the AI provider used by the assistant. Any OpenAI-compatible endpoint works, including
            LocalAI, Ollama, and OpenAI itself.
          </Text>

          <MStack gap="md">
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
            <TextInput
              label="Provider label"
              placeholder="e.g. OpenAI, Ollama, LocalAI"
              value={settings.aiProviderLabel ?? ""}
              onChange={e => setSettings({ ...settings, aiProviderLabel: e.currentTarget.value })}
            />

            {saved && (
              <Alert color="green" icon={<IconCheck size={16} />} variant="light">
                Settings saved.
              </Alert>
            )}

            <Group justify="flex-end">
              <Button onClick={save} loading={saving}>Save</Button>
            </Group>
          </MStack>
        </Container>
      </AppShell.Main>
    </AppShell>
  );
}
