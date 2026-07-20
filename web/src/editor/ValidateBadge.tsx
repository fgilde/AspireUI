import { Badge, Tooltip } from "@mantine/core";
import { IconCircleCheck, IconAlertTriangle } from "@tabler/icons-react";
import { useEditor } from "./DockLayout";

// Compact health indicator in the header; clicking opens (and flashes) the Validation panel.
export function ValidateBadge() {
  const { diagnostics, showValidation } = useEditor();
  const errors = diagnostics.filter(d => d.severity === "error").length;
  const warns = diagnostics.filter(d => d.severity === "warning").length;
  return (
    <Tooltip label={errors ? `${errors} error(s) — click for details` : warns ? `${warns} warning(s) — click for details` : "Compiles cleanly"} withArrow>
      <Badge variant="light" style={{ cursor: "pointer" }} onClick={showValidation}
        color={errors ? "red" : warns ? "yellow" : "green"}
        leftSection={errors || warns ? <IconAlertTriangle size={12} /> : <IconCircleCheck size={12} />}>
        {errors ? errors : warns ? warns : "OK"}
      </Badge>
    </Tooltip>
  );
}
