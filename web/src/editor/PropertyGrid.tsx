import { useEffect, useMemo, useState } from "react";
import { TextInput, NumberInput, Switch, Select, Stack as MStack, Button, Group, Divider, ActionIcon, Text } from "@mantine/core";
import { IconPlus, IconX } from "@tabler/icons-react";
import type { Stack, Node, ResourceType, CatalogParam } from "../model";
import { setAddArg, toLiteral, fromLiteral, readWithRows, writeWithRows, matchOverloadByArity } from "../model";
import * as api from "../api";

const ENV_METHOD = "WithEnvironment";

function field(p: CatalogParam, value: string, onChange: (v: string) => void) {
  if (p.type === "int" || p.type === "number") return <NumberInput key={p.name} label={p.label} withAsterisk={p.required}
    allowDecimal={p.type === "number"} value={value === "" ? "" : Number(value)} onChange={v => onChange(String(v ?? ""))} />;
  if (p.type === "bool") return <Switch key={p.name} label={p.label}
    checked={value === "true"} onChange={e => onChange(e.currentTarget.checked ? "true" : "false")} />;
  if (p.type === "enum") return <Select key={p.name} label={p.label} withAsterisk={p.required}
    data={p.options ?? []} value={value || null} onChange={v => onChange(v ?? "")} />;
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
  useEffect(() => { setDraft(node); setRawMethod(""); setRawArgs(""); }, [node.id]);

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

  return (
    <MStack gap="sm">
      <TextInput label="Name" value={draft.resourceName}
        onChange={e => commit({ ...draft, resourceName: e.currentTarget.value })} />
      {matchOverloadByArity(rt?.addOverloads ?? [], draft.addArgs.length)?.params.map((p, i) => field(p, fromLiteral(draft.addArgs[i] ?? '""'),
        v => commit(setAddArg(draft, i, toLiteral(v, p.type, p.enumTypeName)))))}

      {hasEnv && (
        <div>
          <Divider my="xs" label="Environment variables" labelPosition="left" />
          {envRows.map((row, ri) => (
            <Group key={ri} gap="xs" mb={4} align="end">
              <TextInput style={{ flex: 1 }} placeholder="NAME" value={fromLiteral(row[0] ?? '""')}
                onChange={e => {
                  const nr = envRows.map(r => [...r]); nr[ri][0] = toLiteral(e.currentTarget.value, "string");
                  commit(writeWithRows(draft, ENV_METHOD, nr));
                }} />
              <TextInput style={{ flex: 1 }} placeholder="value" value={fromLiteral(row[1] ?? '""')}
                onChange={e => {
                  const nr = envRows.map(r => [...r]); nr[ri][1] = toLiteral(e.currentTarget.value, "string");
                  commit(writeWithRows(draft, ENV_METHOD, nr));
                }} />
              <ActionIcon variant="subtle" color="red" onClick={() => commit(writeWithRows(draft, ENV_METHOD, envRows.filter((_, x) => x !== ri)))}>
                <IconX size={14} />
              </ActionIcon>
            </Group>
          ))}
          <Button size="xs" variant="light" leftSection={<IconPlus size={12} />}
            onClick={() => commit(writeWithRows(draft, ENV_METHOD, [...envRows, ['""', '""']]))}>Add variable</Button>
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
                  {rowParams.map((p, pi) => field(p, fromLiteral(row[pi] ?? '""'), v => {
                    const nr = rows.map(r => [...r]); while (nr[ri].length <= pi) nr[ri].push('""');
                    nr[ri][pi] = toLiteral(v, p.type, p.enumTypeName); commit(writeWithRows(draft, method, nr));
                  }))}
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
        <Select
          label="Add capability"
          placeholder="Search…"
          searchable
          clearable
          value={null}
          data={available.map(w => ({ value: w.method, label: w.label }))}
          onChange={v => v && addCapability(v)}
        />
      )}

      <Divider my="xs" label="Raw call (advanced)" labelPosition="left" />
      <Text size="xs" c="dimmed">For anything not covered above. Args are raw C# literals.</Text>
      <Group gap="xs" align="end">
        <TextInput style={{ flex: 1 }} label="Method" placeholder="WithSomething" value={rawMethod} onChange={e => setRawMethod(e.currentTarget.value)} />
        <TextInput style={{ flex: 2 }} label="Args" placeholder='"a", 1, true' value={rawArgs} onChange={e => setRawArgs(e.currentTarget.value)} />
        <Button size="sm" variant="light" leftSection={<IconPlus size={12} />} onClick={addRaw}>Add</Button>
      </Group>
    </MStack>
  );
}
