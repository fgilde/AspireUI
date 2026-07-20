import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { Spotlight } from "@mantine/spotlight";
import type { SpotlightActionData } from "@mantine/spotlight";
import { IconHome, IconSettings, IconUsers, IconStack2, IconPalette } from "@tabler/icons-react";
import { useAppTheme } from "./ThemeProvider";
import { useAuth } from "./auth/AuthContext";
import * as api from "./api";
import type { Stack } from "./model";

// Global command palette (Ctrl/Cmd+K): navigation, open a stack, switch theme.
export function CommandPalette() {
  const nav = useNavigate();
  const { themes, setThemeId } = useAppTheme();
  const { status } = useAuth();
  const [stacks, setStacks] = useState<Stack[]>([]);
  useEffect(() => { api.listStacks().then(setStacks).catch(() => {}); }, []);

  const actions: SpotlightActionData[] = useMemo(() => [
    { id: "home", label: "Stacks", description: "Go to the stacks overview", leftSection: <IconHome size={18} />, onClick: () => nav("/") },
    { id: "settings", label: "Settings", description: "AI provider & config", leftSection: <IconSettings size={18} />, onClick: () => nav("/settings") },
    ...(status?.user?.isAdmin ? [{ id: "users", label: "Users", description: "Manage users", leftSection: <IconUsers size={18} />, onClick: () => nav("/users") }] : []),
    ...stacks.map(s => ({
      id: "stack-" + s.id, label: s.name, description: `Open stack (${s.nodes.length} resources)`,
      leftSection: <IconStack2 size={18} />, onClick: () => nav(`/stacks/${s.id}`),
    })),
    ...themes.map(t => ({
      id: "theme-" + t.id, label: `Theme: ${t.label}`, description: "Switch theme",
      leftSection: <IconPalette size={18} />, onClick: () => setThemeId(t.id),
    })),
  ], [nav, stacks, themes, setThemeId, status]);

  return (
    <Spotlight actions={actions} shortcut="mod + K" nothingFound="Nothing found…"
      highlightQuery searchProps={{ placeholder: "Search stacks, settings, themes…" }} />
  );
}
