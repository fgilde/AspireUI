import { createContext, forwardRef, useCallback, useContext, useImperativeHandle, useRef } from "react";
import type { FunctionComponent } from "react";
import { DockviewReact } from "dockview-react";
import type { DockviewApi, DockviewReadyEvent, IDockviewPanelProps } from "dockview-react";
import "dockview-react/dist/styles/dockview.css";
import type { Stack } from "../model";
import { Palette } from "./Palette";
import { Canvas } from "./Canvas";
import { PropertyPanel } from "./PropertyPanel";
import { CodePreview } from "./CodePreview";

const LAYOUT_KEY = "aspireui.layout";

interface EditorState {
  stack: Stack;
  setStack: (s: Stack) => void;
  selected: string | null;
  setSelected: (id: string | null) => void;
}

const EditorContext = createContext<EditorState | null>(null);

function useEditor(): EditorState {
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
  const { stack, setStack, setSelected } = useEditor();
  return <Canvas stack={stack} setStack={setStack} onSelect={setSelected} />;
}
function PropertiesPanel() {
  const { stack, setStack, selected, setSelected } = useEditor();
  return <PropertyPanel stack={stack} nodeId={selected} setStack={setStack} onDeleted={() => setSelected(null)} />;
}
function PreviewPanel() {
  const { stack } = useEditor();
  return <CodePreview stackId={stack.id} version={JSON.stringify(stack)} />;
}

const components: Record<string, FunctionComponent<IDockviewPanelProps>> = {
  palette: PalettePanel,
  canvas: CanvasPanel,
  properties: PropertiesPanel,
  preview: PreviewPanel,
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
}

export interface DockLayoutHandle {
  resetLayout: () => void;
}

interface DockLayoutProps {
  stack: Stack;
  setStack: (s: Stack) => void;
  selected: string | null;
  setSelected: (id: string | null) => void;
}

export const DockLayout = forwardRef<DockLayoutHandle, DockLayoutProps>(function DockLayout(
  { stack, setStack, selected, setSelected },
  ref,
) {
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

  return (
    <EditorContext.Provider value={{ stack, setStack, selected, setSelected }}>
      <div className="dockview-theme-dark" style={{ height: "100%", width: "100%" }}>
        <DockviewReact components={components} onReady={onReady} />
      </div>
    </EditorContext.Provider>
  );
});
