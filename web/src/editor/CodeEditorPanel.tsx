import { useEffect, useRef, useState } from "react";
import { Button, Group, Text, Alert, Modal } from "@mantine/core";
import type { IDockviewPanelProps } from "dockview-react";
import { IconDeviceFloppy, IconAlertCircle, IconRefresh } from "@tabler/icons-react";
import * as monaco from "monaco-editor/editor/editor.api";
import "monaco-editor/basic-languages/monaco.contribution";
import editorWorker from "monaco-editor/editor/editor.worker.js?worker";
import { useEditor } from "./DockLayout";
import { useAppTheme } from "../ThemeProvider";
import * as api from "../api";

(self as unknown as { MonacoEnvironment: monaco.Environment }).MonacoEnvironment = {
  getWorker: () => new editorWorker(),
};

const HILITE_STYLE = "aspireui-code-hilite";
if (typeof document !== "undefined" && !document.getElementById(HILITE_STYLE)) {
  const el = document.createElement("style");
  el.id = HILITE_STYLE;
  el.textContent = `
    .aspireui-node-line { background: rgba(255,165,0,0.10); }
    .aspireui-node-glyph { background: var(--mantine-color-orange-filled, orange); width: 4px !important; margin-left: 3px; }
    .aspireui-node-token { background: rgba(255,165,0,0.35); border-radius: 2px; }`;
  document.head.appendChild(el);
}

monaco.editor.defineTheme("aspireui-terminal", {
  base: "vs-dark", inherit: true,
  rules: [{ token: "", foreground: "33ff88" }, { token: "comment", foreground: "2a8f55" }],
  colors: { "editor.background": "#050f09", "editor.foreground": "#33ff88" },
});

function kindOf(tag: string): monaco.languages.CompletionItemKind {
  const K = monaco.languages.CompletionItemKind;
  switch (tag) {
    case "Method": case "ExtensionMethod": return K.Method;
    case "Property": return K.Property;
    case "Field": return K.Field;
    case "Class": return K.Class;
    case "Interface": return K.Interface;
    case "Enum": return K.Enum;
    case "EnumMember": return K.EnumMember;
    case "Namespace": return K.Module;
    case "Keyword": return K.Keyword;
    case "Local": case "Parameter": return K.Variable;
    default: return K.Text;
  }
}

function extractMessage(err: unknown): string {
  const msg = err instanceof Error ? err.message : String(err);
  const body = msg.slice(msg.indexOf(": ") + 2);
  try { const p = JSON.parse(body); if (Array.isArray(p)) return p.join("\n"); if (typeof p === "string") return p; } catch { /* raw */ }
  return body;
}

export function CodeEditorPanel(props: IDockviewPanelProps) {
  const { stack, setStack, selected } = useEditor();
  const { current } = useAppTheme();
  const monacoTheme = current.monaco;
  const hostRef = useRef<HTMLDivElement>(null);
  const edRef = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);
  const decoRef = useRef<monaco.editor.IEditorDecorationsCollection | null>(null);
  const dirtyRef = useRef(false);
  const applyingRef = useRef(false);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<string | null>(null);
  const [leavePrompt, setLeavePrompt] = useState(false);
  const id = stack.id;

  const applyRemote = (ed: monaco.editor.IStandaloneCodeEditor) =>
    api.previewStack(id).then(code => {
      if (code === ed.getValue()) return;
      applyingRef.current = true;
      ed.setValue(code);
      applyingRef.current = false;
      dirtyRef.current = false;
    });

  useEffect(() => {
    if (!hostRef.current) return;
    const ed = monaco.editor.create(hostRef.current, {
      value: "// loading…",
      language: "csharp",
      theme: monacoTheme,
      automaticLayout: true,
      minimap: { enabled: false },
      glyphMargin: true, // gutter markers for the selected node's lines
      fontSize: 13,
      scrollBeyondLastLine: false,
      fixedOverflowWidgets: true,
      quickSuggestions: true,
      acceptSuggestionOnCommitCharacter: false,
      editContext: false,
    });
    edRef.current = ed;
    ed.onDidChangeModelContent(() => { if (!applyingRef.current) dirtyRef.current = true; });
    applyRemote(ed);

    let diagTimer: number | undefined;
    const runDiagnostics = () => {
      const model = ed.getModel();
      if (!model) return;
      api.codeDiagnostics(id, model.getValue()).then(diags => {
        monaco.editor.setModelMarkers(model, "roslyn", diags.map(d => {
          const s = model.getPositionAt(d.start), e = model.getPositionAt(d.end);
          return {
            message: d.message,
            severity: d.severity === "error" ? monaco.MarkerSeverity.Error : monaco.MarkerSeverity.Warning,
            startLineNumber: s.lineNumber, startColumn: s.column, endLineNumber: e.lineNumber, endColumn: e.column,
          };
        }));
      }).catch(() => { /* degrade silently */ });
    };
    const changeSub = ed.onDidChangeModelContent(() => {
      window.clearTimeout(diagTimer);
      diagTimer = window.setTimeout(runDiagnostics, 400);
    });

    const completion = monaco.languages.registerCompletionItemProvider("csharp", {
      triggerCharacters: ["."],
      provideCompletionItems: async (model, position) => {
        const word = model.getWordUntilPosition(position);
        const range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
        try {
          const items = await api.codeComplete(id, model.getValue(), model.getOffsetAt(position));
          return { suggestions: items.map(i => ({
            label: i.label, kind: kindOf(i.kind), insertText: i.insertText, detail: i.detail ?? undefined, range,
          })) };
        } catch { return { suggestions: [] }; }
      },
    });
    const hover = monaco.languages.registerHoverProvider("csharp", {
      provideHover: async (model, position) => {
        try {
          const { contents } = await api.codeHover(id, model.getValue(), model.getOffsetAt(position));
          return contents ? { contents: [{ value: "```csharp\n" + contents + "\n```" }] } : null;
        } catch { return null; }
      },
    });
    const signature = monaco.languages.registerSignatureHelpProvider("csharp", {
      signatureHelpTriggerCharacters: ["(", ","],
      provideSignatureHelp: async (model, position) => {
        try {
          const s = await api.codeSignature(id, model.getValue(), model.getOffsetAt(position));
          if (!s) return null;
          return {
            value: { signatures: [{ label: s.label, parameters: s.parameters.map(p => ({ label: p })) }], activeSignature: 0, activeParameter: 0 },
            dispose: () => {},
          };
        } catch { return null; }
      },
    });

    return () => {
      window.clearTimeout(diagTimer);
      changeSub.dispose(); completion.dispose(); hover.dispose(); signature.dispose();
      ed.dispose();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);

  useEffect(() => {
    monaco.editor.setTheme(monacoTheme);
  }, [monacoTheme]);

  const stackKey = JSON.stringify(stack);
  useEffect(() => {
    const ed = edRef.current;
    if (ed && !dirtyRef.current) void applyRemote(ed);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [stackKey]);

  const selectedVar = selected ? stack.nodes.find(n => n.id === selected)?.varName : undefined;
  useEffect(() => {
    const ed = edRef.current, model = ed?.getModel();
    if (!ed || !model) return;
    decoRef.current?.clear();
    if (!selectedVar) return;
    const matches = model.findMatches(`\\b${selectedVar}\\b`, false, true, true, null, false);
    if (matches.length === 0) return;
    const lines = [...new Set(matches.map(m => m.range.startLineNumber))];
    decoRef.current = ed.createDecorationsCollection([
      ...lines.map(ln => ({ range: new monaco.Range(ln, 1, ln, 1),
        options: { isWholeLine: true, className: "aspireui-node-line", glyphMarginClassName: "aspireui-node-glyph" } })),
      ...matches.map(m => ({ range: m.range, options: { inlineClassName: "aspireui-node-token" } })),
    ]);
    ed.revealLineInCenter(lines[0]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedVar, stackKey]);

  const save = async () => {
    const code = edRef.current?.getValue();
    if (code == null) return;
    setBusy(true); setErrors(null);
    try {
      const updated = await api.codeSave(id, stack.name, code);
      dirtyRef.current = false;
      setStack(updated);
    } catch (err) {
      setErrors(extractMessage(err));
    } finally {
      setBusy(false);
    }
  };

  useEffect(() => {
    const ed = edRef.current;
    if (!ed) return;
    const d = ed.addAction({
      id: "aspireui-save", label: "Save stack", keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS],
      run: () => { void save(); },
    });
    return () => d.dispose();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, stack.name]);

  useEffect(() => {
    const d = props.api.onDidVisibilityChange(e => { if (!e.isVisible && dirtyRef.current) setLeavePrompt(true); });
    return () => d.dispose();
  }, [props.api]);
  const discard = () => { const ed = edRef.current; if (ed) { dirtyRef.current = false; void applyRemote(ed); } setLeavePrompt(false); };

  return (
    <div style={{ height: "100%", display: "flex", flexDirection: "column" }}>
      <Group justify="space-between" px="sm" py={4} wrap="nowrap">
        <Text size="xs" c="dimmed">Edit Program.cs — IntelliSense via Roslyn. Save re-parses into the graph (formatting/comments not kept).</Text>
        <Group gap={6} wrap="nowrap">
          <Button size="compact-sm" variant="default" leftSection={<IconRefresh size={14} />}
            onClick={() => { const ed = edRef.current; if (ed) { dirtyRef.current = false; void applyRemote(ed); } }}>
            Reload
          </Button>
          <Button size="compact-sm" leftSection={<IconDeviceFloppy size={14} />} loading={busy} onClick={() => void save()}>Save</Button>
        </Group>
      </Group>
      {errors && (
        <Alert color="red" icon={<IconAlertCircle size={16} />} m="xs" title="Could not save" withCloseButton onClose={() => setErrors(null)}>
          <Text size="xs" style={{ whiteSpace: "pre-wrap" }}>{errors}</Text>
        </Alert>
      )}
      <div ref={hostRef} style={{ flex: 1, minHeight: 0 }} />
      <Modal opened={leavePrompt} onClose={() => setLeavePrompt(false)} title="Unsaved code changes" centered>
        <Text size="sm" mb="md">You changed the code but didn't save. Save now, or discard your edits?</Text>
        <Group justify="flex-end" gap="xs">
          <Button variant="default" color="red" onClick={discard}>Discard</Button>
          <Button loading={busy} onClick={async () => { await save(); setLeavePrompt(false); }}>Save</Button>
        </Group>
      </Modal>
    </div>
  );
}
