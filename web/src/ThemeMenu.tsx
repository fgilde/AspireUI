import { Menu, ActionIcon, Tooltip } from "@mantine/core";
import { IconPalette, IconCheck } from "@tabler/icons-react";
import { useAppTheme } from "./ThemeProvider";

export function ThemeMenu() {
  const { themeId, setThemeId, themes } = useAppTheme();
  return (
    <Menu shadow="md" width={200} position="bottom-end" withArrow>
      <Menu.Target>
        <Tooltip label="Theme" withArrow>
          <ActionIcon variant="default" size="lg" aria-label="Choose theme"><IconPalette size={18} /></ActionIcon>
        </Tooltip>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>Theme</Menu.Label>
        {themes.map(t => (
          <Menu.Item key={t.id} onClick={() => setThemeId(t.id)}
            leftSection={t.id === themeId ? <IconCheck size={14} /> : <span style={{ width: 14 }} />}>
            {t.label}
          </Menu.Item>
        ))}
      </Menu.Dropdown>
    </Menu>
  );
}
