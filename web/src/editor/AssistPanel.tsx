import { useState } from "react";
import { Link } from "react-router-dom";
import { Stack as MStack, Group, Textarea, Button, ScrollArea, Text, Paper, Chip, ActionIcon, Tooltip, SegmentedControl } from "@mantine/core";
import { IconSend, IconX, IconTrash } from "@tabler/icons-react";
import { useEditor } from "./DockLayout";
import * as api from "../api";

interface Entry { prompt: string; reply?: string; error?: string }

// ok() in api.ts throws `${status}: ${bodyText}` — the body is JSON (a plain
// string for 400, {reply} / {reply,errors} for 422, ProblemDetails for 502).
// Pull the human-readable message back out of whichever shape it is.
function extractMessage(err: unknown): string {
  const msg = err instanceof Error ? err.message : String(err);
  const body = msg.slice(msg.indexOf(": ") + 2);
  try {
    const parsed = JSON.parse(body);
    if (typeof parsed === "string") return parsed;
    if (parsed && typeof parsed === "object") {
      if (typeof parsed.reply === "string") return parsed.reply;
      if (typeof parsed.detail === "string") return parsed.detail;
    }
  } catch {
    // body wasn't JSON — fall through to raw text
  }
  return body;
}

export function AssistPanel() {
  const { stack, setStack } = useEditor();
  const [prompt, setPrompt] = useState("");
  const [busy, setBusy] = useState(false);
  const [history, setHistory] = useState<Entry[]>([]);
  const [mode, setMode] = useState<string>(() => localStorage.getItem("aspireui.assistMode") || "graph");
  const setModePersist = (m: string) => { setMode(m); localStorage.setItem("aspireui.assistMode", m); };

  const send = async () => {
    const p = prompt.trim();
    if (!p || busy) return;
    setBusy(true);
    try {
      const { reply, stack: updated } = await (mode === "code" ? api.assistStackCode(stack.id, p) : api.assistStack(stack.id, p));
      setStack(updated);
      setHistory(h => [...h, { prompt: p, reply }]);
      setPrompt("");
    } catch (err) {
      setHistory(h => [...h, { prompt: p, error: extractMessage(err) }]);
    } finally {
      setBusy(false);
    }
  };

  // Read-only: ask the AI to explain the current stack (doesn't change it).
  const explain = async () => {
    if (busy) return;
    setBusy(true);
    try {
      const { reply } = await api.explainStack(stack.id);
      setHistory(h => [...h, { prompt: "Explain this stack", reply }]);
    } catch (err) {
      setHistory(h => [...h, { prompt: "Explain this stack", error: extractMessage(err) }]);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div style={{ height: "100%", display: "flex", flexDirection: "column" }}>
      {history.length > 0 && (
        <Group justify="flex-end" px="sm" pt={4}>
          <Button size="compact-xs" variant="subtle" color="gray" leftSection={<IconTrash size={12} />}
            onClick={() => setHistory([])}>Clear all</Button>
        </Group>
      )}
      <ScrollArea style={{ flex: 1 }} px="sm" py="xs">
        {history.length === 0 && (
          <Text size="sm" c="dimmed">Ask the AI assistant to change this stack, e.g. "add a Redis cache".</Text>
        )}
        <MStack gap="xs">
          {history.map((e, i) => (
            <Paper key={i} withBorder p="xs" radius="sm">
              <Group justify="space-between" wrap="nowrap" align="flex-start" gap={6}>
                <Text size="sm" fw={600} style={{ flex: 1, minWidth: 0 }}>{e.prompt}</Text>
                <Tooltip label="Remove" withArrow>
                  <ActionIcon size="xs" variant="subtle" color="gray"
                    onClick={() => setHistory(h => h.filter((_, j) => j !== i))}><IconX size={13} /></ActionIcon>
                </Tooltip>
              </Group>
              {e.error ? (
                <>
                  <Text size="sm" c="red">{e.error}</Text>
                  {e.error.includes("AI not configured") && (
                    <Text size="sm"><Link to="/settings">Set it in Settings</Link></Text>
                  )}
                </>
              ) : (
                <Text size="sm" c="dimmed">{e.reply}</Text>
              )}
            </Paper>
          ))}
        </MStack>
      </ScrollArea>
      <Group px="xs" pt="xs" gap={6} align="center">
        <Tooltip label="Graph: edits the resource model. Code: rewrites Program.cs, then re-parses it (more robust for some backends)." withArrow multiline w={260}>
          <SegmentedControl size="xs" value={mode} onChange={setModePersist}
            data={[{ label: "Graph", value: "graph" }, { label: "Code", value: "code" }]} />
        </Tooltip>
        <Button size="compact-xs" variant="light" loading={busy} onClick={() => void explain()}>Explain this stack</Button>
        {["Add a Redis cache", "Add a Postgres database", "What could be improved?"].map(s => (
          <Chip key={s} size="xs" checked={false} variant="light" onClick={() => setPrompt(s)}>{s}</Chip>
        ))}
      </Group>
      <Group p="xs" align="flex-end" wrap="nowrap" gap="xs">
        <Textarea
          style={{ flex: 1 }}
          placeholder="Describe what you want to change..."
          autosize minRows={1} maxRows={4}
          value={prompt}
          onChange={e => setPrompt(e.currentTarget.value)}
          onKeyDown={e => {
            if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); void send(); }
          }}
          disabled={busy}
        />
        <Button onClick={() => void send()} loading={busy} disabled={!prompt.trim()} leftSection={<IconSend size={14} />}>
          Send
        </Button>
      </Group>
    </div>
  );
}
