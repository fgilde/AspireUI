import { useEffect, useRef, useState } from "react";
import { Button, Group, Text, Alert, useMantineColorScheme } from "@mantine/core";
import { IconDeviceFloppy, IconAlertCircle } from "@tabler/icons-react";
// Lean import: the editor API + just the C# basic-language (tokenizer) contribution — avoids bundling
// monaco's TS/CSS/HTML/JSON language workers (~8 MB) we don't use. IntelliSense is our Roslyn backend.
import * as monaco from "monaco-editor/editor/editor.api";
import "monaco-editor/basic-languages/monaco.contribution";
import editorWorker from "monaco-editor/editor/editor.worker.js?worker";
import { useEditor } from "./DockLayout";
import * as api from "../api";

// C# has no built-in monaco worker (it's syntax-highlight only); IntelliSense comes from our Roslyn
// backend via the providers below. Only the generic editor worker is needed — bundled, no CDN.
(self as unknown as { MonacoEnvironment: monaco.Environment }).MonacoEnvironment = {
  getWorker: () => new editorWorker(),
};

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

export function CodeEditorPanel() {
  const { stack, setStack } = useEditor();
  const { colorScheme } = useMantineColorScheme();
  const hostRef = useRef<HTMLDivElement>(null);
  const edRef = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<string | null>(null);
  const id = stack.id;

  // Mount the editor once + register Roslyn-backed providers for this stack. Providers read the live
  // model text, so they stay correct as the user types.
  useEffect(() => {
    if (!hostRef.current) return;
    const ed = monaco.editor.create(hostRef.current, {
      value: "// loading…",
      language: "csharp",
      theme: colorScheme === "light" ? "vs" : "vs-dark",
      automaticLayout: true,
      minimap: { enabled: false },
      fontSize: 13,
      scrollBeyondLastLine: false,
    });
    edRef.current = ed;
    api.previewStack(id).then(code => ed.setValue(code));

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
    // Mount once per stack id; theme handled separately below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);

  useEffect(() => {
    monaco.editor.setTheme(colorScheme === "light" ? "vs" : "vs-dark");
  }, [colorScheme]);

  const save = async () => {
    const code = edRef.current?.getValue();
    if (code == null) return;
    setBusy(true); setErrors(null);
    try {
      setStack(await api.codeSave(id, stack.name, code));
    } catch (err) {
      setErrors(extractMessage(err));
    } finally {
      setBusy(false);
    }
  };

  // Ctrl+S saves; keep the editor content on failure.
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

  return (
    <div style={{ height: "100%", display: "flex", flexDirection: "column" }}>
      <Group justify="space-between" px="sm" py={4} wrap="nowrap">
        <Text size="xs" c="dimmed">Edit Program.cs — IntelliSense via Roslyn. Save re-parses into the graph (formatting/comments not kept).</Text>
        <Button size="compact-sm" leftSection={<IconDeviceFloppy size={14} />} loading={busy} onClick={() => void save()}>Save</Button>
      </Group>
      {errors && (
        <Alert color="red" icon={<IconAlertCircle size={16} />} m="xs" title="Could not save" withCloseButton onClose={() => setErrors(null)}>
          <Text size="xs" style={{ whiteSpace: "pre-wrap" }}>{errors}</Text>
        </Alert>
      )}
      <div ref={hostRef} style={{ flex: 1, minHeight: 0 }} />
    </div>
  );
}
