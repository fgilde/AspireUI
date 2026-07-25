import { createContext, useContext } from "react";
import type { AuthStatus } from "../model";

// Auth context shared for login/logout state and triggering auth refreshes.
export interface AuthContextValue {
  status: AuthStatus | null;
  refresh: () => Promise<void>;
}

export const AuthContext = createContext<AuthContextValue>({ status: null, refresh: async () => {} });
export const useAuth = () => useContext(AuthContext);
