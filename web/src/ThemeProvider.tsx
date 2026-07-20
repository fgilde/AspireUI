import { createContext, useContext, useState, useCallback } from "react";
import type { ReactNode } from "react";
import { MantineProvider } from "@mantine/core";
import { THEMES, DEFAULT_THEME, THEME_KEY, getTheme, type AppTheme } from "./themes";

interface ThemeCtx {
  themeId: string;
  setThemeId: (id: string) => void;
  current: AppTheme;
  themes: AppTheme[];
}
const Ctx = createContext<ThemeCtx | null>(null);

// The selected app theme lives here (not a static MantineProvider) so it can switch at runtime.
// Each theme forces its own light/dark scheme, so existing useMantineColorScheme() reads still work;
// dockview/monaco read `current.dockview`/`current.monaco` from this context.
export function AppThemeProvider({ children }: { children: ReactNode }) {
  const [themeId, setThemeIdState] = useState<string>(() => localStorage.getItem(THEME_KEY) ?? DEFAULT_THEME);
  const setThemeId = useCallback((id: string) => { localStorage.setItem(THEME_KEY, id); setThemeIdState(id); }, []);
  const current = getTheme(themeId);
  return (
    <Ctx.Provider value={{ themeId, setThemeId, current, themes: THEMES }}>
      <MantineProvider theme={current.theme} forceColorScheme={current.scheme}>
        {children}
      </MantineProvider>
    </Ctx.Provider>
  );
}

export function useAppTheme(): ThemeCtx {
  const c = useContext(Ctx);
  if (!c) throw new Error("useAppTheme outside AppThemeProvider");
  return c;
}
