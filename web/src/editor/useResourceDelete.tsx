import { useCallback, useState } from "react";
import { Modal, Stack as MStack, Group, Text, Button, Checkbox } from "@mantine/core";
import { removeNode, orphanableDeps, type Stack, type Node, type OrphanDep } from "../model";
import * as api from "../api";
import { confirmDelete, toastOk, toastErr } from "../ui";

// Modal dialog showing orphanable dependencies for deletion confirmation.
export function SmartDeleteModal({ node, deps, onConfirm, onCancel }:
  { node: Node; deps: OrphanDep[]; onConfirm: (ids: string[]) => void; onCancel: () => void }) {
  const [checked, setChecked] = useState<Record<string, boolean>>(
    () => Object.fromEntries(deps.map(d => [d.node.id, true])));
  return (
    <Modal opened onClose={onCancel} title={`Delete "${node.resourceName}"?`} size="md" centered>
      <MStack gap="sm">
        <Text size="sm" c="dimmed">Also remove these resources it uses (nothing else references them)?</Text>
        <MStack gap={4}>
          {deps.map(d => (
            <Checkbox key={d.node.id} checked={!!checked[d.node.id]}
              onChange={() => setChecked(c => ({ ...c, [d.node.id]: !c[d.node.id] }))}
              label={d.owned ? `${d.node.resourceName} (companion)` : d.node.resourceName} />
          ))}
        </MStack>
        <Group justify="flex-end" gap="xs" mt="xs">
          <Button variant="subtle" onClick={onCancel}>Cancel</Button>
          <Button color="red" onClick={() => onConfirm(deps.filter(d => checked[d.node.id]).map(d => d.node.id))}>Delete</Button>
        </Group>
      </MStack>
    </Modal>
  );
}

// Smart delete hook handling orphanable deps and multi-selection; returns deleteOne, deleteMany, dialog.
export function useResourceDelete(stack: Stack, setStack: (s: Stack) => void, onAfter?: () => void) {
  const [del, setDel] = useState<{ node: Node; deps: OrphanDep[] } | null>(null);

  const deleteOne = useCallback((nodeId: string) => {
    const n = stack.nodes.find(x => x.id === nodeId);
    if (!n) return;
    const deps = orphanableDeps(stack, nodeId);
    if (deps.length > 0) { setDel({ node: n, deps }); return; }
    confirmDelete(`"${n.resourceName}"`, "This also removes its connections and any code that references it.").then(ok => {
      if (!ok) return;
      api.saveStack(removeNode(stack, nodeId)).then(s => { setStack(s); onAfter?.(); toastOk("Resource deleted"); }).catch(toastErr);
    });
  }, [stack, setStack, onAfter]);

  const applyDelete = useCallback((nodeId: string, alsoRemove: string[]) => {
    let next = removeNode(stack, nodeId);
    for (const d of alsoRemove) next = removeNode(next, d);
    api.saveStack(next).then(s => { setStack(s); onAfter?.(); setDel(null); toastOk("Deleted"); }).catch(toastErr);
  }, [stack, setStack, onAfter]);

  const deleteMany = useCallback((ids: string[]) => {
    const real = ids.filter(id => stack.nodes.some(n => n.id === id));
    if (real.length === 0) return;
    if (real.length === 1) { deleteOne(real[0]); return; }

    confirmDelete(`${real.length} resources`, "This also removes their connections and any code that references them.").then(ok => {
      if (!ok) return;
      const next = real.reduce((s, id) => removeNode(s, id), stack);
      api.saveStack(next).then(s => { setStack(s); onAfter?.(); toastOk(`Deleted ${real.length}`); }).catch(toastErr);
    });
  }, [stack, setStack, onAfter, deleteOne]);

  const dialog = del
    ? <SmartDeleteModal node={del.node} deps={del.deps} onCancel={() => setDel(null)} onConfirm={ids => applyDelete(del.node.id, ids)} />
    : null;

  return { deleteOne, deleteMany, dialog };
}
