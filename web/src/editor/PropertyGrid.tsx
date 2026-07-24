import { useEffect, useMemo, useRef, useState } from "react";
import type { ChangeEvent } from "react";
import { TextInput, NumberInput, Switch, Select, Stack as MStack, Button, Group, Divider, ActionIcon, Text, SegmentedControl, Tooltip, Menu, Modal } from "@mantine/core";
import { IconPlus, IconX, IconLink, IconInfoCircle, IconFolder, IconUpload } from "@tabler/icons-react";
import type { Stack, Node, ResourceType, CatalogParam } from "../model";
import { setAddArg, toLiteral, fromLiteral, readWithRows, writeWithRows, matchOverloadByArity, isPathParam, parseDotenv, sanitizeIdentifier, rid } from "../model";
import { toastOk, toastErr } from "../ui";
import { ResourceGlyph } from "../resourceIcons";
import { PathPickerModal } from "./PathPickerModal";
import { AddResourceDialog } from "./AddResourceDialog";
import * as api from "../api";

const ENV_METHOD = "WithEnvironment";

interface FieldOpts {
  nodes?: Node[];
  resourceTypeName?: (addMethod: string) => string | null | undefined;
  onBrowsePath?: (cur: string, set: (v: string) => void) => void;
  onAddResource?: (p: CatalogParam, set: (v: string) => void) => void;
}

// Small explain-icon — hover for an Aspire concept hint, so the grid teaches while you build.
function InfoDot({ text }: { text: string }) {
  return (
    <Tooltip label={text} withArrow multiline w={280} position="top">
      <IconInfoCircle size={13} style={{ opacity: 0.55, cursor: "help", verticalAlign: "middle" }} />
    </Tooltip>
  );
}
function labelWith(text: string, info: string) {
  return <Group gap={5} wrap="nowrap" component="span">{text}<InfoDot text={info} /></Group>;
}

function field(p: CatalogParam, value: string, onChange: (v: string) => void, opts: FieldOpts = {}) {
  const nodes = opts.nodes ?? [];
  if (p.type === "int" || p.type === "number") return <NumberInput key={p.name} label={p.label} withAsterisk={p.required}
    allowDecimal={p.type === "number"} value={value === "" ? "" : Number(value)} onChange={v => onChange(String(v ?? ""))} />;
  if (p.type === "bool") return <Switch key={p.name} label={p.label}
    checked={value === "true"} onChange={e => onChange(e.currentTarget.checked ? "true" : "false")} />;
  if (p.type === "enum") return <Select key={p.name} label={p.label} withAsterisk={p.required}
    data={p.options ?? []} value={value || null} onChange={v => onChange(v ?? "")} />;
  // A resource-reference param (e.g. WithPostgresDatasource(postgres)): pick another resource in
  // the stack; the bare varName is passed verbatim (not a string literal). "+ New" adds one inline.
  if (p.type === "resourceRef") {
    // Only offer resources whose produced CLR type matches the required param type (fall back to all
    // when nothing matches, e.g. the param wants a base interface we can't name-match).
    const cands = nodes.filter(n => !n.composite && n.varName);
    const typed = p.enumTypeName && opts.resourceTypeName
      ? cands.filter(n => opts.resourceTypeName!(n.addMethod) === p.enumTypeName) : cands;
    const pickable = typed.length > 0 ? typed : cands;
    return (
    <Group key={p.name} gap={6} align="end" wrap="nowrap">
      <Select style={{ flex: 1 }} label={p.label} withAsterisk={p.required} placeholder="Pick a resource" searchable
        data={pickable.map(n => ({ value: n.varName, label: `${n.resourceName} (${n.varName})` }))}
        value={value || null} onChange={v => onChange(v ?? "")} />
      {opts.onAddResource && (
        <Tooltip label="Add a new resource" withArrow>
          <ActionIcon variant="light" size="input-sm" onClick={() => opts.onAddResource!(p, onChange)}><IconPlus size={16} /></ActionIcon>
        </Tooltip>
      )}
    </Group>
  );
  }
  if (isPathParam(p) && opts.onBrowsePath) return (
    <TextInput key={p.name} label={p.label} withAsterisk={p.required} value={value}
      onChange={e => onChange(e.currentTarget.value)}
      rightSection={
        <Tooltip label="Browse…" withArrow>
          <ActionIcon variant="subtle" onClick={() => opts.onBrowsePath!(value, onChange)}><IconFolder size={15} /></ActionIcon>
        </Tooltip>
      } />
  );
  return <TextInput key={p.name} label={p.label} withAsterisk={p.required} value={value}
    onChange={e => onChange(e.currentTarget.value)} />;
}

function blankArgs(params: CatalogParam[]): string[] {
  return params.map(p => toLiteral(p.default ?? (p.type === "enum" ? p.options?.[0] ?? "" : ""), p.type, p.enumTypeName));
}

export function PropertyGrid({ stack, node, rt, setStack }:
  { stack: Stack; node: Node; rt: ResourceType | undefined; setStack: (s: Stack) => void }) {
  const [draft, setDraft] = useState<Node>(node);
  const [rawMethod, setRawMethod] = useState("");
  const [rawArgs, setRawArgs] = useState("");
  // Sync the draft to the authoritative node whenever its content changes (covers undo/redo and
  // assistant/code edits, not just switching nodes) so the grid never shows stale values.
  useEffect(() => { setDraft(node); }, [node]);
  useEffect(() => { setRawMethod(""); setRawArgs(""); }, [node.id]);

  const commit = (n: Node) => { setDraft(n); api.patchNode(stack.id, n).then(setStack); };

  const hasEnv = rt?.withs.some(w => w.method === ENV_METHOD) ?? false;
  const methodsInUse = useMemo(
    () => Array.from(new Set(draft.withCalls.map(w => w.method))).filter(m => m !== ENV_METHOD),
    [draft.withCalls]);
  const available = (rt?.withs ?? []).filter(w => w.method !== ENV_METHOD && !methodsInUse.includes(w.method));

  const addCapability = (method: string) => {
    const w = rt?.withs.find(x => x.method === method);
    if (!w) return;
    const simplest = [...w.overloads].sort((a, b) => a.params.length - b.params.length)[0]?.params ?? [];
    commit(writeWithRows(draft, method, [blankArgs(simplest)]));
  };

  const addRaw = () => {
    if (!rawMethod.trim()) return;
    const args = rawArgs.split(",").map(s => s.trim()).filter(s => s.length > 0);
    commit({ ...draft, withCalls: [...draft.withCalls, { method: rawMethod.trim(), args }] });
    setRawMethod(""); setRawArgs("");
  };

  const envRows = readWithRows(draft, ENV_METHOD);
  const otherNodes = stack.nodes.filter(n => n.id !== node.id);

  // Import a .env file onto this node. Plain: append WithEnvironment("K","V") rows. Secret: create an
  // AddParameter(name, secret:true) node per key + reference it from the env row (value = param var).
  const envFileRef = useRef<HTMLInputElement>(null);
  const importSecret = useRef(false);
  const importEnv = (secret: boolean) => { importSecret.current = secret; envFileRef.current?.click(); };
  const onEnvFile = async (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]; e.target.value = "";
    if (!file) return;
    const pairs = parseDotenv(await file.text());
    if (pairs.length === 0) { toastErr("No KEY=VALUE lines found.", "Nothing to import"); return; }
    if (!importSecret.current) {
      const rows = [...envRows, ...pairs.map(([k, v]) => [toLiteral(k, "string"), toLiteral(v, "string")])];
      commit(writeWithRows(draft, ENV_METHOD, rows));
      toastOk(`Imported ${pairs.length} variable(s)`);
      return;
    }
    // Secret: build parameter nodes + reference rows, saved in one shot at the stack level.
    const taken = new Set(stack.nodes.map(n => n.varName));
    const paramNodes: Node[] = [];
    const newRows = [...envRows];
    pairs.forEach(([k], i) => {
      let vn = sanitizeIdentifier(k.toLowerCase()); while (taken.has(vn)) vn = `${vn}_${i}`; taken.add(vn);
      paramNodes.push({
        id: "n" + rid(), varName: vn, resourceName: k, addMethod: "AddParameter",
        addArgs: ["true"], withCalls: [], x: node.x, y: node.y + 140 + i * 30, // secret:true
      });
      newRows.push([toLiteral(k, "string"), vn]); // env value references the parameter var (raw)
    });
    const updatedSelf = writeWithRows(draft, ENV_METHOD, newRows);
    const nodes = stack.nodes.map(n => n.id === node.id ? updatedSelf : n).concat(paramNodes);
    setDraft(updatedSelf);
    api.saveStack({ ...stack, nodes }).then(setStack);
    toastOk(`Imported ${pairs.length} secret parameter(s)`);
  };

  // Path picker + inline "add a resource" flow for resource-reference params.
  const [catalog, setCatalog] = useState<ResourceType[]>([]);
  useEffect(() => { api.getCatalog().then(setCatalog); }, []);
  const [pathPick, setPathPick] = useState<{ value: string; onPick: (v: string) => void } | null>(null);
  const [addTarget, setAddTarget] = useState<{ enumTypeName?: string | null; onPick: (v: string) => void } | null>(null);
  const [addRt, setAddRt] = useState<ResourceType | null>(null);
  const fieldOpts = {
    nodes: otherNodes,
    resourceTypeName: (addMethod: string) => catalog.find(r => r.addMethod === addMethod)?.resourceTypeName,
    onBrowsePath: (cur: string, set: (v: string) => void) => setPathPick({ value: cur, onPick: v => { set(v); setPathPick(null); } }),
    onAddResource: (p: CatalogParam, set: (v: string) => void) => setAddTarget({ enumTypeName: p.enumTypeName, onPick: v => { set(v); setAddTarget(null); setAddRt(null); } }),
  };
  // Resource types offered for an inline "+ New": prefer those producing the required CLR type;
  // fall back to all real (non-composite) resources if nothing matches.
  const addChoices = useMemo(() => {
    const real = catalog.filter(r => !r.composite);
    const matched = addTarget?.enumTypeName ? real.filter(r => r.resourceTypeName === addTarget.enumTypeName) : real;
    return matched.length > 0 ? matched : real;
  }, [catalog, addTarget]);

  // Simplified controls for the most common Aspire endpoint settings — only for resources that
  // actually expose them. The detailed capability grid below still allows the full form.
  const canExternal = rt?.withs.some(w => w.method === "WithExternalHttpEndpoints") ?? false;
  const canHttp = rt?.withs.some(w => w.method === "WithHttpEndpoint") ?? false;
  const isExternal = draft.withCalls.some(w => w.method === "WithExternalHttpEndpoints");
  const httpRows = readWithRows(draft, "WithHttpEndpoint");
  const portArg = httpRows[0]?.find(a => a.trim().startsWith("port:"));
  const port = portArg ? portArg.split(":")[1].trim() : "";
  const setPort = (v: string) =>
    commit(writeWithRows(draft, "WithHttpEndpoint", v.trim() ? [[`port: ${parseInt(v, 10) || 0}`]] : []));
  const setExternal = (on: boolean) =>
    commit(writeWithRows(draft, "WithExternalHttpEndpoints", on ? [[]] : []));

  return (
    <MStack gap="sm">
      <TextInput label="Name" value={draft.resourceName}
        onChange={e => commit({ ...draft, resourceName: e.currentTarget.value })} />
      {matchOverloadByArity(rt?.addOverloads ?? [], draft.addArgs.length)?.params.map((p, i) => field(p, fromLiteral(draft.addArgs[i] ?? '""'),
        v => commit(setAddArg(draft, i, toLiteral(v, p.type, p.enumTypeName))), fieldOpts))}

      {(canExternal || canHttp) && (
        <div>
          <Divider my="xs" labelPosition="left" label={labelWith("Quick settings",
            "The most common endpoint settings. 'Publicly accessible' emits WithExternalHttpEndpoints(); 'HTTP port' emits WithHttpEndpoint(port:). Full detail is in the capabilities below.")} />
          {canExternal && (
            <Switch mb="xs" label="Publicly accessible" checked={isExternal}
              description="Exposes an external HTTP endpoint (WithExternalHttpEndpoints). Off = internal only."
              onChange={e => setExternal(e.currentTarget.checked)} />
          )}
          {canHttp && (
            <NumberInput label="HTTP port" placeholder="auto" value={port === "" ? "" : Number(port)}
              description="Fixed host port for the HTTP endpoint. Leave empty for an auto-assigned port."
              min={0} max={65535} onChange={v => setPort(String(v ?? ""))} />
          )}
          {isExternal && <Text size="xs" c="dimmed" mt={4}>A public URL is assigned at deploy time; custom domains are configured per deploy target.</Text>}
        </div>
      )}

      {hasEnv && (
        <div>
          <Divider my="xs" labelPosition="left" label={labelWith("Environment variables",
            "Emitted as WithEnvironment(\"KEY\", value). 'Text' = a literal string; 'Expr' = raw C# (e.g. another resource's endpoint) — use the link icon to reference a resource.")} />
          {envRows.map((row, ri) => {
            const rawValue = row[1] ?? '""';
            const isExpression = !rawValue.trim().startsWith('"');
            const setVal = (literal: string) => {
              const nr = envRows.map(r => [...r]); nr[ri][1] = literal;
              commit(writeWithRows(draft, ENV_METHOD, nr));
            };
            return (
              <Group key={ri} gap="xs" mb={4} align="end" wrap="nowrap">
                <TextInput style={{ flex: 1 }} placeholder="NAME" value={fromLiteral(row[0] ?? '""')}
                  onChange={e => {
                    const nr = envRows.map(r => [...r]); nr[ri][0] = toLiteral(e.currentTarget.value, "string");
                    commit(writeWithRows(draft, ENV_METHOD, nr));
                  }} />
                {/* Text = quoted literal; Expr = raw C# (e.g. another resource's endpoint). Toggle converts. */}
                <SegmentedControl size="xs" value={isExpression ? "expr" : "text"}
                  onChange={m => setVal(m === "expr"
                    ? (isExpression ? rawValue : fromLiteral(rawValue))                 // "v" -> v
                    : toLiteral(isExpression ? rawValue : fromLiteral(rawValue), "string"))} // v -> "v"
                  data={[{ label: "Text", value: "text" }, { label: "Expr", value: "expr" }]} />
                <TextInput style={{ flex: 1.4 }} placeholder={isExpression ? "resource.GetEndpoint(\"http\")" : "value"}
                  value={isExpression ? rawValue : fromLiteral(rawValue)}
                  styles={isExpression ? { input: { fontFamily: "monospace", color: "var(--mantine-color-grape-text)" } } : undefined}
                  onChange={e => setVal(isExpression ? e.currentTarget.value : toLiteral(e.currentTarget.value, "string"))} />
                {isExpression && otherNodes.length > 0 && (
                  <Menu position="bottom-end" withArrow width={260}>
                    <Menu.Target>
                      <Tooltip label="Reference another resource" withArrow><ActionIcon variant="subtle"><IconLink size={14} /></ActionIcon></Tooltip>
                    </Menu.Target>
                    <Menu.Dropdown mah={300} style={{ overflowY: "auto" }}>
                      <Menu.Label>Insert a reference</Menu.Label>
                      {otherNodes.map(n => [
                        <Menu.Item key={n.id + "h"} onClick={() => setVal(`${n.varName}.GetEndpoint("http")`)}>{n.resourceName} — HTTP endpoint</Menu.Item>,
                        <Menu.Item key={n.id + "c"} onClick={() => setVal(`${n.varName}.Resource.ConnectionStringExpression`)}>{n.resourceName} — connection string</Menu.Item>,
                      ])}
                    </Menu.Dropdown>
                  </Menu>
                )}
                <ActionIcon variant="subtle" color="red" onClick={() => commit(writeWithRows(draft, ENV_METHOD, envRows.filter((_, x) => x !== ri)))}>
                  <IconX size={14} />
                </ActionIcon>
              </Group>
            );
          })}
          <Group gap="xs">
            <Button size="xs" variant="light" leftSection={<IconPlus size={12} />}
              onClick={() => commit(writeWithRows(draft, ENV_METHOD, [...envRows, ['""', '""']]))}>Add variable</Button>
            <Menu position="bottom-start" withArrow>
              <Menu.Target>
                <Button size="xs" variant="subtle" leftSection={<IconUpload size={12} />}>Import .env</Button>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item onClick={() => importEnv(false)}>As values (plaintext)</Menu.Item>
                <Menu.Item onClick={() => importEnv(true)}>As secret parameters</Menu.Item>
              </Menu.Dropdown>
            </Menu>
          </Group>
        </div>
      )}

      {methodsInUse.map(method => {
        const w = rt?.withs.find(x => x.method === method);
        const rows = readWithRows(draft, method);
        if (!w) {
          // Unknown to the catalog (e.g. loaded from an older stack) — edit as raw comma-args.
          return (
            <div key={method}>
              <Group justify="space-between">
                <Divider my="xs" label={method} labelPosition="left" style={{ flex: 1 }} />
                <ActionIcon size="sm" variant="subtle" color="red" onClick={() => commit(writeWithRows(draft, method, []))}><IconX size={14} /></ActionIcon>
              </Group>
              {rows.map((row, ri) => (
                <Group key={ri} gap="xs" mb={4} align="end">
                  <TextInput style={{ flex: 1 }} value={row.join(", ")}
                    onChange={e => {
                      const nr = rows.map(r => [...r]); nr[ri] = e.currentTarget.value.split(",").map(s => s.trim());
                      commit(writeWithRows(draft, method, nr));
                    }} />
                  <ActionIcon variant="subtle" color="red" onClick={() => commit(writeWithRows(draft, method, rows.filter((_, x) => x !== ri)))}><IconX size={14} /></ActionIcon>
                </Group>
              ))}
              <Button size="xs" variant="light" leftSection={<IconPlus size={12} />}
                onClick={() => commit(writeWithRows(draft, method, [...rows, []]))}>Add row</Button>
            </div>
          );
        }
        const simplest = [...w.overloads].sort((a, b) => a.params.length - b.params.length)[0]?.params ?? [];
        return (
          <div key={method}>
            <Group justify="space-between">
              <Divider my="xs" label={w.label} labelPosition="left" style={{ flex: 1 }} />
              <ActionIcon size="sm" variant="subtle" color="red" onClick={() => commit(writeWithRows(draft, method, []))}><IconX size={14} /></ActionIcon>
            </Group>
            {rows.map((row, ri) => {
              const rowParams = matchOverloadByArity(w.overloads, row.length)?.params ?? simplest;
              return (
                <Group key={ri} align="end" gap="xs" mb={4}>
                  {rowParams.length > 0
                    ? rowParams.map((p, pi) => field(p, fromLiteral(row[pi] ?? '""'), v => {
                        const nr = rows.map(r => [...r]); while (nr[ri].length <= pi) nr[ri].push('""');
                        nr[ri][pi] = toLiteral(v, p.type, p.enumTypeName); commit(writeWithRows(draft, method, nr));
                      }, fieldOpts))
                    // Variadic / no-typed-param methods (e.g. WithContainerRuntimeArgs(params string[]))
                    // — edit the raw args as comma-separated C# literals.
                    : <TextInput style={{ flex: 1 }} label="Args (raw)" placeholder='"--gpus", "all"'
                        value={row.join(", ")}
                        onChange={e => { const nr = rows.map(r => [...r]); nr[ri] = e.currentTarget.value.split(",").map(s => s.trim()).filter(Boolean); commit(writeWithRows(draft, method, nr)); }} />}
                  <ActionIcon variant="subtle" color="red" onClick={() => commit(writeWithRows(draft, method, rows.filter((_, x) => x !== ri)))}><IconX size={14} /></ActionIcon>
                </Group>
              );
            })}
            <Button size="xs" variant="light" leftSection={<IconPlus size={12} />}
              onClick={() => commit(writeWithRows(draft, method, [...rows, blankArgs(simplest)]))}>Add row</Button>
          </div>
        );
      })}

      {available.length > 0 && (
        <Tooltip label="Add a With*/Add* capability to this resource" position="top-start" withArrow openDelay={400}>
          <Select
            label="Add capability"
            placeholder="Search…"
            searchable
            clearable
            value={null}
            data={available.map(w => ({ value: w.method, label: w.label }))}
            onChange={v => v && addCapability(v)}
          />
        </Tooltip>
      )}

      <Divider my="xs" labelPosition="left" label={labelWith("Raw call (advanced)",
        "Append any fluent call verbatim, e.g. WithReplicas / WithLifetime. Args are raw C# literals, emitted as {resource}.{Method}(args) in Program.cs.")} />
      <Text size="xs" c="dimmed">For anything not covered above. Args are raw C# literals.</Text>
      <Group gap="xs" align="end">
        <TextInput style={{ flex: 1 }} label="Method" placeholder="WithSomething" value={rawMethod} onChange={e => setRawMethod(e.currentTarget.value)} />
        <TextInput style={{ flex: 2 }} label="Args" placeholder='"a", 1, true' value={rawArgs} onChange={e => setRawArgs(e.currentTarget.value)} />
        <Button size="sm" variant="light" leftSection={<IconPlus size={12} />} onClick={addRaw}>Add</Button>
      </Group>

      <input ref={envFileRef} type="file" accept=".env,text/plain" hidden onChange={onEnvFile} />

      {pathPick && (
        <PathPickerModal opened initial={pathPick.value} onClose={() => setPathPick(null)} onPick={pathPick.onPick} />
      )}

      {/* Inline "+ New resource" for a resource-reference param: pick a (type-matching) resource type… */}
      <Modal opened={!!addTarget && !addRt} onClose={() => setAddTarget(null)} title="Add a resource" size="md">
        <MStack gap={4}>
          {addTarget?.enumTypeName && <Text size="xs" c="dimmed">Resources matching {addTarget.enumTypeName} first.</Text>}
          {addChoices.map(rtc => (
            <Button key={rtc.addMethod} variant="subtle" justify="start" leftSection={<ResourceGlyph addMethod={rtc.addMethod} size={16} />}
              onClick={() => setAddRt(rtc)}>{rtc.label}</Button>
          ))}
        </MStack>
      </Modal>

      {/* …then configure it; on create it's saved to the stack and set back into the picker. */}
      {addRt && (
        <AddResourceDialog rt={addRt}
          existingCount={stack.nodes.filter(n => n.addMethod === addRt.addMethod).length}
          totalCount={stack.nodes.length}
          nodes={stack.nodes}
          onCreate={(newNode, refIds, usedByIds, extra) => {
            const eid = () => "e" + rid();
            const edges = [
              ...refIds.map(toNodeId => ({ id: eid(), fromNodeId: newNode.id, toNodeId, kind: "reference" })),
              ...usedByIds.map(fromNodeId => ({ id: eid(), fromNodeId, toNodeId: newNode.id, kind: "reference" })),
              ...(extra?.edges ?? []),
            ];
            const extraPackages = [...stack.extraPackages];
            if (newNode.composite && addRt.package && !extraPackages.some(p => p.id === addRt.package))
              extraPackages.push({ id: addRt.package, version: addRt.packageVersion || "" });
            const target = addTarget;
            api.saveStack({ ...stack, nodes: [...stack.nodes, newNode, ...(extra?.nodes ?? [])], edges: [...stack.edges, ...edges], extraPackages })
              .then(s => { setStack(s); target?.onPick(newNode.varName); });
          }}
          onClose={() => setAddRt(null)} />
      )}
    </MStack>
  );
}
