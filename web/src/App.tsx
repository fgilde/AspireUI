import { useEffect, useState, type ReactNode } from "react";
import { Routes, Route, Navigate } from "react-router-dom";
import { StacksOverview } from "./pages/StacksOverview";
import { Editor } from "./pages/Editor";
import { Settings } from "./pages/Settings";
import { Users } from "./pages/Users";
import { Profile } from "./pages/Profile";
import { Hosting } from "./pages/Hosting";
import { AppDetail } from "./pages/AppDetail";
import { AuthGate } from "./auth/AuthGate";
import { LoginPage } from "./auth/LoginPage";
import { SetupWizard } from "./auth/SetupWizard";
import { useAuth } from "./auth/AuthContext";
import { useViewMode } from "./viewMode";
import { canOpenEditor } from "./model";
import { CommandPalette } from "./CommandPalette";
import { ShortcutsHelp } from "./ShortcutsHelp";

function Home() {
  const { mode } = useViewMode();
  const [, force] = useState(0);
  useEffect(() => {
    const h = () => force(n => n + 1);
    window.addEventListener("aspireui:mode-changed", h);
    return () => window.removeEventListener("aspireui:mode-changed", h);
  }, []);
  return <StacksOverview simple={mode === "simple"} />;
}

function AuthedExtras() {
  const { status } = useAuth();
  if (!status?.authenticated) return null;
  return <><CommandPalette /><ShortcutsHelp /></>;
}

function AdminOnly({ children }: { children: ReactNode }) {
  const { status } = useAuth();
  return status?.user?.isAdmin ? children : <Navigate to="/" replace />;
}

function EditorGate({ children }: { children: ReactNode }) {
  const { status } = useAuth();
  return canOpenEditor(status?.user) ? children : <Navigate to="/hosting" replace />;
}

export default function App() {
  return (
    <AuthGate>
      <AuthedExtras />
      <Routes>
        <Route path="/setup" element={<SetupWizard />} />
        <Route path="/login" element={<LoginPage />} />
        <Route path="/" element={<Home />} />
        <Route path="/editor/:id" element={<EditorGate><Editor /></EditorGate>} />
        <Route path="/settings" element={<Settings />} />
        <Route path="/profile" element={<Profile />} />
        <Route path="/hosting" element={<Hosting />} />
        <Route path="/app/:id" element={<AppDetail />} />
        <Route path="/users" element={<AdminOnly><Users /></AdminOnly>} />
      </Routes>
    </AuthGate>
  );
}
