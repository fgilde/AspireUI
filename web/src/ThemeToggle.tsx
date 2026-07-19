import { ActionIcon, Tooltip, useMantineColorScheme } from "@mantine/core";
import { IconMoon, IconSun } from "@tabler/icons-react";

// Light/dark toggle. Mantine persists the choice; DockLayout/Canvas/CodePreview read
// the same colorScheme so panels, the canvas and code preview follow along.
export function ThemeToggle() {
  const { colorScheme, toggleColorScheme } = useMantineColorScheme();
  const dark = colorScheme === "dark";
  return (
    <Tooltip label={dark ? "Switch to light theme" : "Switch to dark theme"} withArrow>
      <ActionIcon variant="default" size="lg" onClick={() => toggleColorScheme()} aria-label="Toggle color scheme">
        {dark ? <IconSun size={18} /> : <IconMoon size={18} />}
      </ActionIcon>
    </Tooltip>
  );
}
