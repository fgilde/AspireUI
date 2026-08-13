import { useAuth } from "./auth/AuthContext";
import { canOpenEditor } from "./model";

const KEY = "aspireui.viewMode";
export type ViewMode = "full" | "simple";

// Flip to true to bring the "My apps ⇄ Builder" switch in the header back, with the
// per-user view modes an admin allows. While it is false, the mode follows the editor
// permission: builder for anyone who may open the editor, app store for everyone else.
export const VIEW_MODE_SWITCH = false;

// Effective UI mode for the current user: constrained to the modes an admin allows them.
// - allowed only one → that one (no toggle).
// - allowed both → the user's stored preference (default "full").
export function useViewMode(): { mode: ViewMode; allowed: ViewMode[]; canToggle: boolean; setMode: (m: ViewMode) => void } {
  const { status } = useAuth();
  if (!VIEW_MODE_SWITCH) {
    const mode: ViewMode = canOpenEditor(status?.user) ? "full" : "simple";
    return { mode, allowed: [mode], canToggle: false, setMode: () => {} };
  }
  const allowed = (status?.user?.viewModes?.filter(m => m === "full" || m === "simple") as ViewMode[] | undefined) ?? ["full", "simple"];
  const canToggle = allowed.length > 1;
  const stored = (localStorage.getItem(KEY) as ViewMode | null) ?? "full";
  const mode: ViewMode = canToggle ? stored : allowed[0] ?? "full";
  const setMode = (m: ViewMode) => { localStorage.setItem(KEY, m); window.dispatchEvent(new Event("aspireui:mode-changed")); };
  return { mode, allowed, canToggle, setMode };
}
