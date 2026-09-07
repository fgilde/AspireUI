import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { Alert, Button, Card, Center, Divider, PasswordInput, Stack as MStack, Text, TextInput, Title } from "@mantine/core";
import { IconAlertCircle, IconKey } from "@tabler/icons-react";
import * as api from "../api";
import { useAuth } from "./AuthContext";
import logo from "../assets/logo.svg";

export function LoginPage() {
  const nav = useNavigate();
  const { refresh } = useAuth();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // Set once the password is accepted and the account wants a code as well.
  const [ticket, setTicket] = useState<string | null>(null);
  const [code, setCode] = useState("");
  const [sso, setSso] = useState<{ enabled: boolean; label: string } | null>(null);

  useEffect(() => { api.ssoStatus().then(setSso).catch(() => setSso(null)); }, []);
  // A failed sign-on comes back here with a reason in the url rather than a blank page.
  useEffect(() => {
    const reason = new URLSearchParams(location.search).get("ssoError");
    if (reason) setError(reason);
  }, []);

  const submit = async () => {
    if (!username || !password || busy) return;
    setError(null);
    setBusy(true);
    try {
      const r = await api.login(username, password);
      if (api.isTwoFactorChallenge(r)) { setTicket(r.ticket); setBusy(false); return; }
      await refresh();
      nav("/");
    } catch {
      setError("Invalid username or password.");
    } finally {
      setBusy(false);
    }
  };

  const submitCode = async () => {
    if (!ticket || !code || busy) return;
    setError(null);
    setBusy(true);
    try {
      await api.loginTwoFactor(ticket, code);
      await refresh();
      nav("/");
    } catch {
      setError("That code is not right. A recovery code works here too.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Center h="100vh">
      <Card withBorder shadow="sm" padding="xl" w={360}>
        <MStack gap="md">
          <MStack gap={8} align="center">
            <img src={logo} alt="AspireUI" width={190} style={{ maxWidth: "100%" }} />
            <Title order={4}>Sign in</Title>
            <Text c="dimmed" size="sm">Welcome back.</Text>
          </MStack>
          {error && <Alert color="red" icon={<IconAlertCircle size={16} />}>{error}</Alert>}
          {ticket ? (
            <>
              <Text size="sm" c="dimmed">
                Enter the six-digit code from your authenticator app. A recovery code works too.
              </Text>
              <TextInput
                label="Code"
                value={code}
                placeholder="123456"
                onChange={e => setCode(e.currentTarget.value)}
                onKeyDown={e => { if (e.key === "Enter") submitCode(); }}
                data-autofocus
              />
              <Button onClick={submitCode} loading={busy} fullWidth mt="xs">Sign in</Button>
              <Button variant="subtle" size="compact-sm" onClick={() => { setTicket(null); setCode(""); setError(null); }}>
                Start over
              </Button>
            </>
          ) : (
            <>
              <TextInput
                label="Username"
                value={username}
                onChange={e => setUsername(e.currentTarget.value)}
                onKeyDown={e => { if (e.key === "Enter") submit(); }}
                data-autofocus
              />
              <PasswordInput
                label="Password"
                value={password}
                onChange={e => setPassword(e.currentTarget.value)}
                onKeyDown={e => { if (e.key === "Enter") submit(); }}
              />
              <Button onClick={submit} loading={busy} fullWidth mt="xs">Sign in</Button>
              {sso?.enabled && (
                <>
                  <Divider label="or" labelPosition="center" my="xs" />
                  <Button variant="default" fullWidth leftSection={<IconKey size={16} />}
                    component="a" href={api.ssoStartUrl("/")}>
                    Sign in with {sso.label}
                  </Button>
                </>
              )}
            </>
          )}
        </MStack>
      </Card>
    </Center>
  );
}
