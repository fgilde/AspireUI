import { Routes, Route } from "react-router-dom";
import { StacksOverview } from "./pages/StacksOverview";
import { Editor } from "./pages/Editor";
import { Settings } from "./pages/Settings";
import { AuthGate } from "./auth/AuthGate";
import { LoginPage } from "./auth/LoginPage";
import { SetupWizard } from "./auth/SetupWizard";

export default function App() {
  return (
    <AuthGate>
      <Routes>
        <Route path="/setup" element={<SetupWizard />} />
        <Route path="/login" element={<LoginPage />} />
        <Route path="/" element={<StacksOverview />} />
        <Route path="/stacks/:id" element={<Editor />} />
        <Route path="/settings" element={<Settings />} />
      </Routes>
    </AuthGate>
  );
}
