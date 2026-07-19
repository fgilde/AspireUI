import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Alert, Button, Card, Center, PasswordInput, Stack as MStack, Text, TextInput, ThemeIcon, Title } from "@mantine/core";
import { IconAlertCircle, IconStack2 } from "@tabler/icons-react";
import * as api from "../api";
import { useAuth } from "./AuthContext";

export function LoginPage() {
  const nav = useNavigate();
  const { refresh } = useAuth();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    if (!username || !password || busy) return;
    setError(null);
    setBusy(true);
    try {
      await api.login(username, password);
      await refresh();
      nav("/");
    } catch {
      setError("Invalid username or password.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Center h="100vh">
      <Card withBorder shadow="sm" padding="xl" w={360}>
        <MStack gap="md">
          <MStack gap={4} align="center">
            <ThemeIcon variant="light" size={40} radius="xl">
              <IconStack2 size={22} />
            </ThemeIcon>
            <Title order={3}>Sign in to AspireUI</Title>
            <Text c="dimmed" size="sm">Welcome back.</Text>
          </MStack>
          {error && <Alert color="red" icon={<IconAlertCircle size={16} />}>{error}</Alert>}
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
        </MStack>
      </Card>
    </Center>
  );
}
