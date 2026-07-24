import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Menu, UnstyledButton, Avatar, Group, Text, Badge } from "@mantine/core";
import { IconLogout, IconUsers, IconSettings, IconBrandGithub, IconHelp, IconUser, IconPalette, IconServer, IconCode } from "@tabler/icons-react";
import * as api from "../api";
import { useAuth } from "./AuthContext";
import { useAppTheme } from "../ThemeProvider";
import { HelpModal } from "../HelpButton";
import { ThemeDrawer } from "../ThemeDrawer";
import { REPO_URL } from "../GitHubLink";
import { useViewMode } from "../viewMode";
import { IconLayoutGrid, IconLayoutDashboard } from "@tabler/icons-react";

// One consolidated account menu for the header — folds in theme, GitHub, help/docs, admin Users,
// Settings and Logout so the toolbar stays tidy.
export function UserMenu() {
  const nav = useNavigate();
  const { status, refresh } = useAuth();
  const { current } = useAppTheme();
  const { mode, canToggle, setMode } = useViewMode();
  const [help, setHelp] = useState(false);
  const [themeOpen, setThemeOpen] = useState(false);
  const user = status?.user;
  if (!user) return null;
  const gotoMode = (m: "full" | "simple") => { setMode(m); nav("/"); };

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
          {canToggle && (mode === "simple"
            ? <Menu.Item leftSection={<IconLayoutDashboard size={14} />} onClick={() => gotoMode("full")}>Switch to Builder</Menu.Item>
            : <Menu.Item leftSection={<IconLayoutGrid size={14} />} onClick={() => gotoMode("simple")}>Switch to Simple (app store)</Menu.Item>)}
          <Menu.Item leftSection={<IconUser size={14} />} onClick={() => nav("/profile")}>Profile</Menu.Item>
          {user.isAdmin && <Menu.Item leftSection={<IconUsers size={14} />} onClick={() => nav("/users")}>Users</Menu.Item>}
          <Menu.Item leftSection={<IconServer size={14} />} onClick={() => nav("/hosting")}>Hosting</Menu.Item>
          <Menu.Item leftSection={<IconSettings size={14} />} onClick={() => nav("/settings")}>Settings</Menu.Item>

          <Menu.Divider />
          <Menu.Item leftSection={<IconPalette size={14} />} onClick={() => setThemeOpen(true)}
            rightSection={<Text size="xs" c="dimmed" truncate maw={90}>{current.label}</Text>}>Theme</Menu.Item>

          <Menu.Divider />
          <Menu.Item leftSection={<IconHelp size={14} />} onClick={() => setHelp(true)}>Help &amp; docs</Menu.Item>
          <Menu.Item leftSection={<IconCode size={14} />} component="a" href="/scalar" target="_blank" rel="noreferrer">API reference</Menu.Item>
          <Menu.Item leftSection={<IconBrandGithub size={14} />} component="a" href={REPO_URL} target="_blank" rel="noreferrer">AspireUI on GitHub</Menu.Item>
          <Menu.Divider />
          <Menu.Item leftSection={<IconLogout size={14} />} color="red" onClick={doLogout}>Logout</Menu.Item>
        </Menu.Dropdown>
      </Menu>
      <HelpModal opened={help} onClose={() => setHelp(false)} />
      <ThemeDrawer opened={themeOpen} onClose={() => setThemeOpen(false)} />
    </>
  );
}
