import { useState } from "react";
import { TextInput, Button, Group } from "@mantine/core";
import { notifications } from "@mantine/notifications";
import { modals } from "@mantine/modals";

// Thin wrappers so pages/panels don't each re-derive notification/modal boilerplate.
export const toastOk = (message: string, title = "Done") =>
  notifications.show({ title, message, color: "green" });

export const toastErr = (err: unknown, title = "Something went wrong") =>
  notifications.show({ title, message: err instanceof Error ? err.message : String(err), color: "red" });

export function confirmDelete(what: string, detail?: string): Promise<boolean> {
  return new Promise(resolve => {
    modals.openConfirmModal({
      title: `Delete ${what}?`,
      children: detail ?? "This can't be undone.",
      labels: { confirm: "Delete", cancel: "Cancel" },
      confirmProps: { color: "red" },
      onConfirm: () => resolve(true),
      onCancel: () => resolve(false),
      onClose: () => resolve(false),
    });
  });
}

export function confirmAction(title: string, detail: string, confirmLabel = "Continue"): Promise<boolean> {
  return new Promise(resolve => {
    modals.openConfirmModal({
      title, children: detail,
      labels: { confirm: confirmLabel, cancel: "Cancel" },
      onConfirm: () => resolve(true),
      onCancel: () => resolve(false),
      onClose: () => resolve(false),
    });
  });
}

function PromptBody({ label, initial, onDone }: { label: string; initial: string; onDone: (v: string | null) => void }) {
  const [val, setVal] = useState(initial);
  return (
    <>
      <TextInput label={label} value={val} data-autofocus onChange={e => setVal(e.currentTarget.value)}
        onKeyDown={e => { if (e.key === "Enter") onDone(val.trim() || null); }} />
      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={() => onDone(null)}>Cancel</Button>
        <Button onClick={() => onDone(val.trim() || null)}>OK</Button>
      </Group>
    </>
  );
}

// A proper text-prompt modal (replaces window.prompt).
export function promptText(title: string, label: string, initial = ""): Promise<string | null> {
  return new Promise(resolve => {
    let settled = false;
    const finish = (v: string | null) => { if (settled) return; settled = true; modals.closeAll(); resolve(v); };
    modals.open({ title, children: <PromptBody label={label} initial={initial} onDone={finish} />, onClose: () => finish(null) });
  });
}
