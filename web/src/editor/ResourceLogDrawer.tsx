import { useEffect, useMemo, useRef, useState } from "react";
import { Drawer, ScrollArea, Text, Group, Badge, TextInput, ActionIcon, Tooltip, Switch } from "@mantine/core";
import { IconSearch, IconDownload, IconTrash } from "@tabler/icons-react";

interface Line { text: string; stderr: boolean }

// Live console logs for one running resource, streamed from the server SSE endpoint
// (GET /stacks/{id}/resources/{name}/logs -> WatchResourceConsoleLogs). EventSource carries the
// session cookie automatically (same origin), so no extra auth wiring is needed.
function LogStream({ stackId, name, display }: { stackId: string; name: string; display: string }) {
  const [lines, setLines] = useState<Line[]>([]);
  const [filter, setFilter] = useState("");
  const [errOnly, setErrOnly] = useState(false);
  const viewport = useRef<HTMLDivElement>(null);
  const atBottom = useRef(true);

  useEffect(() => {
    setLines([]); setFilter(""); setErrOnly(false);
    const es = new EventSource(`/api/stacks/${stackId}/resources/${encodeURIComponent(name)}/logs`);
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

  const shown = useMemo(() => {
    const f = filter.trim().toLowerCase();
    return lines.filter(l => (!errOnly || l.stderr) && (!f || l.text.toLowerCase().includes(f)));
  }, [lines, filter, errOnly]);

  // Autoscroll to newest unless the user scrolled up to read history.
  useEffect(() => {
    if (atBottom.current) viewport.current?.scrollTo({ top: viewport.current.scrollHeight });
  }, [shown]);

  const download = () => {
    const blob = new Blob([lines.map(l => l.text).join("\n")], { type: "text/plain" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = `${display}.log`;
    a.click();
    URL.revokeObjectURL(a.href);
  };

  // Highlight filter matches inline.
  const render = (text: string) => {
    const f = filter.trim();
    if (!f) return text;
    const i = text.toLowerCase().indexOf(f.toLowerCase());
    if (i < 0) return text;
    return <>{text.slice(0, i)}<mark style={{ background: "var(--mantine-color-yellow-light)", color: "inherit" }}>{text.slice(i, i + f.length)}</mark>{text.slice(i + f.length)}</>;
  };

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%" }}>
      <Group px="sm" py={6} gap="xs" wrap="nowrap">
        <TextInput size="xs" style={{ flex: 1 }} placeholder="Filter lines…" leftSection={<IconSearch size={13} />}
          value={filter} onChange={e => setFilter(e.currentTarget.value)} />
        <Switch size="xs" label="stderr only" checked={errOnly} onChange={e => setErrOnly(e.currentTarget.checked)} />
        <Text size="xs" c="dimmed">{shown.length}/{lines.length}</Text>
        <Tooltip label="Download log" withArrow><ActionIcon size="sm" variant="subtle" onClick={download}><IconDownload size={14} /></ActionIcon></Tooltip>
        <Tooltip label="Clear view" withArrow><ActionIcon size="sm" variant="subtle" onClick={() => setLines([])}><IconTrash size={14} /></ActionIcon></Tooltip>
      </Group>
      <ScrollArea style={{ flex: 1 }} viewportRef={viewport}
        onScrollPositionChange={({ y }) => {
          const el = viewport.current;
          atBottom.current = !el || el.scrollHeight - (y + el.clientHeight) < 40;
        }}>
        <div style={{ fontFamily: "var(--mantine-font-family-monospace)", fontSize: 12, whiteSpace: "pre-wrap", padding: "4px 10px" }}>
          {shown.length === 0
            ? <Text size="sm" c="dimmed">{lines.length ? "No matching lines." : "Waiting for output…"}</Text>
            : shown.map((l, i) => (
                <div key={i} style={{ color: l.stderr ? "var(--mantine-color-red-4)" : undefined }}>{render(l.text)}</div>
              ))}
        </div>
      </ScrollArea>
    </div>
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
      {target && <LogStream stackId={stackId} name={target.name} display={target.display} />}
    </Drawer>
  );
}
