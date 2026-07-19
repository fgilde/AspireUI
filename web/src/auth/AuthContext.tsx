import { createContext, useContext } from "react";
import type { AuthStatus } from "../model";

// Shared between AuthGate (owns the fetch) and LoginPage/SetupWizard/UserMenu
// (need to trigger a re-fetch after login/setup/logout, or read the current user).
export interface AuthContextValue {
  status: AuthStatus | null;
  refresh: () => Promise<void>;
}

export const AuthContext = createContext<AuthContextValue>({ status: null, refresh: async () => {} });
export const useAuth = () => useContext(AuthContext);
