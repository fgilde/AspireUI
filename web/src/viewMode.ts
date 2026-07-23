import { useAuth } from "./auth/AuthContext";

const KEY = "aspireui.viewMode";
export type ViewMode = "full" | "simple";

// Effective UI mode for the current user: constrained to the modes an admin allows them.
// - allowed only one → that one (no toggle).
// - allowed both → the user's stored preference (default "full").
export function useViewMode(): { mode: ViewMode; allowed: ViewMode[]; canToggle: boolean; setMode: (m: ViewMode) => void } {
  const { status } = useAuth();
  const allowed = (status?.user?.viewModes?.filter(m => m === "full" || m === "simple") as ViewMode[] | undefined) ?? ["full", "simple"];
  const canToggle = allowed.length > 1;
  const stored = (localStorage.getItem(KEY) as ViewMode | null) ?? "full";
  const mode: ViewMode = canToggle ? stored : allowed[0] ?? "full";
  const setMode = (m: ViewMode) => { localStorage.setItem(KEY, m); window.dispatchEvent(new Event("aspireui:mode-changed")); };
  return { mode, allowed, canToggle, setMode };
}
