import { useEffect, useState } from "react";
import { TextInput, NumberInput, Switch, Stack as MStack, Button, Group, Divider, ActionIcon } from "@mantine/core";
import { IconPlus, IconX } from "@tabler/icons-react";
import type { Stack, Node, ResourceType, CatalogParam } from "../model";
import { setAddArg, toLiteral, fromLiteral, readWithRows, writeWithRows, matchOverloadByArity } from "../model";
import * as api from "../api";

export function PropertyGrid({ stack, node, rt, setStack }:
  { stack: Stack; node: Node; rt: ResourceType | undefined; setStack: (s: Stack) => void }) {
  const [draft, setDraft] = useState<Node>(node);
  useEffect(() => setDraft(node), [node.id]);

  const commit = (n: Node) => { setDraft(n); api.patchNode(stack.id, n).then(setStack); };

  const field = (p: CatalogParam, value: string, onChange: (v: string) => void) => {
    if (p.type === "int") return <NumberInput key={p.name} label={p.label} value={value === "" ? "" : Number(value)} onChange={v => onChange(String(v ?? ""))} />;
    if (p.type === "bool") return <Switch key={p.name} label={p.label} checked={value === "true"} onChange={e => onChange(e.currentTarget.checked ? "true" : "false")} />;
    return <TextInput key={p.name} label={p.label} value={value} onChange={e => onChange(e.currentTarget.value)} />;
  };

  return (
    <MStack gap="sm">
      <TextInput label="Name" value={draft.resourceName}
        onChange={e => commit({ ...draft, resourceName: e.currentTarget.value })} />
      {matchOverloadByArity(rt?.addOverloads ?? [], draft.addArgs.length)?.params.map((p, i) => field(p, fromLiteral(draft.addArgs[i] ?? '""'),
        v => commit(setAddArg(draft, i, toLiteral(v, p.type, p.enumTypeName)))))}
      {rt?.withs.map(w => {
        const rows = readWithRows(draft, w.method);
        const simplest = [...w.overloads].sort((a, b) => a.params.length - b.params.length)[0]?.params ?? [];
        if (simplest.length === 0 && w.overloads.length <= 1) {
          return <Switch key={w.method} label={w.label} checked={rows.length > 0}
            onChange={e => commit(writeWithRows(draft, w.method, e.currentTarget.checked ? [[]] : []))} />;
        }
        return (
          <div key={w.method}>
            <Divider my="xs" label={w.label} labelPosition="left" />
            {rows.map((row, ri) => {
              const rowParams = matchOverloadByArity(w.overloads, row.length)?.params ?? simplest;
              return (
                <Group key={ri} align="end" gap="xs" mb={4}>
                  {rowParams.map((p, pi) => field(p, fromLiteral(row[pi] ?? '""'), v => {
                    const nr = rows.map(r => [...r]); while (nr[ri].length <= pi) nr[ri].push('""');
                    nr[ri][pi] = toLiteral(v, p.type, p.enumTypeName); commit(writeWithRows(draft, w.method, nr));
                  }))}
                  <ActionIcon variant="subtle" color="red" onClick={() => commit(writeWithRows(draft, w.method, rows.filter((_, x) => x !== ri)))}><IconX size={14} /></ActionIcon>
                </Group>
              );
            })}
            <Button size="xs" variant="light" leftSection={<IconPlus size={12} />}
              onClick={() => commit(writeWithRows(draft, w.method, [...rows, simplest.map(() => '""')]))}>Add {w.label}</Button>
          </div>
        );
      })}
    </MStack>
  );
}
