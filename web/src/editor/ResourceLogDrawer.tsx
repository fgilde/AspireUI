import { useEffect, useRef, useState } from "react";
import { Drawer, ScrollArea, Text, Group, Badge } from "@mantine/core";

interface Line { text: string; stderr: boolean }

// Live console logs for one running resource, streamed from the server SSE endpoint
// (GET /stacks/{id}/resources/{name}/logs -> WatchResourceConsoleLogs). EventSource carries the
// session cookie automatically (same origin), so no extra auth wiring is needed.
function LogStream({ stackId, name }: { stackId: string; name: string }) {
  const [lines, setLines] = useState<Line[]>([]);
  const viewport = useRef<HTMLDivElement>(null);
  const atBottom = useRef(true);

  useEffect(() => {
    setLines([]);
    const es = new EventSource(`/stacks/${stackId}/resources/${encodeURIComponent(name)}/logs`);
    es.onmessage = e => {
      try {
        const d = JSON.parse(e.data) as { text: string; stderr?: boolean };
        setLines(prev => {
          const next = [...prev, { text: d.text, stderr: !!d.stderr }];
          return next.length > 2000 ? next.slice(next.length - 2000) : next; // cap buffer
        });
      } catch { /* ignore malformed frame */ }
    };
    return () => es.close();
  }, [stackId, name]);

  // Autoscroll to the newest line unless the user scrolled up to read history.
  useEffect(() => {
    if (atBottom.current) viewport.current?.scrollTo({ top: viewport.current.scrollHeight });
  }, [lines]);

  return (
    <ScrollArea style={{ flex: 1, height: "100%" }} viewportRef={viewport}
      onScrollPositionChange={({ y }) => {
        const el = viewport.current;
        atBottom.current = !el || el.scrollHeight - (y + el.clientHeight) < 40;
      }}>
      <div style={{ fontFamily: "var(--mantine-font-family-monospace)", fontSize: 12, whiteSpace: "pre-wrap", padding: "4px 10px" }}>
        {lines.length === 0
          ? <Text size="sm" c="dimmed">Waiting for output…</Text>
          : lines.map((l, i) => (
              <div key={i} style={{ color: l.stderr ? "var(--mantine-color-red-4)" : undefined }}>{l.text}</div>
            ))}
      </div>
    </ScrollArea>
  );
}

export function ResourceLogDrawer({ stackId, target, onClose }:
  { stackId: string; target: { name: string; display: string } | null; onClose: () => void }) {
  return (
    <Drawer opened={!!target} onClose={onClose} position="bottom" size="45%" withCloseButton
      title={
        <Group gap={8}>
          <Text fw={600}>Logs</Text>
          {target && <Badge variant="light">{target.display}</Badge>}
        </Group>
      }
      styles={{ body: { height: "calc(100% - 60px)", display: "flex", flexDirection: "column", padding: 0 } }}>
      {target && <LogStream stackId={stackId} name={target.name} />}
    </Drawer>
  );
}
