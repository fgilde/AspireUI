import { useEffect } from "react";
import { Table, Kbd, Text } from "@mantine/core";
import { modals } from "@mantine/modals";

const SHORTCUTS: [string, string][] = [
  ["Ctrl / ⌘ + K", "Command palette"],
  ["Ctrl / ⌘ + Z", "Undo canvas edit"],
  ["Ctrl / ⌘ + Shift + Z", "Redo"],
  ["Ctrl / ⌘ + S", "Save code (Code tab)"],
  ["Delete / Backspace", "Delete selected node/edge"],
  ["Right-click node", "Node context menu"],
  ["?", "This help"],
];

// Global "?" opens a shortcuts cheat-sheet (ignored while typing in a field/editor).
export function ShortcutsHelp() {
  useEffect(() => {
    const h = (e: KeyboardEvent) => {
      if (e.key !== "?" || e.ctrlKey || e.metaKey || e.altKey) return;
      const t = e.target as HTMLElement | null;
      if (t?.closest?.(".monaco-editor, input, textarea, [contenteditable=true]")) return;
      e.preventDefault();
      modals.open({
        title: "Keyboard shortcuts",
        children: (
          <Table verticalSpacing="xs">
            <Table.Tbody>
              {SHORTCUTS.map(([k, d]) => (
                <Table.Tr key={k}>
                  <Table.Td><Kbd>{k}</Kbd></Table.Td>
                  <Table.Td><Text size="sm">{d}</Text></Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        ),
      });
    };
    window.addEventListener("keydown", h);
    return () => window.removeEventListener("keydown", h);
  }, []);
  return null;
}
