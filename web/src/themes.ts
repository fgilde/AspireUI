import { createTheme } from "@mantine/core";
import type { MantineColorScheme, MantineThemeOverride, MantineColorsTuple } from "@mantine/core";

export interface AppTheme {
  id: string;
  label: string;
  scheme: Exclude<MantineColorScheme, "auto">;
  swatch: string;     // accent dot shown in the theme menu
  theme: MantineThemeOverride;
  dockview: string;   // dockview theme class
  monaco: string;     // monaco editor theme (defined in CodeEditorPanel)
}

const GH_SANS = "-apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif";
const MONO = "'JetBrains Mono', 'Cascadia Code', 'SF Mono', Consolas, monospace";

// --- dark surface palettes (index: 0-3 text ramp, 4 border, 5 hover, 6 card, 7 body, 8-9 darker) ---
const githubDark: MantineColorsTuple = ["#e6edf3","#c9d1d9","#b1bac4","#768390","#30363d","#21262d","#161b22","#0d1117","#010409","#010409"];
const dracula:    MantineColorsTuple = ["#f8f8f2","#e2e2dc","#c9c9c3","#6272a4","#44475a","#3a3d4d","#282a36","#21222c","#191a21","#121319"];
const nord:       MantineColorsTuple = ["#eceff4","#e5e9f0","#d8dee9","#7b88a1","#4c566a","#434c5e","#3b4252","#2e3440","#272c36","#20242c"];
const oneDark:    MantineColorsTuple = ["#abb2bf","#9aa2b1","#828a99","#5c6370","#3e4451","#333842","#282c34","#21252b","#1b1e24","#16181d"];
const monokai:    MantineColorsTuple = ["#f8f8f2","#e6e6df","#ccccc4","#75715e","#49483e","#3e3d32","#272822","#22231c","#1c1d17","#141510"];
const aspireDark: MantineColorsTuple = ["#e9e9ec","#d3d3d9","#a9a9b6","#6c6c7a","#3f3f4a","#33333d","#26262e","#1c1c22","#151519","#0f0f12"];
const solarDark:  MantineColorsTuple = ["#fdf6e3","#eee8d5","#93a1a1","#839496","#586e75","#0a4552","#073642","#002b36","#00212b","#001820"];
const termDark:   MantineColorsTuple = ["#7dd6a0","#5cae80","#2f7a52","#1e5638","#123a26","#0d2e1e","#0a2417","#050f09","#030b06","#020703"];

// --- accent (primary) tuples ---
const ghBlue:   MantineColorsTuple = ["#cae8ff","#a5d6ff","#79c0ff","#58a6ff","#388bfd","#1f6feb","#1158c7","#0d419d","#0c2d6b","#051d4d"];
const ghBlueL:  MantineColorsTuple = ["#ddf4ff","#b6e3ff","#80ccff","#54aeff","#218bff","#0969da","#0550ae","#033d8b","#0a3069","#002155"];
const draPurple:MantineColorsTuple = ["#f1e6ff","#dcc6ff","#c9a9ff","#bd93f9","#a875f0","#9a63e8","#8b4fe0","#7a3fd0","#6a34b8","#5a2aa0"];
const nordFrost:MantineColorsTuple = ["#e3eef2","#c7dde6","#a3ccd9","#88c0d0","#6ba8bd","#5e81ac","#5273a0","#456293","#3a5280","#2f4269"];
const odBlue:   MantineColorsTuple = ["#d7ecff","#aed6ff","#82c0ff","#61afef","#4a97d8","#3d82c2","#3470ab","#2a5a8c","#20456d","#173250"];
const mkGreen:  MantineColorsTuple = ["#f0fcd0","#dff7a0","#c8ef62","#a6e22e","#8fc625","#78a91f","#659019","#4f7113","#3b540e","#293b09"];
const aspireVio:MantineColorsTuple = ["#f0e9ff","#dcc9ff","#c3a3ff","#a97dff","#9257f5","#8b5cf6","#7a45e0","#6733c4","#5526a3","#431c82"];
const blazor:   MantineColorsTuple = ["#f3effd","#e2d9f7","#c1abee","#9f7be6","#8253de","#7139da","#6a2cd9","#5921c0","#4e1cac","#421597"];
const term:     MantineColorsTuple = ["#c8ffd8","#9dffba","#5cf58c","#33e070","#22b858","#1a9247","#137a3a","#0d5c2c","#08401f","#041f0f"];
const solBlue:  MantineColorsTuple = ["#dceffb","#b3dbf5","#7cbfe9","#4aa3dc","#268bd2","#1f74b3","#1a5f94","#154b75","#103856","#0b2538"];
// light-scheme gray ramps
const ghGrayL:  MantineColorsTuple = ["#f6f8fa","#eaeef2","#d0d7de","#afb8c1","#8c959f","#6e7781","#57606a","#424a53","#32383f","#24292f"];
const solGrayL: MantineColorsTuple = ["#fdf6e3","#eee8d5","#d3cbb0","#93a1a1","#839496","#657b83","#586e75","#073642","#002b36","#00212b"];

export const THEMES: AppTheme[] = [
  { id: "ocean", label: "Ocean (default)", scheme: "dark", swatch: "#4c6ef5", dockview: "dockview-theme-dark", monaco: "vs-dark",
    theme: createTheme({ primaryColor: "indigo", defaultRadius: "md" }) },

  { id: "github-dark", label: "GitHub Dark", scheme: "dark", swatch: "#2f81f7", dockview: "dockview-theme-github", monaco: "vs-dark",
    theme: createTheme({ colors: { dark: githubDark, brand: ghBlue }, primaryColor: "brand", primaryShade: { light: 5, dark: 3 },
      defaultRadius: "md", fontFamily: GH_SANS, autoContrast: true }) },

  { id: "github-light", label: "GitHub Light", scheme: "light", swatch: "#0969da", dockview: "dockview-theme-light", monaco: "vs",
    theme: createTheme({ colors: { gray: ghGrayL, brand: ghBlueL }, primaryColor: "brand", primaryShade: 5,
      white: "#ffffff", black: "#1f2328", defaultRadius: "md", fontFamily: GH_SANS, autoContrast: true }) },

  { id: "aspire", label: "Aspire Dashboard", scheme: "dark", swatch: "#8b5cf6", dockview: "dockview-theme-abyss", monaco: "vs-dark",
    theme: createTheme({ colors: { dark: aspireDark, brand: aspireVio }, primaryColor: "brand", primaryShade: 5, defaultRadius: "md" }) },

  { id: "blazor", label: "Blazor", scheme: "light", swatch: "#512bd4", dockview: "dockview-theme-light", monaco: "vs",
    theme: createTheme({ colors: { blazor }, primaryColor: "blazor", primaryShade: 6, defaultRadius: "sm" }) },

  { id: "dracula", label: "Dracula", scheme: "dark", swatch: "#bd93f9", dockview: "dockview-theme-dracula", monaco: "vs-dark",
    theme: createTheme({ colors: { dark: dracula, brand: draPurple }, primaryColor: "brand", primaryShade: 3, defaultRadius: "md" }) },

  { id: "nord", label: "Nord", scheme: "dark", swatch: "#88c0d0", dockview: "dockview-theme-nord", monaco: "vs-dark",
    theme: createTheme({ colors: { dark: nord, brand: nordFrost }, primaryColor: "brand", primaryShade: 3, defaultRadius: "md" }) },

  { id: "one-dark", label: "One Dark", scheme: "dark", swatch: "#61afef", dockview: "dockview-theme-abyss", monaco: "vs-dark",
    theme: createTheme({ colors: { dark: oneDark, brand: odBlue }, primaryColor: "brand", primaryShade: 3, defaultRadius: "md" }) },

  { id: "monokai", label: "Monokai", scheme: "dark", swatch: "#a6e22e", dockview: "dockview-theme-monokai", monaco: "vs-dark",
    theme: createTheme({ colors: { dark: monokai, brand: mkGreen }, primaryColor: "brand", primaryShade: 3, defaultRadius: "sm", autoContrast: true }) },

  { id: "terminal", label: "Terminal", scheme: "dark", swatch: "#33e070", dockview: "dockview-theme-monokai", monaco: "aspireui-terminal",
    theme: createTheme({ colors: { term, dark: termDark }, primaryColor: "term", primaryShade: 4, defaultRadius: 0,
      fontFamily: MONO, fontFamilyMonospace: MONO, headings: { fontFamily: MONO }, autoContrast: true }) },

  { id: "solarized-dark", label: "Solarized Dark", scheme: "dark", swatch: "#268bd2", dockview: "dockview-theme-solarized", monaco: "vs-dark",
    theme: createTheme({ colors: { dark: solarDark, brand: solBlue }, primaryColor: "brand", primaryShade: 4, defaultRadius: "md" }) },

  { id: "solarized-light", label: "Solarized Light", scheme: "light", swatch: "#268bd2", dockview: "dockview-theme-solarized", monaco: "vs",
    theme: createTheme({ colors: { gray: solGrayL, brand: solBlue }, primaryColor: "brand", primaryShade: 4,
      white: "#fdf6e3", black: "#586e75", defaultRadius: "md" }) },
];

export const DEFAULT_THEME = "ocean";
export const THEME_KEY = "aspireui.theme";

export function getTheme(id: string | null): AppTheme {
  return THEMES.find(t => t.id === id) ?? THEMES[0];
}
