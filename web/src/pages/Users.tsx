import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  ActionIcon, Alert, AppShell, Badge, Button, Container, Group, PasswordInput,
  Stack as MStack, Switch, Table, TextInput, Title, Menu, Modal, Checkbox, Text,
} from "@mantine/core";
import { IconAlertCircle, IconArrowLeft, IconTrash, IconDots, IconKey, IconLock, IconLockOpen, IconPlus, IconShield, IconShieldOff, IconLayoutGrid } from "@tabler/icons-react";
import type { UserDto } from "../model";
import * as api from "../api";
import { useTitle } from "../useTitle";

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
  useTitle("Users");
  const [users, setUsers] = useState<UserDto[]>([]);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [isAdmin, setIsAdmin] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [addOpen, setAddOpen] = useState(false);

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
      setAddOpen(false);
      await refresh();
    } catch (e) {
      setError(errorMessage(e, "Failed to create user."));
    } finally {
      setBusy(false);
    }
  };

  const toggleAdmin = async (u: UserDto) => {
    setError(null);
    try { await api.adminSetAdmin(u.id, !u.isAdmin); await refresh(); }
    catch (e) { setError(errorMessage(e, "Failed to update role.")); }
  };

  // View-modes dialog state (which UI modes a user may use).
  const [vmTarget, setVmTarget] = useState<UserDto | null>(null);
  const [vmFull, setVmFull] = useState(true);
  const [vmSimple, setVmSimple] = useState(true);
  const openViewModes = (u: UserDto) => {
    const m = u.viewModes ?? ["full", "simple"];
    setVmFull(m.includes("full")); setVmSimple(m.includes("simple")); setVmTarget(u);
  };
  const submitViewModes = async () => {
    if (!vmTarget) return;
    const modes = [vmFull && "full", vmSimple && "simple"].filter(Boolean) as string[];
    try { await api.adminSetViewModes(vmTarget.id, modes); setVmTarget(null); await refresh(); }
    catch (e) { setError(errorMessage(e, "Failed to set view modes.")); }
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

  const toggleDisabled = async (u: UserDto) => {
    setError(null);
    try { await api.adminSetDisabled(u.id, !u.disabled); await refresh(); }
    catch (e) { setError(errorMessage(e, "Failed to update user.")); }
  };

  // Set-password dialog state.
  const [pwTarget, setPwTarget] = useState<UserDto | null>(null);
  const [pwValue, setPwValue] = useState("");
  const [pwForce, setPwForce] = useState(true);
  const submitPassword = async () => {
    if (!pwTarget) return;
    if (pwValue.length < 8) { setError("Password must be at least 8 characters."); return; }
    setBusy(true);
    try { await api.adminSetPassword(pwTarget.id, pwValue, pwForce); setPwTarget(null); setPwValue(""); await refresh(); }
    catch (e) { setError(errorMessage(e, "Failed to set password.")); }
    finally { setBusy(false); }
  };

  return (
    <AppShell header={{ height: 56 }} padding="lg">
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Group>
            <Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={() => nav("/")}>Stacks</Button>
            <Title order={4}>Users</Title>
          </Group>
          <Button leftSection={<IconPlus size={16} />} onClick={() => { setError(null); setAddOpen(true); }}>Add user</Button>
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
                <Table.Th>Status</Table.Th>
                <Table.Th>Created</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {users.map(u => {
                const lastAdmin = u.isAdmin && adminCount <= 1;
                return (
                  <Table.Tr key={u.id} style={u.disabled ? { opacity: 0.55 } : undefined}>
                    <Table.Td>{u.username}</Table.Td>
                    <Table.Td>{u.isAdmin && <Badge color="indigo" variant="light">Admin</Badge>}</Table.Td>
                    <Table.Td>
                      {u.disabled
                        ? <Badge color="red" variant="light">Disabled</Badge>
                        : <Badge color="green" variant="light">Active</Badge>}
                      {u.mustChangePassword && <Badge ml={4} color="yellow" variant="light">Must change pw</Badge>}
                    </Table.Td>
                    <Table.Td>{new Date(u.createdAt).toLocaleDateString()}</Table.Td>
                    <Table.Td>
                      <Menu position="bottom-end" withArrow>
                        <Menu.Target><ActionIcon variant="subtle" aria-label={`Actions for ${u.username}`}><IconDots size={16} /></ActionIcon></Menu.Target>
                        <Menu.Dropdown>
                          <Menu.Item leftSection={<IconKey size={14} />} onClick={() => { setPwTarget(u); setPwValue(""); setPwForce(true); }}>Set password…</Menu.Item>
                          <Menu.Item leftSection={u.isAdmin ? <IconShieldOff size={14} /> : <IconShield size={14} />}
                            disabled={u.isAdmin && lastAdmin}
                            onClick={() => toggleAdmin(u)}>{u.isAdmin ? "Remove admin" : "Make admin"}</Menu.Item>
                          <Menu.Item leftSection={<IconLayoutGrid size={14} />} onClick={() => openViewModes(u)}>View modes…</Menu.Item>
                          <Menu.Item leftSection={u.disabled ? <IconLockOpen size={14} /> : <IconLock size={14} />}
                            disabled={!u.disabled && lastAdmin}
                            onClick={() => toggleDisabled(u)}>{u.disabled ? "Enable" : "Disable"}</Menu.Item>
                          <Menu.Divider />
                          <Menu.Item color="red" leftSection={<IconTrash size={14} />} disabled={lastAdmin}
                            onClick={() => removeUser(u.id)}>Delete</Menu.Item>
                        </Menu.Dropdown>
                      </Menu>
                    </Table.Td>
                  </Table.Tr>
                );
              })}
            </Table.Tbody>
          </Table>

          <Modal opened={!!vmTarget} onClose={() => setVmTarget(null)} title={`View modes — ${vmTarget?.username}`} centered>
            <MStack gap="md">
              <Text size="sm" c="dimmed">Which UI modes may this user use? Both = they get an in-app toggle.</Text>
              <Checkbox label="Full (builder / canvas)" checked={vmFull} onChange={e => setVmFull(e.currentTarget.checked)} />
              <Checkbox label="Simple (app store)" checked={vmSimple} onChange={e => setVmSimple(e.currentTarget.checked)} />
              <Group justify="flex-end"><Button onClick={submitViewModes} disabled={!vmFull && !vmSimple}>Save</Button></Group>
            </MStack>
          </Modal>

          <Modal opened={!!pwTarget} onClose={() => setPwTarget(null)} title={`Set password — ${pwTarget?.username}`} centered>
            <MStack gap="md">
              <PasswordInput label="New password" description="At least 8 characters" value={pwValue} onChange={e => setPwValue(e.currentTarget.value)} />
              <Switch label="Require change on next login" checked={pwForce} onChange={e => setPwForce(e.currentTarget.checked)} />
              <Group justify="flex-end"><Button onClick={submitPassword} loading={busy}>Set password</Button></Group>
            </MStack>
          </Modal>

          <Modal opened={addOpen} onClose={() => setAddOpen(false)} title="Add user" centered>
            <MStack gap="md">
              <TextInput label="Username" value={username} onChange={e => setUsername(e.currentTarget.value)} data-autofocus />
              <PasswordInput label="Password" description="At least 8 characters"
                value={password} onChange={e => setPassword(e.currentTarget.value)} />
              <Switch label="Admin" checked={isAdmin} onChange={e => setIsAdmin(e.currentTarget.checked)} />
              <Group justify="flex-end">
                <Button variant="subtle" onClick={() => setAddOpen(false)}>Cancel</Button>
                <Button onClick={addUser} loading={busy}>Add user</Button>
              </Group>
            </MStack>
          </Modal>
        </Container>
      </AppShell.Main>
    </AppShell>
  );
}
