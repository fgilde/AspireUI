import { useState } from "react";
import { Group, Button, PasswordInput, Stack as MStack, Text, Alert, Avatar, Badge, Divider, Paper } from "@mantine/core";
import { IconCheck, IconAlertCircle } from "@tabler/icons-react";
import { PageShell } from "../components/PageShell";
import { useAuth } from "../auth/AuthContext";
import * as api from "../api";
import { useTitle } from "../useTitle";

export function Profile() {
  useTitle("Profile");
  const { status, refresh } = useAuth();
  const user = status?.user;
  const mustChange = !!user?.mustChangePassword;
  const [oldPassword, setOld] = useState("");
  const [newPassword, setNew] = useState("");
  const [confirm, setConfirm] = useState("");
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null);

  const change = async () => {
    setMsg(null);
    if (newPassword.length < 8) { setMsg({ ok: false, text: "New password must be at least 8 characters." }); return; }
    if (newPassword !== confirm) { setMsg({ ok: false, text: "New password and confirmation don't match." }); return; }
    setBusy(true);
    try {
      await api.changePassword(oldPassword, newPassword);
      setMsg({ ok: true, text: "Password changed." });
      setOld(""); setNew(""); setConfirm("");
      await refresh();  // clears a forced-change lock so the app unblocks
    } catch (e) {
      const m = e instanceof Error ? e.message : String(e);
      setMsg({ ok: false, text: m.slice(m.indexOf(": ") + 2) });
    } finally { setBusy(false); }
  };

  return (
    <PageShell title="Profile" container="sm">
          <Paper withBorder p="md" radius="md" mb="lg">
            <Group>
              <Avatar radius="xl" size={56} color="indigo">{(user?.username ?? "?").slice(0, 2).toUpperCase()}</Avatar>
              <div>
                <Group gap={8}>
                  <Text fw={600} size="lg">{user?.username}</Text>
                  {user?.isAdmin && <Badge variant="light" color="grape">admin</Badge>}
                </Group>
                {user?.createdAt && <Text size="xs" c="dimmed">Member since {new Date(user.createdAt).toLocaleDateString()}</Text>}
              </div>
            </Group>
          </Paper>

          {mustChange && (
            <Alert color="yellow" variant="light" icon={<IconAlertCircle size={16} />} mb="md">
              An administrator requires you to change your password before continuing.
            </Alert>
          )}
          <Divider label="Change password" labelPosition="left" mb="md" />
          <MStack gap="md">
            <PasswordInput label="Current password" value={oldPassword} onChange={e => setOld(e.currentTarget.value)} />
            <PasswordInput label="New password" value={newPassword} onChange={e => setNew(e.currentTarget.value)} />
            <PasswordInput label="Confirm new password" value={confirm} onChange={e => setConfirm(e.currentTarget.value)} />
            {msg && (
              <Alert color={msg.ok ? "green" : "red"} variant="light"
                icon={msg.ok ? <IconCheck size={16} /> : <IconAlertCircle size={16} />}>{msg.text}</Alert>
            )}
            <Group justify="flex-end">
              <Button onClick={change} loading={busy} disabled={!oldPassword || !newPassword}>Change password</Button>
            </Group>
          </MStack>
    </PageShell>
  );
}
