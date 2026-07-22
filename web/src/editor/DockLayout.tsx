import { createContext, forwardRef, useCallback, useContext, useImperativeHandle, useRef } from "react";
import type { FunctionComponent, CSSProperties } from "react";
import { DockviewReact } from "dockview-react";
import type { DockviewApi, DockviewReadyEvent, IDockviewPanelProps } from "dockview-react";
import "dockview-react/dist/styles/dockview.css";
import "./dockview-mantine.css";
import type { Stack, RunStatus } from "../model";
import type { CodeDiagnostic } from "../api";
import { ValidationPanel } from "./ValidationPanel";
import { Palette } from "./Palette";
import { Canvas } from "./Canvas";
import { PropertyPanel } from "./PropertyPanel";
import { CodePreview } from "./CodePreview";
import { PackagesPanel } from "./PackagesPanel";
import { LogsPanel } from "./LogsPanel";
import { AssistPanel } from "./AssistPanel";
import { PublishPanel } from "./PublishPanel";
import { CodeEditorPanel } from "./CodeEditorPanel";
import { DashboardPanel } from "./DashboardPanel";
import { useAppTheme } from "../ThemeProvider";

// Bumped to v5 so saved layouts (pre-Code editor tab) are discarded rather than
// restored without the new tab.
const LAYOUT_KEY = "aspireui.layout.v6";

export interface EditorState {
  stack: Stack;
  setStack: (s: Stack) => void;
  selected: string | null;
  setSelected: (id: string | null) => void;
  selectedIds: string[];                 // all canvas-selected node ids (multi-select)
  setSelectedIds: (ids: string[]) => void;
  runStatus: RunStatus;
  setRunStatus: (s: RunStatus) => void;
  diagnostics: CodeDiagnostic[];
  flashValidation: number;          // bumped when the badge is clicked, to flash the panel
  showValidation: () => void;       // focus the Validation panel + flash it
  showPanel: (id: string) => void;  // open (or focus) a dock panel by id, e.g. "logs"
  flashProps: number;               // bumped to flash the Properties panel (e.g. from "Edit properties")
  showProperties: () => void;       // open + flash the Properties panel
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
  const { stack, setStack, setSelected, setSelectedIds, showProperties, runStatus } = useEditor();
  return <Canvas stack={stack} setStack={setStack} onSelect={setSelected} onSelectIds={setSelectedIds}
    onShowProperties={showProperties} runState={runStatus.state} />;
}
function PropertiesPanel() {
  const { stack, setStack, selected, setSelected, selectedIds, flashProps } = useEditor();
  return <PropertyPanel stack={stack} nodeId={selected} selectedIds={selectedIds} flashProps={flashProps}
    setStack={setStack} onDeleted={() => setSelected(null)} />;
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
  dashboard: DashboardPanel,
  validation: ValidationPanel,
};

// All panels the editor knows about, with their titles + where a reopened one should dock.
// "side" panels flank the canvas; the rest join the bottom tab group.
export const PANELS: { id: string; title: string; where: "left" | "right" | "bottom" }[] = [
  { id: "palette", title: "Palette", where: "left" },
  { id: "canvas", title: "Canvas", where: "bottom" },
  { id: "properties", title: "Properties", where: "right" },
  { id: "preview", title: "Code Preview", where: "bottom" },
  { id: "packages", title: "Packages", where: "bottom" },
  { id: "logs", title: "Logs", where: "bottom" },
  { id: "assist", title: "Assistant", where: "bottom" },
  { id: "publish", title: "Publish / Deploy", where: "bottom" },
  { id: "code", title: "Code", where: "bottom" },
  { id: "dashboard", title: "Dashboard", where: "bottom" },
  { id: "validation", title: "Validation", where: "bottom" },
];

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
  api.addPanel({
    id: "dashboard", component: "dashboard", title: "Dashboard",
    position: { direction: "within", referencePanel: "preview" },
  });
  api.addPanel({
    id: "validation", component: "validation", title: "Validation",
    position: { direction: "within", referencePanel: "preview" },
  });
}

export interface DockLayoutHandle {
  resetLayout: () => void;
  saveNamed: (name: string) => void;
  loadNamed: (name: string) => void;
  deleteNamed: (name: string) => void;
  listNamed: () => string[];
  focusPanel: (id: string) => void;
  listPanels: () => { id: string; title: string; open: boolean }[];
  togglePanel: (id: string) => void;
  showPanel: (id: string) => void;
  popoutPanel: (id: string) => void;
}

// Named, reusable dock layouts (separate from the auto-persisted current layout).
const LAYOUTS_KEY = "aspireui.layouts.v1";
const readLayouts = (): Record<string, unknown> => {
  try { return JSON.parse(localStorage.getItem(LAYOUTS_KEY) || "{}"); } catch { return {}; }
};
const writeLayouts = (m: Record<string, unknown>) => localStorage.setItem(LAYOUTS_KEY, JSON.stringify(m));

// No props: the dock's stack/selection/run-status are supplied by the
// EditorContext.Provider that Editor.tsx wraps around this component.
export const DockLayout = forwardRef<DockLayoutHandle>(function DockLayout(_props, ref) {
  const apiRef = useRef<DockviewApi | null>(null);

  const resetLayout = useCallback(() => {
    localStorage.removeItem(LAYOUT_KEY);
    if (apiRef.current) buildDefaultLayout(apiRef.current);
  }, []);

  useImperativeHandle(ref, () => ({
    resetLayout,
    saveNamed: (name: string) => {
      if (!apiRef.current) return;
      const m = readLayouts(); m[name] = apiRef.current.toJSON(); writeLayouts(m);
    },
    loadNamed: (name: string) => {
      const m = readLayouts();
      if (apiRef.current && m[name]) { try { apiRef.current.fromJSON(m[name] as any); } catch { /* stale */ } }
    },
    deleteNamed: (name: string) => { const m = readLayouts(); delete m[name]; writeLayouts(m); },
    listNamed: () => Object.keys(readLayouts()),
    focusPanel: (id: string) => { const p = apiRef.current?.getPanel(id); p?.api.setActive(); },
    listPanels: () => PANELS.map(p => ({ id: p.id, title: p.title, open: !!apiRef.current?.getPanel(p.id) })),
    togglePanel: (id: string) => {
      const existing = apiRef.current?.getPanel(id);
      if (existing) { existing.api.close(); return; }
      openPanel(id);
    },
    showPanel: (id: string) => openPanel(id),
    // Detach a panel into its own OS window (dockview popout). Opens it first if closed.
    popoutPanel: (id: string) => {
      const api = apiRef.current;
      if (!api) return;
      if (!api.getPanel(id)) openPanel(id);
      const p = api.getPanel(id);
      if (p) { try { api.addPopoutGroup(p); } catch { /* popup blocked / unsupported */ } }
    },
  }), [resetLayout]);

  // Open (or focus, if already open) a panel. Reopened side panels flank the canvas; the rest join
  // the bottom tab group (falling back sensibly if those reference panels were themselves closed).
  function openPanel(id: string) {
    const api = apiRef.current;
    if (!api) return;
    const existing = api.getPanel(id);
    if (existing) { existing.api.setActive(); return; }
    const def = PANELS.find(p => p.id === id);
    if (!def) return;
    const pos = def.where === "left" ? { direction: "left" as const, referencePanel: "canvas" }
      : def.where === "right" ? { direction: "right" as const, referencePanel: "canvas" }
      : (api.getPanel("preview") ? { direction: "within" as const, referencePanel: "preview" } : undefined);
    const referenceOk = pos && "referencePanel" in pos && api.getPanel(pos.referencePanel!);
    api.addPanel({ id, component: id, title: def.title, position: referenceOk ? pos : undefined });
    api.getPanel(id)?.api.setActive();
  }

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

  const { current } = useAppTheme();
  // Bind dockview's surface colors to Mantine theme tokens (inline custom props override the theme
  // class), so panels/tabs follow the active scheme — fixes light themes rendering dark panels.
  const dvVars: Record<string, string> = {
    "--dv-group-view-background-color": "var(--mantine-color-body)",
    "--dv-tabs-and-actions-container-background-color": "var(--mantine-color-default)",
    "--dv-activegroup-visiblepanel-tab-background-color": "var(--mantine-color-body)",
    "--dv-activegroup-hiddenpanel-tab-background-color": "var(--mantine-color-default)",
    "--dv-inactivegroup-visiblepanel-tab-background-color": "var(--mantine-color-body)",
    "--dv-inactivegroup-hiddenpanel-tab-background-color": "var(--mantine-color-default)",
    "--dv-activegroup-visiblepanel-tab-color": "var(--mantine-color-text)",
    "--dv-activegroup-hiddenpanel-tab-color": "var(--mantine-color-dimmed)",
    "--dv-inactivegroup-visiblepanel-tab-color": "var(--mantine-color-text)",
    "--dv-inactivegroup-hiddenpanel-tab-color": "var(--mantine-color-dimmed)",
    "--dv-separator-border": "var(--mantine-color-default-border)",
    "--dv-tab-divider-color": "var(--mantine-color-default-border)",
    "--dv-icon-hover-background-color": "var(--mantine-color-default-hover)",
  };
  return (
    <div className={current.dockview} style={{ height: "100%", width: "100%", ...dvVars } as CSSProperties}>
      <DockviewReact components={components} onReady={onReady} />
    </div>
  );
});
