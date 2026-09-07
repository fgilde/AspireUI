import { useEffect, useState } from "react";
import {
  ActionIcon, Alert, Badge, Button, Group, PasswordInput,
  Stack as MStack, Switch, Table, TextInput, Menu, Modal, Checkbox, Text,
} from "@mantine/core";
import { IconAlertCircle, IconTrash, IconDots, IconKey, IconLock, IconLockOpen, IconPlus, IconShield, IconShieldOff, IconLayoutGrid, IconDeviceMobileOff } from "@tabler/icons-react";
import { PageShell } from "../components/PageShell";
import type { UserDto } from "../model";
import { can, PERMISSIONS, PERM_PRESETS } from "../model";
import { VIEW_MODE_SWITCH } from "../viewMode";
import { useAuth } from "../auth/AuthContext";
import * as api from "../api";
import { useTitle } from "../useTitle";

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

// "Everything" reads better than eleven of eleven, and an old user with no list at all is exactly that.
function permsLabel(u: UserDto): string {
  const n = PERMISSIONS.filter(p => can(u, p.id)).length;
  return n === PERMISSIONS.length ? "Everything" : n === 0 ? "Look only" : `${n} of ${PERMISSIONS.length}`;
}

function PermissionChecklist({ value, onChange, enabled }: {
  value: string[]; onChange: (v: string[]) => void; enabled: (id: string) => boolean;
}) {
  const toggle = (id: string, on: boolean) =>
    onChange(on ? [...value, id] : value.filter(x => x !== id));
  return (
    <MStack gap="xs">
      {PERMISSIONS.map(p => (
        <Checkbox key={p.id} label={p.label} description={p.description} disabled={!enabled(p.id)}
          checked={value.includes(p.id)} onChange={e => toggle(p.id, e.currentTarget.checked)} />
      ))}
    </MStack>
  );
}

export function Users() {
  useTitle("Users");
  const [users, setUsers] = useState<UserDto[]>([]);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [isAdmin, setIsAdmin] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [addOpen, setAddOpen] = useState(false);
  const [addPerms, setAddPerms] = useState<string[]>(PERMISSIONS.map(p => p.id));

  // Only an admin hands out the admin flag, and nobody hands out a permission they do not hold —
  // the server refuses either way, so the form does not offer it.
  const me = useAuth().status?.user;
  const iAmAdmin = !!me?.isAdmin;
  const grantable = (id: string) => iAmAdmin || can(me, id);

  const refresh = () => api.listUsers().then(setUsers);
  useEffect(() => { refresh(); }, []);

  const adminCount = users.filter(u => u.isAdmin).length;

  const addUser = async () => {
    setError(null);
    if (!username.trim()) { setError("Username is required."); return; }
    if (password.length < 8) { setError("Password must be at least 8 characters."); return; }
    setBusy(true);
    try {
      await api.createUser(username, password, isAdmin, isAdmin ? undefined : addPerms.filter(grantable));
      setUsername(""); setPassword(""); setIsAdmin(false); setAddPerms(PERMISSIONS.map(p => p.id));
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

  const [vmTarget, setVmTarget] = useState<UserDto | null>(null);
  const [vmFull, setVmFull] = useState(true);
  const [vmSimple, setVmSimple] = useState(true);
  const [vmPerms, setVmPerms] = useState<string[]>([]);
  const openPermissions = (u: UserDto) => {
    const m = u.viewModes ?? ["full", "simple"];
    setVmFull(m.includes("full")); setVmSimple(m.includes("simple"));
    setVmPerms(PERMISSIONS.map(p => p.id).filter(id => can(u, id)));
    setVmTarget(u);
  };
  const submitPermissions = async () => {
    if (!vmTarget) return;
    const modes = [vmFull && "full", vmSimple && "simple"].filter(Boolean) as string[];
    try {
      await api.adminSetViewModes(vmTarget.id, modes);
      await api.adminSetPermissions(vmTarget.id, vmPerms.filter(grantable));
      setVmTarget(null); await refresh();
    }
    catch (e) { setError(errorMessage(e, "Failed to set permissions.")); }
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

  // The way back in for somebody whose phone and recovery codes are both gone.
  const clearTwoFactor = async (u: UserDto) => {
    setError(null);
    try { await api.adminClearTwoFactor(u.id); await refresh(); }
    catch (e) { setError(errorMessage(e, "Failed to remove two-factor.")); }
  };

  const toggleDisabled = async (u: UserDto) => {
    setError(null);
    try { await api.adminSetDisabled(u.id, !u.disabled); await refresh(); }
    catch (e) { setError(errorMessage(e, "Failed to update user.")); }
  };

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
    <PageShell title="Users" container="sm"
      actions={<Button leftSection={<IconPlus size={16} />} onClick={() => { setError(null); setAddOpen(true); }}>Add user</Button>}>
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
                    <Table.Td>
                      {u.isAdmin
                        ? <Badge color="indigo" variant="light">Admin</Badge>
                        : <Badge color="gray" variant="light">{permsLabel(u)}</Badge>}
                    </Table.Td>
                    <Table.Td>
                      {u.disabled
                        ? <Badge color="red" variant="light">Disabled</Badge>
                        : <Badge color="green" variant="light">Active</Badge>}
                      {u.mustChangePassword && <Badge ml={4} color="yellow" variant="light">Must change pw</Badge>}
                      {u.twoFactor && <Badge ml={4} color="teal" variant="light">2FA</Badge>}
                    </Table.Td>
                    <Table.Td>{new Date(u.createdAt).toLocaleDateString()}</Table.Td>
                    <Table.Td>
                      <Menu position="bottom-end" withArrow>
                        <Menu.Target><ActionIcon variant="subtle" aria-label={`Actions for ${u.username}`}><IconDots size={16} /></ActionIcon></Menu.Target>
                        <Menu.Dropdown>
                          <Menu.Item leftSection={<IconKey size={14} />} onClick={() => { setPwTarget(u); setPwValue(""); setPwForce(true); }}>Set password…</Menu.Item>
                          {iAmAdmin && <Menu.Item leftSection={u.isAdmin ? <IconShieldOff size={14} /> : <IconShield size={14} />}
                            disabled={u.isAdmin && lastAdmin}
                            onClick={() => toggleAdmin(u)}>{u.isAdmin ? "Remove admin" : "Make admin"}</Menu.Item>}
                          <Menu.Item leftSection={<IconLayoutGrid size={14} />} onClick={() => openPermissions(u)}>Permissions…</Menu.Item>
                          {u.twoFactor && (
                            <Menu.Item leftSection={<IconDeviceMobileOff size={14} />} onClick={() => clearTwoFactor(u)}>
                              Remove two-factor
                            </Menu.Item>
                          )}
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

          <Modal opened={!!vmTarget} onClose={() => setVmTarget(null)} title={`Permissions — ${vmTarget?.username}`} centered size="lg">
            <MStack gap="md">
              {vmTarget?.isAdmin && (
                <Alert color="blue" variant="light">An admin may everything; these boxes take effect if the admin flag is removed.</Alert>
              )}
              {VIEW_MODE_SWITCH && <>
                <Text size="sm" fw={600}>View modes</Text>
                <Text size="xs" c="dimmed" mt={-8}>Which UI modes may this user use? Both = they get an in-app toggle.</Text>
                <Checkbox label="Full (builder / canvas)" checked={vmFull} onChange={e => setVmFull(e.currentTarget.checked)} />
                <Checkbox label="Simple (app store)" checked={vmSimple} onChange={e => setVmSimple(e.currentTarget.checked)} />
              </>}
              <Group gap="xs" align="center">
                <Text size="sm" fw={600}>Permissions</Text>
                {PERM_PRESETS.map(p => (
                  <Button key={p.label} size="compact-xs" variant="light"
                    onClick={() => setVmPerms(p.perms.filter(grantable))}>{p.label}</Button>
                ))}
              </Group>
              <PermissionChecklist value={vmPerms} onChange={setVmPerms} enabled={grantable} />
              <Group justify="flex-end"><Button onClick={submitPermissions} disabled={!vmFull && !vmSimple}>Save</Button></Group>
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
              {iAmAdmin && <Switch label="Admin" description="An admin may everything, permissions included."
                checked={isAdmin} onChange={e => setIsAdmin(e.currentTarget.checked)} />}
              {!isAdmin && <>
                <Group gap="xs" align="center">
                  <Text size="sm" fw={600}>Permissions</Text>
                  {PERM_PRESETS.map(p => (
                    <Button key={p.label} size="compact-xs" variant="light"
                      onClick={() => setAddPerms(p.perms.filter(grantable))}>{p.label}</Button>
                  ))}
                </Group>
                <PermissionChecklist value={addPerms} onChange={setAddPerms} enabled={grantable} />
              </>}
              <Group justify="flex-end">
                <Button variant="subtle" onClick={() => setAddOpen(false)}>Cancel</Button>
                <Button onClick={addUser} loading={busy}>Add user</Button>
              </Group>
            </MStack>
          </Modal>
    </PageShell>
  );
}
