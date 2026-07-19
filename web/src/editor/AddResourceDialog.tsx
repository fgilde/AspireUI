import { useState } from "react";
import { Modal, TextInput, NumberInput, Switch, Select, Stack as MStack, Button, Group } from "@mantine/core";
import type { CatalogOverload, CatalogParam, Node, ResourceType } from "../model";
import { sanitizeIdentifier, toLiteral } from "../model";

function signature(o: CatalogOverload): string {
  return o.params.length === 0 ? "()" : o.params.map(p => p.required ? p.name : `${p.name}?`).join(", ");
}

function defaultName(rt: ResourceType, existingCount: number): string {
  const base = sanitizeIdentifier(rt.label || rt.addMethod.replace(/^Add/, ""));
  const lower = base.charAt(0).toLowerCase() + base.slice(1);
  return existingCount > 0 ? `${lower}${existingCount}` : lower;
}

export function AddResourceDialog({ rt, existingCount, totalCount, onCreate, onClose }: {
  rt: ResourceType; existingCount: number; totalCount: number; onCreate: (node: Node) => void; onClose: () => void;
}) {
  const [name, setName] = useState(() => defaultName(rt, existingCount));
  const [overloadIdx, setOverloadIdx] = useState(0);
  const [values, setValues] = useState<Record<string, string>>({});
  const overload = rt.addOverloads[overloadIdx] ?? rt.addOverloads[0];

  const setValue = (p: string, v: string) => setValues(vs => ({ ...vs, [p]: v }));

  const missingRequired = overload?.params.some(p => p.required && !(values[p.name] ?? p.default ?? "")) ?? false;
  const canCreate = name.trim().length > 0 && !missingRequired;

  const field = (p: CatalogParam) => {
    const value = values[p.name] ?? p.default ?? "";
    if (p.type === "int" || p.type === "number") return <NumberInput key={p.name} label={p.label} withAsterisk={p.required}
      allowDecimal={p.type === "number"} value={value === "" ? "" : Number(value)} onChange={v => setValue(p.name, String(v ?? ""))} />;
    if (p.type === "bool") return <Switch key={p.name} label={p.label}
      checked={value === "true"} onChange={e => setValue(p.name, e.currentTarget.checked ? "true" : "false")} />;
    if (p.type === "enum") return <Select key={p.name} label={p.label} withAsterisk={p.required}
      data={p.options ?? []} value={value || null} onChange={v => setValue(p.name, v ?? "")} />;
    return <TextInput key={p.name} label={p.label} withAsterisk={p.required} value={value}
      onChange={e => setValue(p.name, e.currentTarget.value)} />;
  };

  const create = () => {
    if (!overload || !canCreate) return;
    const literals = overload.params.map(p => toLiteral(values[p.name] ?? p.default ?? "", p.type, p.enumTypeName));
    let end = literals.length;
    while (end > 0) {
      const p = overload.params[end - 1];
      if (!p.required && !(values[p.name] ?? p.default ?? "")) end--; else break;
    }
    const node: Node = {
      id: "n" + crypto.randomUUID().slice(0, 8),
      varName: sanitizeIdentifier(name),
      resourceName: name,
      addMethod: rt.addMethod,
      addArgs: literals.slice(0, end),
      withCalls: [],
      x: 60 + totalCount * 28,
      y: 60 + totalCount * 28,
    };
    onCreate(node);
  };

  return (
    <Modal opened onClose={onClose} title={`Add ${rt.label}`}>
      <MStack gap="sm">
        <TextInput label="Name" withAsterisk value={name} onChange={e => setName(e.currentTarget.value)} />
        {rt.addOverloads.length > 1 && (
          <Select label="Overload" allowDeselect={false}
            data={rt.addOverloads.map((o, i) => ({ value: String(i), label: signature(o) }))}
            value={String(overloadIdx)} onChange={v => setOverloadIdx(Number(v ?? 0))} />
        )}
        {overload?.params.map(field)}
        <Group justify="end" mt="sm">
          <Button variant="subtle" onClick={onClose}>Cancel</Button>
          <Button disabled={!canCreate} onClick={create}>Create</Button>
        </Group>
      </MStack>
    </Modal>
  );
}
