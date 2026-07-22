import { useCallback, useEffect, useState } from "react";
import type { ReactNode } from "react";
import { Navigate, useLocation, useNavigate } from "react-router-dom";
import { Center, Loader } from "@mantine/core";
import { routeForStatus, type AuthStatus } from "../model";
import * as api from "../api";
import { AuthContext } from "./AuthContext";

const AUTH_ROUTES = ["/login", "/setup"];

// Wraps the whole route tree: fetches /auth/status once on mount, then on every
// navigation decides whether the current path is allowed (fresh install → only
// /setup; unauthenticated → only /login; authenticated → everything else).
// Also wires api.setOnUnauthorized so an expired session on any app call bounces
// here too, without every page needing its own 401 handling.
export function AuthGate({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus | null>(null);
  const nav = useNavigate();
  const location = useLocation();

  const refresh = useCallback(() => api.authStatus().then(setStatus), []);
  useEffect(() => { refresh(); }, [refresh]);
  useEffect(() => {
    // Re-fetch status (authStatus uses okAuth → can't re-trigger this hook) so the gate
    // re-renders into /login instead of getting stuck on the loading spinner forever.
    api.setOnUnauthorized(() => { refresh(); nav("/login", { replace: true }); });
  }, [nav, refresh]);

  if (!status) {
    return <Center h="100vh"><Loader color="indigo" /></Center>;
  }

  const target = routeForStatus(status);
  if (target && location.pathname !== target) return <Navigate to={target} replace />;
  if (!target && AUTH_ROUTES.includes(location.pathname)) return <Navigate to="/" replace />;
  // Admin forced a password change → keep the user on /profile until they change it.
  if (!target && status.user?.mustChangePassword && location.pathname !== "/profile")
    return <Navigate to="/profile" replace />;

  return <AuthContext.Provider value={{ status, refresh }}>{children}</AuthContext.Provider>;
}
