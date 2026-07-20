import type { ReactNode } from "react";
import { Routes, Route, Navigate } from "react-router-dom";
import { StacksOverview } from "./pages/StacksOverview";
import { Editor } from "./pages/Editor";
import { Settings } from "./pages/Settings";
import { Users } from "./pages/Users";
import { AuthGate } from "./auth/AuthGate";
import { LoginPage } from "./auth/LoginPage";
import { SetupWizard } from "./auth/SetupWizard";
import { useAuth } from "./auth/AuthContext";
import { CommandPalette } from "./CommandPalette";
import { ShortcutsHelp } from "./ShortcutsHelp";

// Global helpers active once authenticated (need the router + a session).
function AuthedExtras() {
  const { status } = useAuth();
  if (!status?.authenticated) return null;
  return <><CommandPalette /><ShortcutsHelp /></>;
}

// Belt-and-suspenders: the backend already 403s /users for non-admins, this just
// avoids rendering the page (and its API calls) for a user who can't use it.
function AdminOnly({ children }: { children: ReactNode }) {
  const { status } = useAuth();
  return status?.user?.isAdmin ? children : <Navigate to="/" replace />;
}

export default function App() {
  return (
    <AuthGate>
      <AuthedExtras />
      <Routes>
        <Route path="/setup" element={<SetupWizard />} />
        <Route path="/login" element={<LoginPage />} />
        <Route path="/" element={<StacksOverview />} />
        <Route path="/stacks/:id" element={<Editor />} />
        <Route path="/settings" element={<Settings />} />
        <Route path="/users" element={<AdminOnly><Users /></AdminOnly>} />
      </Routes>
    </AuthGate>
  );
}
