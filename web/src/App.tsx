import { Routes, Route } from "react-router-dom";
import { StacksOverview } from "./pages/StacksOverview";
import { Editor } from "./pages/Editor";
import { Settings } from "./pages/Settings";

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<StacksOverview />} />
      <Route path="/stacks/:id" element={<Editor />} />
      <Route path="/settings" element={<Settings />} />
    </Routes>
  );
}
