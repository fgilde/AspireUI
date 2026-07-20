import { useEffect, useRef, useState } from "react";
import { ScrollArea, Table, Badge, Text, Code, Group, ThemeIcon } from "@mantine/core";
import { IconCircleCheck, IconAlertTriangle } from "@tabler/icons-react";
import { useEditor } from "./DockLayout";

// Live validation results (Roslyn diagnostics over the generated code). Flashes when the header
// badge is clicked so it's obvious where the details landed.
export function ValidationPanel() {
  const { diagnostics, flashValidation } = useEditor();
  const [flash, setFlash] = useState(false);
  const first = useRef(true);
  useEffect(() => {
    if (first.current) { first.current = false; return; }  // don't flash on mount
    setFlash(true);
    const t = window.setTimeout(() => setFlash(false), 900);
    return () => window.clearTimeout(t);
  }, [flashValidation]);

  const errors = diagnostics.filter(d => d.severity === "error");
  const warns = diagnostics.filter(d => d.severity === "warning");

  return (
    <ScrollArea style={{ height: "100%", transition: "background-color .2s", backgroundColor: flash ? "var(--mantine-color-yellow-light)" : undefined }} px="sm" py="xs">
      {diagnostics.length === 0 ? (
        <Group gap={8}><ThemeIcon color="green" variant="light" size="sm"><IconCircleCheck size={14} /></ThemeIcon>
          <Text size="sm" c="green">No issues — the generated code compiles cleanly.</Text></Group>
      ) : (
        <>
          <Group gap={8} mb="xs">
            <ThemeIcon color={errors.length ? "red" : "yellow"} variant="light" size="sm"><IconAlertTriangle size={14} /></ThemeIcon>
            <Text size="sm" fw={600}>{errors.length} error(s), {warns.length} warning(s)</Text>
          </Group>
          <Table verticalSpacing={4} striped highlightOnHover>
            <Table.Tbody>
              {diagnostics.map((d, i) => (
                <Table.Tr key={i}>
                  <Table.Td width={70}><Badge size="xs" color={d.severity === "error" ? "red" : "yellow"}>{d.severity}</Badge></Table.Td>
                  <Table.Td><Code style={{ whiteSpace: "pre-wrap", fontSize: 11, background: "transparent" }}>{d.message}</Code></Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </>
      )}
    </ScrollArea>
  );
}
