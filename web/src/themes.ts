import { createTheme } from "@mantine/core";
import type { MantineColorScheme, MantineThemeOverride, MantineColorsTuple } from "@mantine/core";

export interface AppTheme {
  id: string;
  label: string;
  scheme: Exclude<MantineColorScheme, "auto">;
  theme: MantineThemeOverride;
  dockview: string;   // dockview theme class
  monaco: string;     // monaco editor theme name (defined in CodeEditorPanel)
}

const blazor: MantineColorsTuple = [
  "#f3effd", "#e2d9f7", "#c1abee", "#9f7be6", "#8253de", "#7139da", "#6a2cd9", "#5921c0", "#4e1cac", "#421597",
];
// Near-black green terminal palette (overrides Mantine's dark[] so surfaces go black).
const term: MantineColorsTuple = [
  "#c8ffd8", "#9dffba", "#5cf58c", "#33e070", "#22b858", "#1a9247", "#137a3a", "#0d5c2c", "#08401f", "#041f0f",
];
const termDark: MantineColorsTuple = [
  "#7dd6a0", "#5cae80", "#2f7a52", "#1e5638", "#123a26", "#0d2e1e", "#0a2417", "#07190f", "#050f09", "#020703",
];

export const THEMES: AppTheme[] = [
  { id: "ocean", label: "Ocean (default)", scheme: "dark", dockview: "dockview-theme-dark", monaco: "vs-dark",
    theme: createTheme({ primaryColor: "indigo", defaultRadius: "md" }) },
  { id: "github", label: "GitHub", scheme: "dark", dockview: "dockview-theme-github", monaco: "vs-dark",
    theme: createTheme({ primaryColor: "blue", primaryShade: 6, defaultRadius: "sm",
      fontFamily: "-apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif" }) },
  { id: "aspire", label: "Aspire Dashboard", scheme: "dark", dockview: "dockview-theme-abyss", monaco: "vs-dark",
    theme: createTheme({ primaryColor: "violet", primaryShade: 5, defaultRadius: "md" }) },
  { id: "blazor", label: "Blazor", scheme: "light", dockview: "dockview-theme-light", monaco: "vs",
    theme: createTheme({ colors: { blazor }, primaryColor: "blazor", primaryShade: 6, defaultRadius: "sm" }) },
  { id: "terminal", label: "Terminal", scheme: "dark", dockview: "dockview-theme-monokai", monaco: "aspireui-terminal",
    theme: createTheme({ colors: { term, dark: termDark }, primaryColor: "term", primaryShade: 4, defaultRadius: 0,
      fontFamily: "'JetBrains Mono', 'Cascadia Code', Consolas, 'Courier New', monospace",
      fontFamilyMonospace: "'JetBrains Mono', 'Cascadia Code', Consolas, monospace" }) },
  { id: "nord", label: "Nord", scheme: "dark", dockview: "dockview-theme-nord", monaco: "vs-dark",
    theme: createTheme({ primaryColor: "cyan", primaryShade: 5, defaultRadius: "md" }) },
  { id: "dracula", label: "Dracula", scheme: "dark", dockview: "dockview-theme-dracula", monaco: "vs-dark",
    theme: createTheme({ primaryColor: "grape", primaryShade: 5, defaultRadius: "md" }) },
  { id: "solarized", label: "Solarized Light", scheme: "light", dockview: "dockview-theme-solarized", monaco: "vs",
    theme: createTheme({ primaryColor: "orange", primaryShade: 6, defaultRadius: "sm" }) },
];

export const DEFAULT_THEME = "ocean";
export const THEME_KEY = "aspireui.theme";

export function getTheme(id: string | null): AppTheme {
  return THEMES.find(t => t.id === id) ?? THEMES[0];
}
