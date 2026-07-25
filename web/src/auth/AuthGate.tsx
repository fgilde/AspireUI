import { useCallback, useEffect, useState } from "react";
import type { ReactNode } from "react";
import { Navigate, useLocation, useNavigate } from "react-router-dom";
import { Center, Loader } from "@mantine/core";
import { routeForStatus, type AuthStatus } from "../model";
import * as api from "../api";
import { AuthContext } from "./AuthContext";

const AUTH_ROUTES = ["/login", "/setup"];

// Route guard that enforces path access based on auth status and handles session expiry.
export function AuthGate({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus | null>(null);
  const nav = useNavigate();
  const location = useLocation();

  const refresh = useCallback(() => api.authStatus().then(setStatus), []);
  useEffect(() => { refresh(); }, [refresh]);
  useEffect(() => {
    api.setOnUnauthorized(() => { refresh(); nav("/login", { replace: true }); });
  }, [nav, refresh]);

  if (!status) {
    return <Center h="100vh"><Loader color="indigo" /></Center>;
  }

  const target = routeForStatus(status);
  if (target && location.pathname !== target) return <Navigate to={target} replace />;
  if (!target && AUTH_ROUTES.includes(location.pathname)) return <Navigate to="/" replace />;
  if (!target && status.user?.mustChangePassword && location.pathname !== "/profile")
    return <Navigate to="/profile" replace />;

  return <AuthContext.Provider value={{ status, refresh }}>{children}</AuthContext.Provider>;
}
