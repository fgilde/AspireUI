import { useMemo, useState } from "react";
import { Modal, TextInput, NumberInput, Switch, Select, MultiSelect, Stack as MStack, Button, Group, Fieldset, Text, Divider, ActionIcon, Tooltip } from "@mantine/core";
import { IconFolder } from "@tabler/icons-react";
import { PrismLight as SyntaxHighlighter } from "react-syntax-highlighter";
import csharp from "react-syntax-highlighter/dist/esm/languages/prism/csharp";
import { oneDark, oneLight } from "react-syntax-highlighter/dist/esm/styles/prism";
import { useMantineColorScheme } from "@mantine/core";
import type { CatalogOverload, CatalogParam, Node, Edge, ResourceType } from "../model";
import { sanitizeIdentifier, toLiteral, configureLiteral, isPathParam, reuseCandidates } from "../model";
import { PathPickerModal } from "./PathPickerModal";

SyntaxHighlighter.registerLanguage("csharp", csharp);

function signature(o: CatalogOverload): string {
  return o.params.length === 0 ? "()" : o.params.map(p => p.required ? p.name : `${p.name}?`).join(", ");
}
function defaultName(rt: ResourceType, existingCount: number): string {
  const base = sanitizeIdentifier(rt.label || rt.addMethod.replace(/^Add/, ""));
  const lower = base.charAt(0).toLowerCase() + base.slice(1);
  return existingCount > 0 ? `${lower}${existingCount}` : lower;
}

export function AddResourceDialog({ rt, existingCount, totalCount, nodes, onCreate, onClose }: {
  rt: ResourceType; existingCount: number; totalCount: number;
  nodes: Node[];
  onCreate: (node: Node, refIds: string[], usedByIds: string[], extra?: { nodes: Node[]; edges: Edge[] }) => void;
  onClose: () => void;
}) {
  const { colorScheme } = useMantineColorScheme();
  const [name, setName] = useState(() => defaultName(rt, existingCount));
  const [overloadIdx, setOverloadIdx] = useState(0);
  const [values, setValues] = useState<Record<string, string>>({});
  const [refs, setRefs] = useState<string[]>([]);
  const [usedBy, setUsedBy] = useState<string[]>([]);
  // AI-backend offer, only for the AspireUI resource: wire its built-in assistant to a backend.
  const isAspireUI = rt.addMethod === "AddAspireUI";
  const llmCandidates = useMemo(() => reuseCandidates(nodes, "llm"), [nodes]);
  const [aiChoice, setAiChoice] = useState<string>("none");
  const [aiModel, setAiModel] = useState("llama3.2");
  const [pathPick, setPathPick] = useState<{ value: string; set: (v: string) => void } | null>(null);
  const overload = rt.addOverloads[overloadIdx] ?? rt.addOverloads[0];
  const setValue = (p: string, v: string) => setValues(vs => ({ ...vs, [p]: v }));

  const missingRequired = overload?.params.some(p => p.required && !(values[p.name] ?? p.default ?? "")) ?? false;
  const canCreate = (rt.composite || name.trim().length > 0) && !missingRequired;

  const field = (p: CatalogParam, keyPrefix = "") => {
    const key = keyPrefix + p.name;
    const value = values[key] ?? p.default ?? "";
    const set = (v: string) => setValue(key, v);
    if (p.type === "configure") return (
      <Fieldset key={key} legend={`${p.label} (optional)`} variant="filled">
        <MStack gap="xs">{(p.fields ?? []).map(f => field(f, `${p.name}.`))}</MStack>
      </Fieldset>
    );
    if (p.type === "int" || p.type === "number") return <NumberInput key={key} label={p.label} withAsterisk={p.required}
      allowDecimal={p.type === "number"} value={value === "" ? "" : Number(value)} onChange={v => set(String(v ?? ""))} />;
    if (p.type === "bool") return <Switch key={key} label={p.label}
      checked={value === "true"} onChange={e => set(e.currentTarget.checked ? "true" : "false")} />;
    if (p.type === "enum") return <Select key={key} label={p.label} withAsterisk={p.required}
      data={p.options ?? []} value={value || null} onChange={v => set(v ?? "")} />;
    // A resource-reference arg (composite/macro extensions): pick an existing node; the raw varName
    // is passed verbatim (not a string literal).
    if (p.type === "resourceRef") return <Select key={key} label={p.label} withAsterisk={p.required}
      placeholder="Pick a resource" searchable
      data={nodes.filter(n => !n.composite && n.varName).map(n => ({ value: n.varName, label: `${n.resourceName} (${n.varName})` }))}
      value={value || null} onChange={v => set(v ?? "")} />;
    if (isPathParam(p)) return <TextInput key={key} label={p.label} withAsterisk={p.required} value={value}
      onChange={e => set(e.currentTarget.value)}
      rightSection={
        <Tooltip label="Browse…" withArrow>
          <ActionIcon variant="subtle" onClick={() => setPathPick({ value, set })}><IconFolder size={15} /></ActionIcon>
        </Tooltip>
      } />;
    return <TextInput key={key} label={p.label} withAsterisk={p.required} value={value}
      onChange={e => set(e.currentTarget.value)} />;
  };

  const literalFor = (p: CatalogParam): string =>
    p.type === "configure"
      ? configureLiteral(p.fields ?? [], n => values[`${p.name}.${n}`] ?? "")
      : p.type === "resourceRef"
        ? (values[p.name] ?? p.default ?? "")
        : toLiteral(values[p.name] ?? p.default ?? "", p.type, p.enumTypeName);

  // The add-args (trailing optionals trimmed) — shared by the code preview and create().
  const addArgs = useMemo(() => {
    if (!overload) return [];
    const literals = overload.params.map(literalFor);
    let end = literals.length;
    while (end > 0) {
      const p = overload.params[end - 1];
      const empty = p.type === "configure" ? literals[end - 1] === "" : !(values[p.name] ?? p.default ?? "");
      if (!p.required && empty) end--; else break;
    }
    return literals.slice(0, end);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [overload, values]);

  const varName = sanitizeIdentifier(name || "resource");
  const preview = useMemo(() => {
    // Composite/macro: a bare statement, no `var`, no name arg.
    if (rt.composite) return `builder.${rt.addMethod}(${addArgs.join(", ")});`;
    const args = [`"${name}"`, ...addArgs].join(", ");
    const lines = [`var ${varName} = builder.${rt.addMethod}(${args});`];
    for (const id of refs) {
      const t = nodes.find(n => n.id === id);
      if (t) lines.push(`${varName}.WithReference(${t.varName});`);
    }
    for (const id of usedBy) {
      const t = nodes.find(n => n.id === id);
      if (t) lines.push(`${t.varName}.WithReference(${varName});`);
    }
    if (isAspireUI && aiChoice !== "none") {
      const bv = aiChoice.startsWith("reuse:") ? (nodes.find(n => n.id === aiChoice.slice(6))?.varName ?? "backend")
        : aiChoice === "add:AddOllama" ? "ollama" : "localai";
      if (aiChoice === "add:AddOllama") lines.push(`var ollama = builder.AddOllama("ollama").AddModel(${JSON.stringify(aiModel)});`);
      if (aiChoice === "add:AddLocalAI") lines.push(`var localai = builder.AddLocalAI("localai");`);
      lines.push(`${varName}.WithAi(${bv}, ${JSON.stringify(aiModel)});`);
    }
    return lines.join("\n");
  }, [name, varName, addArgs, refs, usedBy, nodes, rt.addMethod, rt.composite, isAspireUI, aiChoice, aiModel]);

  const create = () => {
    if (!overload || !canCreate) return;
    if (rt.composite) {
      // Statement-node: no var identifier, no references/wait-for edges; carries its own usings.
      onCreate({
        id: "n" + crypto.randomUUID().slice(0, 8),
        varName: "", resourceName: rt.label, addMethod: rt.addMethod,
        addArgs, withCalls: [],
        x: 60 + totalCount * 28, y: 60 + totalCount * 28,
        composite: true, usings: rt.usings ?? [],
      }, [], []);
      return;
    }
    const node: Node = {
      id: "n" + crypto.randomUUID().slice(0, 8),
      varName, resourceName: name, addMethod: rt.addMethod,
      addArgs, withCalls: [],
      x: 60 + totalCount * 28, y: 60 + totalCount * 28,
    };

    // AspireUI + AI backend: wire the assistant via WithAi(<backend>, "<model>"), reusing an existing
    // LLM resource or adding Ollama/LocalAI. The backend node (if new) + a waitFor edge come along.
    if (isAspireUI && aiChoice !== "none") {
      const eid = () => "e" + crypto.randomUUID().slice(0, 8);
      const extra: { nodes: Node[]; edges: Edge[] } = { nodes: [], edges: [] };
      let backendVar: string | null = null, backendId: string | null = null;
      if (aiChoice.startsWith("reuse:")) {
        const t = nodes.find(n => n.id === aiChoice.slice(6));
        if (t) { backendVar = t.varName; backendId = t.id; }
      } else if (aiChoice === "add:AddOllama") {
        backendVar = "ollama"; backendId = "n" + crypto.randomUUID().slice(0, 8);
        extra.nodes.push({ id: backendId, varName: "ollama", resourceName: "ollama", addMethod: "AddOllama",
          addArgs: [], withCalls: [{ method: "AddModel", args: [JSON.stringify(aiModel)] }], x: node.x + 260, y: node.y + 140 });
      } else if (aiChoice === "add:AddLocalAI") {
        backendVar = "localai"; backendId = "n" + crypto.randomUUID().slice(0, 8);
        extra.nodes.push({ id: backendId, varName: "localai", resourceName: "localai", addMethod: "AddLocalAI",
          addArgs: [], withCalls: [], x: node.x + 260, y: node.y + 140 });
      }
      if (backendVar && backendId) {
        node.withCalls = [{ method: "WithAi", args: [backendVar, JSON.stringify(aiModel)] }];
        extra.edges.push({ id: eid(), fromNodeId: node.id, toNodeId: backendId, kind: "waitFor" });
      }
      onCreate(node, refs, usedBy, extra);
      return;
    }
    onCreate(node, refs, usedBy);
  };

  return (
    <Modal opened onClose={onClose} title={`Add ${rt.label}`} size="lg">
      <MStack gap="sm">
        {rt.description && <Text size="sm" c="dimmed">{rt.description}</Text>}
        {rt.composite && <Text size="xs" c="dimmed">Setup/macro extension — adds several resources at once and returns the builder (no single resource of its own).</Text>}
        {!rt.composite && <TextInput label="Name" withAsterisk value={name} onChange={e => setName(e.currentTarget.value)} />}
        {rt.addOverloads.length > 1 && (
          <Select label="Overload" allowDeselect={false}
            data={rt.addOverloads.map((o, i) => ({ value: String(i), label: signature(o) }))}
            value={String(overloadIdx)} onChange={v => setOverloadIdx(Number(v ?? 0))} />
        )}
        {overload?.params.map(p => field(p))}

        {!rt.composite && nodes.length > 0 && (
          <>
            <MultiSelect label="References" description={`Resources this one should reference (${varName}.WithReference(x))`}
              data={nodes.map(n => ({ value: n.id, label: n.resourceName }))} value={refs} onChange={setRefs}
              searchable clearable />
            <MultiSelect label="Referenced by" description={`Resources that should reference this one (x.WithReference(${varName}))`}
              data={nodes.map(n => ({ value: n.id, label: n.resourceName }))} value={usedBy} onChange={setUsedBy}
              searchable clearable />
          </>
        )}

        {isAspireUI && (
          <>
            <Divider label="AI assistant" labelPosition="left" />
            <Text size="xs" c="dimmed">Give AspireUI's built-in assistant an LLM backend (optional).</Text>
            <Group gap="xs" align="end" wrap="nowrap">
              <Select style={{ flex: 1 }} label="Backend" allowDeselect={false} value={aiChoice} onChange={v => setAiChoice(v ?? "none")}
                data={[
                  { value: "none", label: "None (configure later)" },
                  ...llmCandidates.map(n => ({ value: `reuse:${n.id}`, label: `Reuse existing: ${n.resourceName}` })),
                  { value: "add:AddOllama", label: "Add Ollama + model" },
                  { value: "add:AddLocalAI", label: "Add LocalAI (Nextended)" },
                ]} />
              {aiChoice !== "none" && (
                <TextInput style={{ flex: 1 }} label="Model" value={aiModel} onChange={e => setAiModel(e.currentTarget.value)} />
              )}
            </Group>
          </>
        )}

        <Divider label="Code preview" labelPosition="left" />
        <SyntaxHighlighter language="csharp" style={colorScheme === "light" ? oneLight : oneDark}
          customStyle={{ margin: 0, background: "var(--mantine-color-default)", borderRadius: 6, fontSize: 12 }} wrapLongLines>
          {preview}
        </SyntaxHighlighter>

        <Group justify="end" mt="sm">
          <Button variant="subtle" onClick={onClose}>Cancel</Button>
          <Button disabled={!canCreate} onClick={create}>Create</Button>
        </Group>
      </MStack>
      {pathPick && (
        <PathPickerModal opened initial={pathPick.value} onClose={() => setPathPick(null)}
          onPick={p => { pathPick.set(p); setPathPick(null); }} />
      )}
    </Modal>
  );
}
