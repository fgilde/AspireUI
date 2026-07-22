import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Menu, UnstyledButton, Avatar, Group, Text, Badge } from "@mantine/core";
import { IconLogout, IconUsers, IconSettings, IconBrandGithub, IconHelp, IconCheck, IconUser } from "@tabler/icons-react";
import * as api from "../api";
import { useAuth } from "./AuthContext";
import { useAppTheme } from "../ThemeProvider";
import { HelpModal } from "../HelpButton";
import { REPO_URL } from "../GitHubLink";

// One consolidated account menu for the header — folds in theme, GitHub, help/docs, admin Users,
// Settings and Logout so the toolbar stays tidy.
export function UserMenu() {
  const nav = useNavigate();
  const { status, refresh } = useAuth();
  const { themeId, setThemeId, themes } = useAppTheme();
  const [help, setHelp] = useState(false);
  const user = status?.user;
  if (!user) return null;

  const doLogout = async () => { await api.logout(); await refresh(); nav("/login"); };
  const initials = user.username.slice(0, 2).toUpperCase();

  return (
    <>
      <Menu position="bottom-end" withArrow width={230} shadow="md">
        <Menu.Target>
          <UnstyledButton aria-label="Account menu">
            <Avatar radius="xl" size={34} color="indigo">{initials}</Avatar>
          </UnstyledButton>
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Label>
            <Group gap={6} wrap="nowrap">
              <Text size="sm" fw={600} truncate>{user.username}</Text>
              {user.isAdmin && <Badge size="xs" variant="light" color="grape">admin</Badge>}
            </Group>
          </Menu.Label>
          <Menu.Item leftSection={<IconUser size={14} />} onClick={() => nav("/profile")}>Profile</Menu.Item>
          {user.isAdmin && <Menu.Item leftSection={<IconUsers size={14} />} onClick={() => nav("/users")}>Users</Menu.Item>}
          <Menu.Item leftSection={<IconSettings size={14} />} onClick={() => nav("/settings")}>Settings</Menu.Item>

          <Menu.Divider />
          <Menu.Label>Theme</Menu.Label>
          {themes.map(t => (
            <Menu.Item key={t.id} onClick={() => setThemeId(t.id)}
              leftSection={
                <span style={{ display: "inline-flex", alignItems: "center", gap: 6, width: 28 }}>
                  {t.id === themeId ? <IconCheck size={13} /> : <span style={{ width: 13 }} />}
                  <span style={{ width: 11, height: 11, borderRadius: "50%", background: t.swatch, border: "1px solid rgba(128,128,128,.4)" }} />
                </span>
              }>{t.label}</Menu.Item>
          ))}

          <Menu.Divider />
          <Menu.Item leftSection={<IconHelp size={14} />} onClick={() => setHelp(true)}>Help &amp; docs</Menu.Item>
          <Menu.Item leftSection={<IconBrandGithub size={14} />} component="a" href={REPO_URL} target="_blank" rel="noreferrer">AspireUI on GitHub</Menu.Item>
          <Menu.Divider />
          <Menu.Item leftSection={<IconLogout size={14} />} color="red" onClick={doLogout}>Logout</Menu.Item>
        </Menu.Dropdown>
      </Menu>
      <HelpModal opened={help} onClose={() => setHelp(false)} />
    </>
  );
}
