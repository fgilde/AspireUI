import { useEffect, useRef } from "react";
import { Alert } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import type { RunStatus } from "../model";
import { isErrorLine } from "../model";

export function LogsPanel({ runStatus }: { runStatus: RunStatus }) {
  const bottomRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    bottomRef.current?.scrollIntoView({ block: "end" });
  }, [runStatus.log.length]);

  return (
    <div style={{ height: "100%", display: "flex", flexDirection: "column" }}>
      {runStatus.state === "Failed" && (
        <Alert color="red" icon={<IconAlertTriangle size={16} />} radius={0}>
          Run failed — check highlighted lines below
        </Alert>
      )}
      <div style={{ flex: 1, minHeight: 0, overflow: "auto", padding: "4px 8px", fontFamily: "monospace", fontSize: 12 }}>
        {runStatus.log.map((line, i) => (
          <div key={i} style={{ color: isErrorLine(line) ? "var(--mantine-color-red-5)" : undefined, whiteSpace: "pre-wrap" }}>
            {line}
          </div>
        ))}
        <div ref={bottomRef} />
      </div>
    </div>
  );
}
