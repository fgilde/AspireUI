import { createContext, forwardRef, useCallback, useContext, useImperativeHandle, useRef } from "react";
import type { FunctionComponent } from "react";
import { useMantineColorScheme } from "@mantine/core";
import { DockviewReact } from "dockview-react";
import type { DockviewApi, DockviewReadyEvent, IDockviewPanelProps } from "dockview-react";
import "dockview-react/dist/styles/dockview.css";
import type { Stack, RunStatus } from "../model";
import { Palette } from "./Palette";
import { Canvas } from "./Canvas";
import { PropertyPanel } from "./PropertyPanel";
import { CodePreview } from "./CodePreview";
import { PackagesPanel } from "./PackagesPanel";
import { LogsPanel } from "./LogsPanel";
import { AssistPanel } from "./AssistPanel";
import { PublishPanel } from "./PublishPanel";
import { CodeEditorPanel } from "./CodeEditorPanel";

// Bumped to v5 so saved layouts (pre-Code editor tab) are discarded rather than
// restored without the new tab.
const LAYOUT_KEY = "aspireui.layout.v5";

export interface EditorState {
  stack: Stack;
  setStack: (s: Stack) => void;
  selected: string | null;
  setSelected: (id: string | null) => void;
  runStatus: RunStatus;
  setRunStatus: (s: RunStatus) => void;
}

export const EditorContext = createContext<EditorState | null>(null);

export function useEditor(): EditorState {
  const ctx = useContext(EditorContext);
  if (!ctx) throw new Error("Dock panel rendered outside DockLayout's EditorContext");
  return ctx;
}

// Panel components: dockview mounts these outside the normal React tree, so
// they read shared editor state from context instead of via props/closures.
function PalettePanel() {
  const { stack, setStack } = useEditor();
  return <Palette stack={stack} setStack={setStack} />;
}
function CanvasPanel() {
  const { stack, setStack, setSelected, runStatus } = useEditor();
  return <Canvas stack={stack} setStack={setStack} onSelect={setSelected} runState={runStatus.state} />;
}
function PropertiesPanel() {
  const { stack, setStack, selected, setSelected } = useEditor();
  return <PropertyPanel stack={stack} nodeId={selected} setStack={setStack} onDeleted={() => setSelected(null)} />;
}
function PreviewPanel() {
  const { stack } = useEditor();
  return <CodePreview stackId={stack.id} version={JSON.stringify(stack)} />;
}
function PackagesPanelTab() {
  const { stack } = useEditor();
  return <PackagesPanel stack={stack} />;
}
function LogsPanelTab() {
  const { runStatus } = useEditor();
  return <LogsPanel runStatus={runStatus} />;
}

const components: Record<string, FunctionComponent<IDockviewPanelProps>> = {
  palette: PalettePanel,
  canvas: CanvasPanel,
  properties: PropertiesPanel,
  preview: PreviewPanel,
  packages: PackagesPanelTab,
  logs: LogsPanelTab,
  assist: AssistPanel,
  publish: PublishPanel,
  code: CodeEditorPanel,
};

function buildDefaultLayout(api: DockviewApi) {
  api.clear();
  api.addPanel({ id: "canvas", component: "canvas", title: "Canvas" });
  api.addPanel({
    id: "palette", component: "palette", title: "Palette", initialWidth: 260,
    position: { direction: "left", referencePanel: "canvas" },
  });
  api.addPanel({
    id: "properties", component: "properties", title: "Properties", initialWidth: 380,
    position: { direction: "right", referencePanel: "canvas" },
  });
  api.addPanel({
    id: "preview", component: "preview", title: "Code Preview", initialHeight: 260,
    position: { direction: "below", referencePanel: "canvas" },
  });
  api.addPanel({
    id: "packages", component: "packages", title: "Packages",
    position: { direction: "within", referencePanel: "preview" },
  });
  api.addPanel({
    id: "logs", component: "logs", title: "Logs",
    position: { direction: "within", referencePanel: "preview" },
  });
  api.addPanel({
    id: "assist", component: "assist", title: "Assistant",
    position: { direction: "within", referencePanel: "preview" },
  });
  api.addPanel({
    id: "publish", component: "publish", title: "Publish / Deploy",
    position: { direction: "within", referencePanel: "preview" },
  });
  api.addPanel({
    id: "code", component: "code", title: "Code",
    position: { direction: "within", referencePanel: "preview" },
  });
}

export interface DockLayoutHandle {
  resetLayout: () => void;
}

// No props: the dock's stack/selection/run-status are supplied by the
// EditorContext.Provider that Editor.tsx wraps around this component.
export const DockLayout = forwardRef<DockLayoutHandle>(function DockLayout(_props, ref) {
  const apiRef = useRef<DockviewApi | null>(null);

  const resetLayout = useCallback(() => {
    localStorage.removeItem(LAYOUT_KEY);
    if (apiRef.current) buildDefaultLayout(apiRef.current);
  }, []);

  useImperativeHandle(ref, () => ({ resetLayout }), [resetLayout]);

  const onReady = useCallback((event: DockviewReadyEvent) => {
    apiRef.current = event.api;
    const saved = localStorage.getItem(LAYOUT_KEY);
    let restored = false;
    if (saved) {
      try {
        event.api.fromJSON(JSON.parse(saved));
        restored = true;
      } catch {
        localStorage.removeItem(LAYOUT_KEY);
      }
    }
    if (!restored) buildDefaultLayout(event.api);
    event.api.onDidLayoutChange(() => {
      localStorage.setItem(LAYOUT_KEY, JSON.stringify(event.api.toJSON()));
    });
  }, []);

  const { colorScheme } = useMantineColorScheme();
  const dockTheme = colorScheme === "light" ? "dockview-theme-light" : "dockview-theme-dark";
  return (
    <div className={dockTheme} style={{ height: "100%", width: "100%" }}>
      <DockviewReact components={components} onReady={onReady} />
    </div>
  );
});
