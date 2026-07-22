import { Drawer, SimpleGrid, UnstyledButton, Text, Group } from "@mantine/core";
import { IconCheck } from "@tabler/icons-react";
import { useAppTheme } from "./ThemeProvider";
import type { AppTheme } from "./themes";

// Preview colors for a theme's card — real values when the theme provides them, else scheme defaults.
function preview(t: AppTheme) {
  const dark = t.scheme === "dark";
  return t.preview ?? {
    bg: dark ? "#17171b" : "#ffffff",
    surface: dark ? "#25262b" : "#f1f3f5",
    accent: t.swatch,
    text: dark ? "#c1c2c5" : "#1f2328",
  };
}

// A little mock "app window" so the theme reads at a glance: title bar with accent dot, a body with
// an accent button and text lines.
function Preview({ t }: { t: AppTheme }) {
  const p = preview(t);
  return (
    <div style={{ background: p.bg, borderRadius: 8, overflow: "hidden", border: `1px solid ${p.surface}`, height: 78 }}>
      <div style={{ background: p.surface, height: 18, display: "flex", alignItems: "center", gap: 4, padding: "0 6px" }}>
        <span style={{ width: 7, height: 7, borderRadius: "50%", background: p.accent }} />
        <span style={{ width: 34, height: 4, borderRadius: 2, background: p.text, opacity: 0.5 }} />
      </div>
      <div style={{ padding: 8, display: "flex", flexDirection: "column", gap: 5 }}>
        <span style={{ width: "70%", height: 5, borderRadius: 3, background: p.text, opacity: 0.65 }} />
        <span style={{ width: "45%", height: 5, borderRadius: 3, background: p.text, opacity: 0.35 }} />
        <span style={{ width: 40, height: 12, borderRadius: 4, background: p.accent }} />
      </div>
    </div>
  );
}

export function ThemeDrawer({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { themeId, setThemeId, themes } = useAppTheme();
  return (
    <Drawer opened={opened} onClose={onClose} position="right" size={360} title="Theme" padding="md">
      <SimpleGrid cols={2} spacing="sm">
        {themes.map(t => {
          const active = t.id === themeId;
          return (
            <UnstyledButton key={t.id} onClick={() => setThemeId(t.id)}
              style={{ borderRadius: 10, padding: 6, border: `2px solid ${active ? "var(--mantine-primary-color-filled)" : "transparent"}`,
                background: active ? "var(--mantine-color-default-hover)" : undefined }}>
              <Preview t={t} />
              <Group gap={5} mt={6} wrap="nowrap">
                {active && <IconCheck size={13} />}
                <Text size="xs" fw={active ? 600 : 400} truncate>{t.label}</Text>
              </Group>
            </UnstyledButton>
          );
        })}
      </SimpleGrid>
    </Drawer>
  );
}
