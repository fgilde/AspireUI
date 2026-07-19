import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  Alert, Button, Card, Center, Group, PasswordInput, Stack as MStack, Stepper,
  Text, TextInput, ThemeIcon, Title,
} from "@mantine/core";
import { IconAlertCircle, IconCheck, IconStack2, IconX } from "@tabler/icons-react";
import * as api from "../api";
import type { EnvHealth } from "../model";
import { useAuth } from "./AuthContext";

function CheckRow({ label, ok, detail, hint }: { label: string; ok: boolean; detail: string; hint: string }) {
  return (
    <Group align="flex-start" gap="sm" wrap="nowrap">
      <ThemeIcon color={ok ? "green" : "red"} variant="light" radius="xl" size={28}>
        {ok ? <IconCheck size={16} /> : <IconX size={16} />}
      </ThemeIcon>
      <div>
        <Text fw={500}>{label}</Text>
        <Text size="sm" c="dimmed">{detail}</Text>
        {!ok && <Text size="sm" c="orange">{hint}</Text>}
      </div>
    </Group>
  );
}

export function SetupWizard() {
  const nav = useNavigate();
  const { refresh } = useAuth();
  const [step, setStep] = useState(0);
  const [health, setHealth] = useState<EnvHealth | null>(null);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api.envHealth()
      .then(setHealth)
      .catch(() => setHealth({ dotnet: { ok: false, version: "unavailable" }, docker: { ok: false, detail: "unavailable" } }));
  }, []);

  const createAdmin = async () => {
    setError(null);
    if (!username.trim()) { setError("Username is required."); return; }
    if (password.length < 8) { setError("Password must be at least 8 characters."); return; }
    if (password !== confirm) { setError("Passwords do not match."); return; }
    setBusy(true);
    try {
      await api.setup(username, password);
      await refresh();
      nav("/");
    } catch (e) {
      setError(e instanceof Error ? e.message : "Setup failed.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Center py={60}>
      <Card withBorder shadow="sm" padding="xl" w={500}>
        <MStack gap={4} align="center" mb="lg">
          <ThemeIcon variant="light" size={40} radius="xl">
            <IconStack2 size={22} />
          </ThemeIcon>
          <Title order={3}>Welcome to AspireUI</Title>
          <Text c="dimmed" size="sm" ta="center">Let's check your environment and create the admin account.</Text>
        </MStack>

        <Stepper active={step} onStepClick={setStep} allowNextStepsSelect={false}>
          <Stepper.Step label="Environment check" description="Dependencies">
            <MStack gap="md" mt="md">
              {health ? (
                <>
                  <CheckRow
                    label=".NET SDK" ok={health.dotnet.ok} detail={health.dotnet.version}
                    hint="Building and running stacks needs the .NET SDK installed."
                  />
                  <CheckRow
                    label="Docker" ok={health.docker.ok} detail={health.docker.detail}
                    hint="Running stacks needs Docker; you can still build/export without it."
                  />
                </>
              ) : (
                <Text c="dimmed" size="sm">Checking environment…</Text>
              )}
              <Group justify="flex-end" mt="md">
                <Button onClick={() => setStep(1)} disabled={!health}>Next</Button>
              </Group>
            </MStack>
          </Stepper.Step>

          <Stepper.Step label="Create admin" description="Account">
            <MStack gap="md" mt="md">
              {error && <Alert color="red" icon={<IconAlertCircle size={16} />}>{error}</Alert>}
              <TextInput
                label="Username" value={username}
                onChange={e => setUsername(e.currentTarget.value)}
                data-autofocus
              />
              <PasswordInput
                label="Password" description="At least 8 characters"
                value={password} onChange={e => setPassword(e.currentTarget.value)}
              />
              <PasswordInput
                label="Confirm password" value={confirm}
                onChange={e => setConfirm(e.currentTarget.value)}
                onKeyDown={e => { if (e.key === "Enter") createAdmin(); }}
              />
              <Group justify="space-between" mt="md">
                <Button variant="default" onClick={() => setStep(0)}>Back</Button>
                <Button onClick={createAdmin} loading={busy}>Create admin &amp; finish</Button>
              </Group>
            </MStack>
          </Stepper.Step>
        </Stepper>
      </Card>
    </Center>
  );
}
