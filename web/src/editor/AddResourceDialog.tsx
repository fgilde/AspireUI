import { useState } from "react";
import { Modal, TextInput, NumberInput, Switch, Select, Stack as MStack, Button, Group, Fieldset } from "@mantine/core";
import type { CatalogOverload, CatalogParam, Node, ResourceType } from "../model";
import { sanitizeIdentifier, toLiteral, configureLiteral } from "../model";

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

  // keyPrefix lets configure sub-fields live under "configureName.fieldName" in the flat values map.
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
    return <TextInput key={key} label={p.label} withAsterisk={p.required} value={value}
      onChange={e => set(e.currentTarget.value)} />;
  };

  const literalFor = (p: CatalogParam): string =>
    p.type === "configure"
      ? configureLiteral(p.fields ?? [], n => values[`${p.name}.${n}`] ?? "")
      : toLiteral(values[p.name] ?? p.default ?? "", p.type, p.enumTypeName);

  const create = () => {
    if (!overload || !canCreate) return;
    const literals = overload.params.map(literalFor);
    let end = literals.length;
    while (end > 0) {
      const p = overload.params[end - 1];
      const empty = p.type === "configure" ? literals[end - 1] === "" : !(values[p.name] ?? p.default ?? "");
      if (!p.required && empty) end--; else break;
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
        {overload?.params.map(p => field(p))}
        <Group justify="end" mt="sm">
          <Button variant="subtle" onClick={onClose}>Cancel</Button>
          <Button disabled={!canCreate} onClick={create}>Create</Button>
        </Group>
      </MStack>
    </Modal>
  );
}
