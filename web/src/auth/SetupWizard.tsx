import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  Alert, Button, Card, Center, Group, PasswordInput, Stack as MStack, Stepper, Switch,
  Text, TextInput, ThemeIcon, Title,
} from "@mantine/core";
import { IconAlertCircle, IconCheck, IconX } from "@tabler/icons-react";
import * as api from "../api";
import type { EnvHealth } from "../model";
import { useAuth } from "./AuthContext";
import { toastOk, toastErr } from "../ui";
import logo from "../assets/logo.svg";

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

  const [publicHost, setPublicHost] = useState("");
  const [reqHost, setReqHost] = useState("");
  const [npmEnabled, setNpmEnabled] = useState(false);
  const [npmUrl, setNpmUrl] = useState("");
  const [npmEmail, setNpmEmail] = useState("");
  const [npmPass, setNpmPass] = useState("");
  const [npmTest, setNpmTest] = useState<{ ok: boolean; error?: string | null } | null>(null);
  const [testing, setTesting] = useState(false);

  useEffect(() => {
    api.envHealth()
      .then(setHealth)
      .catch(() => setHealth({ dotnet: { ok: false, version: "unavailable" }, docker: { ok: false, detail: "unavailable" }, git: { ok: false, detail: "unavailable" } }));
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
      // Now authenticated — prefill the detected IP for the (optional) server-address step.
      try {
        const ds = await api.getDashboardSettings();
        setReqHost(ds.requestHost ?? "");
        const ips = await api.detectIps().catch(() => [] as string[]);
        setPublicHost(ds.publicHostSetting || ips[0] || "");
      } catch { /* ignore */ }
      setStep(2);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Setup failed.");
    } finally {
      setBusy(false);
    }
  };

  const savePublicHost = async () => {
    setBusy(true);
    try { await api.setDashboardSettings(false, "", publicHost.trim()); setStep(3); }
    catch (e) { toastErr(e); } finally { setBusy(false); }
  };

  const testNpm = async () => {
    setTesting(true); setNpmTest(null);
    try { setNpmTest(await api.testNpm({ enabled: true, baseUrl: npmUrl.trim(), email: npmEmail.trim(), password: npmPass, forwardHost: "" })); }
    catch (e) { setNpmTest({ ok: false, error: e instanceof Error ? e.message : String(e) }); }
    finally { setTesting(false); }
  };
  const finish = async (saveNpm: boolean) => {
    setBusy(true);
    try {
      if (saveNpm) await api.setNpmSettings({ enabled: npmEnabled, baseUrl: npmUrl.trim(), email: npmEmail.trim(), password: npmPass || undefined, forwardHost: "" });
      toastOk("Setup complete");
      nav("/");
    } catch (e) { toastErr(e); } finally { setBusy(false); }
  };

  return (
    <Center py={60}>
      <Card withBorder shadow="sm" padding="xl" w={540}>
        <MStack gap={8} align="center" mb="lg">
          <img src={logo} alt="AspireUI" width={220} style={{ maxWidth: "100%" }} />
          <Title order={4}>Welcome</Title>
          <Text c="dimmed" size="sm" ta="center">Check your environment, create the admin account, and optionally set up networking.</Text>
        </MStack>

        <Stepper active={step} onStepClick={setStep} allowNextStepsSelect={false}>
          <Stepper.Step label="Environment" description="Dependencies">
            <MStack gap="md" mt="md">
              {health ? (
                <>
                  <CheckRow label=".NET SDK" ok={health.dotnet.ok} detail={health.dotnet.version}
                    hint="Building and running stacks needs the .NET SDK installed." />
                  <CheckRow label="Docker" ok={health.docker.ok} detail={health.docker.detail}
                    hint="Running stacks needs Docker; you can still build/export without it." />
                  <CheckRow label="Git" ok={health.git.ok} detail={health.git.detail}
                    hint="Only GitHub-repository resources need git; everything else works without it." />
                </>
              ) : <Text c="dimmed" size="sm">Checking environment…</Text>}
              <Group justify="flex-end" mt="md">
                <Button onClick={() => setStep(1)} disabled={!health}>Next</Button>
              </Group>
            </MStack>
          </Stepper.Step>

          <Stepper.Step label="Admin" description="Account">
            <MStack gap="md" mt="md">
              {error && <Alert color="red" icon={<IconAlertCircle size={16} />}>{error}</Alert>}
              <TextInput label="Username" value={username} onChange={e => setUsername(e.currentTarget.value)} data-autofocus />
              <PasswordInput label="Password" description="At least 8 characters" value={password} onChange={e => setPassword(e.currentTarget.value)} />
              <PasswordInput label="Confirm password" value={confirm} onChange={e => setConfirm(e.currentTarget.value)}
                onKeyDown={e => { if (e.key === "Enter") createAdmin(); }} />
              <Group justify="space-between" mt="md">
                <Button variant="default" onClick={() => setStep(0)}>Back</Button>
                <Button onClick={createAdmin} loading={busy}>Create admin</Button>
              </Group>
            </MStack>
          </Stepper.Step>

          <Stepper.Step label="Server address" description="Optional">
            <MStack gap="md" mt="md">
              <Text size="sm" c="dimmed">
                This is the address other machines use to reach <b>this server</b> (its LAN IP or hostname). AspireUI uses it for
                every reachable link — hosted app URLs, the Aspire dashboard, dev-run links — and as the forward target for the
                Nginx Proxy Manager. Get it wrong and those links point nowhere.
              </Text>
              <Text size="sm" c="dimmed">
                <b>Running only on this machine?</b> Leave it as-is or empty — localhost is used automatically. In a container we can
                often only see the docker-bridge IP (172.x); if you fill that it won't work — put the host's real LAN IP.
              </Text>
              <Group align="flex-end">
                <TextInput style={{ flex: 1 }} label="Public host / IP" value={publicHost}
                  placeholder={reqHost ? `e.g. 192.168.1.50 — blank uses ${reqHost}` : "e.g. 192.168.1.50 (blank = localhost)"}
                  onChange={e => setPublicHost(e.currentTarget.value)} />
                <Button variant="default" onClick={async () => {
                  try { const ips = await api.detectIps(); if (ips.length) { setPublicHost(ips[0]); toastOk(`Detected ${ips[0]}`); } else toastErr("No IP detected — enter it manually"); }
                  catch (e) { toastErr(e); }
                }}>Detect</Button>
              </Group>
              <Group justify="space-between" mt="md">
                <Button variant="subtle" color="gray" onClick={() => nav("/")}>Skip &amp; finish</Button>
                <Button onClick={savePublicHost} loading={busy}>Save &amp; next</Button>
              </Group>
            </MStack>
          </Stepper.Step>

          <Stepper.Step label="Reverse proxy" description="Optional">
            <MStack gap="md" mt="md">
              <Text size="sm" c="dimmed">
                Connect <a href="https://nginxproxymanager.com" target="_blank" rel="noreferrer">Nginx Proxy Manager</a> and AspireUI can
                give any hosted app a <b>real domain</b> automatically — including the auto-expiring clone-hook instances. Purely optional;
                skip it and apps stay reachable by IP:port.
              </Text>
              <Switch label="Enable Nginx Proxy Manager" checked={npmEnabled} onChange={e => setNpmEnabled(e.currentTarget.checked)} />
              <TextInput label="NPM URL" placeholder="http://npm-host:81" value={npmUrl} disabled={!npmEnabled} onChange={e => setNpmUrl(e.currentTarget.value)} />
              <TextInput label="Email" placeholder="admin@example.com" value={npmEmail} disabled={!npmEnabled} onChange={e => setNpmEmail(e.currentTarget.value)} />
              <PasswordInput label="Password" value={npmPass} disabled={!npmEnabled} onChange={e => setNpmPass(e.currentTarget.value)} />
              {npmTest && <Alert color={npmTest.ok ? "green" : "red"} p="xs" icon={npmTest.ok ? <IconCheck size={16} /> : <IconAlertCircle size={16} />}>
                {npmTest.ok ? "Connected — NPM reachable and credentials valid." : `Failed: ${npmTest.error}`}
              </Alert>}
              <Group justify="space-between" mt="md">
                <Button variant="subtle" color="gray" onClick={() => finish(false)} loading={busy}>Skip &amp; finish</Button>
                <Group>
                  <Button variant="default" onClick={testNpm} loading={testing} disabled={!npmEnabled || !npmUrl.trim()}>Test</Button>
                  <Button onClick={() => finish(true)} loading={busy}>Save &amp; finish</Button>
                </Group>
              </Group>
            </MStack>
          </Stepper.Step>
        </Stepper>
      </Card>
    </Center>
  );
}
