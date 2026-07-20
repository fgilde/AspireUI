import { useEffect, useMemo, useRef, useState } from "react";
import { Alert, Group, TextInput, Switch, Button } from "@mantine/core";
import { IconAlertTriangle, IconSearch, IconDownload } from "@tabler/icons-react";
import type { RunStatus } from "../model";
import { isErrorLine } from "../model";

export function LogsPanel({ runStatus }: { runStatus: RunStatus }) {
  const bottomRef = useRef<HTMLDivElement>(null);
  const [filter, setFilter] = useState("");
  const [errorsOnly, setErrorsOnly] = useState(false);
  const [autoScroll, setAutoScroll] = useState(true);

  const lines = useMemo(() => {
    const q = filter.trim().toLowerCase();
    return runStatus.log
      .map((line, i) => ({ line, i }))
      .filter(({ line }) => (!q || line.toLowerCase().includes(q)) && (!errorsOnly || isErrorLine(line)));
  }, [runStatus.log, filter, errorsOnly]);

  useEffect(() => {
    if (autoScroll) bottomRef.current?.scrollIntoView({ block: "end" });
  }, [lines.length, autoScroll]);

  const download = () => {
    const blob = new Blob([runStatus.log.join("\n")], { type: "text/plain" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a"); a.href = url; a.download = "run.log"; a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <div style={{ height: "100%", display: "flex", flexDirection: "column" }}>
      {runStatus.state === "Failed" && (
        <Alert color="red" icon={<IconAlertTriangle size={16} />} radius={0}>
          Run failed — check highlighted lines below
        </Alert>
      )}
      <Group gap="xs" px="sm" py={4} wrap="nowrap">
        <TextInput size="xs" style={{ flex: 1 }} placeholder="Filter log…" value={filter}
          onChange={e => setFilter(e.currentTarget.value)} leftSection={<IconSearch size={13} />} />
        <Switch size="xs" label="Errors" checked={errorsOnly} onChange={e => setErrorsOnly(e.currentTarget.checked)} />
        <Switch size="xs" label="Follow" checked={autoScroll} onChange={e => setAutoScroll(e.currentTarget.checked)} />
        <Button size="compact-xs" variant="subtle" leftSection={<IconDownload size={12} />} onClick={download}
          disabled={runStatus.log.length === 0}>Save</Button>
      </Group>
      <div style={{ flex: 1, minHeight: 0, overflow: "auto", padding: "4px 8px", fontFamily: "monospace", fontSize: 12 }}>
        {lines.map(({ line, i }) => (
          <div key={i} style={{ color: isErrorLine(line) ? "var(--mantine-color-red-5)" : undefined, whiteSpace: "pre-wrap" }}>
            {line}
          </div>
        ))}
        <div ref={bottomRef} />
      </div>
    </div>
  );
}
