import { useEffect, useState } from "react";
import { Badge, Tooltip, Loader } from "@mantine/core";
import { IconCircleCheck, IconAlertTriangle } from "@tabler/icons-react";
import { modals } from "@mantine/modals";
import { Table, Text, Code } from "@mantine/core";
import { useEditor } from "./DockLayout";
import * as api from "../api";
import type { CodeDiagnostic } from "../api";

// Canvas-level health: Roslyn diagnostics over the generated code, re-checked when the stack changes.
export function ValidateBadge() {
  const { stack } = useEditor();
  const [diags, setDiags] = useState<CodeDiagnostic[] | null>(null);
  const [busy, setBusy] = useState(false);
  const key = JSON.stringify(stack.nodes) + JSON.stringify(stack.edges) + JSON.stringify(stack.rawStatements);

  useEffect(() => {
    let cancelled = false;
    setBusy(true);
    const t = window.setTimeout(() => {
      api.validateStack(stack.id).then(d => { if (!cancelled) setDiags(d); }).catch(() => { if (!cancelled) setDiags(null); }).finally(() => { if (!cancelled) setBusy(false); });
    }, 500);
    return () => { cancelled = true; window.clearTimeout(t); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);

  if (busy && diags === null) return <Loader size="xs" />;
  if (diags === null) return null;
  const errors = diags.filter(d => d.severity === "error");
  const warns = diags.filter(d => d.severity === "warning");

  const show = () => modals.open({
    title: "Validation", size: "lg",
    children: diags.length === 0 ? <Text size="sm" c="green">No issues — the generated code compiles cleanly.</Text> : (
      <Table verticalSpacing="xs" striped>
        <Table.Tbody>
          {diags.map((d, i) => (
            <Table.Tr key={i}>
              <Table.Td><Badge size="xs" color={d.severity === "error" ? "red" : "yellow"}>{d.severity}</Badge></Table.Td>
              <Table.Td><Code style={{ whiteSpace: "pre-wrap" }}>{d.message}</Code></Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    ),
  });

  return (
    <Tooltip label={errors.length ? `${errors.length} error(s)` : warns.length ? `${warns.length} warning(s)` : "Compiles cleanly"} withArrow>
      <Badge variant="light" style={{ cursor: "pointer" }} onClick={show}
        color={errors.length ? "red" : warns.length ? "yellow" : "green"}
        leftSection={errors.length || warns.length ? <IconAlertTriangle size={12} /> : <IconCircleCheck size={12} />}>
        {errors.length ? errors.length : warns.length ? warns.length : "OK"}
      </Badge>
    </Tooltip>
  );
}
