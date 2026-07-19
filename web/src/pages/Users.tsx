import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  ActionIcon, Alert, AppShell, Badge, Button, Container, Group, PasswordInput,
  Stack as MStack, Switch, Table, TextInput, Title, Tooltip,
} from "@mantine/core";
import { IconAlertCircle, IconArrowLeft, IconTrash } from "@tabler/icons-react";
import type { UserDto } from "../model";
import * as api from "../api";

// Extracts the backend's `{ message }` JSON body out of ok()'s `"${status}: ${text}"`
// Error shape, so 400 (last admin) / 409 (duplicate username) show the real reason.
function errorMessage(e: unknown, fallback: string): string {
  if (!(e instanceof Error)) return fallback;
  const body = e.message.slice(e.message.indexOf(": ") + 2);
  try {
    const parsed = JSON.parse(body) as { message?: string };
    return parsed.message ?? fallback;
  } catch {
    return fallback;
  }
}

export function Users() {
  const nav = useNavigate();
  const [users, setUsers] = useState<UserDto[]>([]);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [isAdmin, setIsAdmin] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const refresh = () => api.listUsers().then(setUsers);
  useEffect(() => { refresh(); }, []);

  const adminCount = users.filter(u => u.isAdmin).length;

  const addUser = async () => {
    setError(null);
    if (!username.trim()) { setError("Username is required."); return; }
    if (password.length < 8) { setError("Password must be at least 8 characters."); return; }
    setBusy(true);
    try {
      await api.createUser(username, password, isAdmin);
      setUsername(""); setPassword(""); setIsAdmin(false);
      await refresh();
    } catch (e) {
      setError(errorMessage(e, "Failed to create user."));
    } finally {
      setBusy(false);
    }
  };

  const removeUser = async (id: string) => {
    setError(null);
    try {
      await api.deleteUser(id);
      await refresh();
    } catch (e) {
      setError(errorMessage(e, "Failed to delete user."));
    }
  };

  return (
    <AppShell header={{ height: 56 }} padding="lg">
      <AppShell.Header>
        <Group h="100%" px="md">
          <Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={() => nav("/")}>Stacks</Button>
          <Title order={4}>Users</Title>
        </Group>
      </AppShell.Header>

      <AppShell.Main>
        <Container size="sm">
          {error && (
            <Alert color="red" icon={<IconAlertCircle size={16} />} mb="md" withCloseButton onClose={() => setError(null)}>
              {error}
            </Alert>
          )}

          <Table verticalSpacing="sm" mb="xl">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Username</Table.Th>
                <Table.Th>Role</Table.Th>
                <Table.Th>Created</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {users.map(u => {
                const lastAdmin = u.isAdmin && adminCount <= 1;
                return (
                  <Table.Tr key={u.id}>
                    <Table.Td>{u.username}</Table.Td>
                    <Table.Td>{u.isAdmin && <Badge color="indigo" variant="light">Admin</Badge>}</Table.Td>
                    <Table.Td>{new Date(u.createdAt).toLocaleDateString()}</Table.Td>
                    <Table.Td>
                      <Tooltip label="Can't delete the last admin" withArrow disabled={!lastAdmin}>
                        <ActionIcon
                          variant="subtle" color="red"
                          aria-label={`Delete ${u.username}`}
                          onClick={() => removeUser(u.id)}
                        >
                          <IconTrash size={16} />
                        </ActionIcon>
                      </Tooltip>
                    </Table.Td>
                  </Table.Tr>
                );
              })}
            </Table.Tbody>
          </Table>

          <MStack gap="md" maw={360}>
            <Title order={5}>Add user</Title>
            <TextInput
              label="Username" value={username}
              onChange={e => setUsername(e.currentTarget.value)}
            />
            <PasswordInput
              label="Password" description="At least 8 characters"
              value={password} onChange={e => setPassword(e.currentTarget.value)}
            />
            <Switch label="Admin" checked={isAdmin} onChange={e => setIsAdmin(e.currentTarget.checked)} />
            <Group justify="flex-end">
              <Button onClick={addUser} loading={busy}>Add user</Button>
            </Group>
          </MStack>
        </Container>
      </AppShell.Main>
    </AppShell>
  );
}
