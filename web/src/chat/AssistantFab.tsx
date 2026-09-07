import { useEffect, useRef, useState } from "react";
import { useLocation } from "react-router-dom";
import {
  ActionIcon, Affix, Alert, Badge, Box, Button, Code, Drawer, Group, Loader, Menu, Paper,
  ScrollArea, Stack, Text, Textarea, Tooltip, Transition,
} from "@mantine/core";
import {
  IconAlertTriangle, IconChevronDown, IconMessage2, IconPlus, IconSparkles, IconTool, IconTrash,
} from "@tabler/icons-react";
import * as api from "../api";
import type { ChatMessage, ChatSession, ChatStatus, ChatToolCall } from "../api";
import { toastErr } from "../ui";

const LAST_SESSION = "aspireui.chat.session";

// The assistant, reachable from every page. It runs the same tools the MCP server exposes, as the
// person who is typing — so what it can do is exactly what they can do.
export function AssistantFab() {
  const { pathname } = useLocation();
  const [status, setStatus] = useState<ChatStatus | null>(null);
  const [open, setOpen] = useState(false);

  useEffect(() => { api.chatStatus().then(setStatus).catch(() => setStatus(null)); }, []);

  // The editor has its own assistant docked into the canvas; two of them in one screen is noise.
  const hidden = pathname.startsWith("/editor") || pathname === "/login" || pathname === "/setup";
  if (hidden || !status?.configured) return null;

  return (
    <>
      <Affix position={{ bottom: 20, right: 20 }}>
        <Transition mounted={!open} transition="pop" duration={150}>
          {style => (
            <Tooltip label="Ask the assistant" position="left" withArrow>
              <ActionIcon size={52} radius="xl" variant="filled" color="grape" style={style}
                aria-label="Open the assistant" onClick={() => setOpen(true)}>
                <IconSparkles size={24} />
              </ActionIcon>
            </Tooltip>
          )}
        </Transition>
      </Affix>
      <ChatDrawer opened={open} onClose={() => setOpen(false)} status={status} />
    </>
  );
}

function ChatDrawer({ opened, onClose, status }: { opened: boolean; onClose: () => void; status: ChatStatus }) {
  const [sessions, setSessions] = useState<ChatSession[]>([]);
  const [sessionId, setSessionId] = useState<string | null>(() => localStorage.getItem(LAST_SESSION));
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [prompt, setPrompt] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const bottom = useRef<HTMLDivElement>(null);

  const loadSessions = () => api.chatSessions().then(setSessions).catch(() => setSessions([]));

  useEffect(() => { if (opened) loadSessions(); }, [opened]);

  // Opening the drawer resumes where the last conversation left off, or starts one.
  useEffect(() => {
    if (!opened) return;
    if (sessionId) {
      api.chatSession(sessionId)
        .then(r => setMessages(r.messages))
        .catch(() => { setSessionId(null); setMessages([]); });
      return;
    }
    api.newChat().then(s => { setSessionId(s.id); setMessages([]); loadSessions(); }).catch(toastErr);
  }, [opened, sessionId]);

  useEffect(() => { localStorage.setItem(LAST_SESSION, sessionId ?? ""); }, [sessionId]);
  useEffect(() => { bottom.current?.scrollIntoView({ block: "end" }); }, [messages, busy]);

  const send = async () => {
    const text = prompt.trim();
    if (!text || !sessionId || busy) return;
    setPrompt(""); setError(null); setBusy(true);
    // Shown immediately; the server stores its own copy.
    setMessages(m => [...m, { id: -Date.now(), sessionId, role: "user", content: text, at: new Date().toISOString() }]);
    try {
      const answer = await api.chatAsk(sessionId, text);
      setMessages(m => [...m, {
        id: -Date.now() - 1, sessionId, role: "assistant", content: answer.reply,
        at: new Date().toISOString(), tools: answer.tools.length > 0 ? JSON.stringify(answer.tools) : undefined,
      }]);
      loadSessions();
    } catch (e) {
      const m = e instanceof Error ? e.message : String(e);
      try { setError((JSON.parse(m.slice(m.indexOf(": ") + 2)) as { message?: string }).message ?? m); }
      catch { setError(m); }
    } finally { setBusy(false); }
  };

  const startNew = async () => {
    try {
      const s = await api.newChat();
      setSessionId(s.id); setMessages([]); setError(null);
      loadSessions();
    } catch (e) { toastErr(e); }
  };

  const remove = async (id: string) => {
    try {
      await api.deleteChat(id);
      if (id === sessionId) { setSessionId(null); setMessages([]); }
      loadSessions();
    } catch (e) { toastErr(e); }
  };

  const current = sessions.find(s => s.id === sessionId);

  return (
    <Drawer opened={opened} onClose={onClose} position="right" size={520} withOverlay={false}
      title={
        <Group gap={8}>
          <IconSparkles size={18} />
          <Text fw={600}>Assistant</Text>
          {status.tools
            ? <Badge size="xs" variant="light" leftSection={<IconTool size={11} />}>{status.toolCount} tools</Badge>
            : <Badge size="xs" variant="light" color="yellow">no tools</Badge>}
        </Group>
      }
      styles={{ body: { height: "calc(100% - 60px)", display: "flex", flexDirection: "column" } }}>

      <Group justify="space-between" mb="xs">
        <Menu position="bottom-start" withArrow>
          <Menu.Target>
            <Button variant="subtle" size="compact-sm" rightSection={<IconChevronDown size={14} />}>
              {current?.title ?? "New chat"}
            </Button>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Label>Conversations</Menu.Label>
            {sessions.length === 0 && <Menu.Item disabled>None yet</Menu.Item>}
            {sessions.map(s => (
              <Menu.Item key={s.id} leftSection={<IconMessage2 size={14} />}
                rightSection={
                  <ActionIcon size="xs" variant="subtle" color="red" aria-label="Delete conversation"
                    onClick={e => { e.stopPropagation(); remove(s.id); }}>
                    <IconTrash size={12} />
                  </ActionIcon>
                }
                onClick={() => { setSessionId(s.id); setError(null); }}>
                <Text size="sm" truncate maw={260}>{s.title}</Text>
                <Text size="10px" c="dimmed">{new Date(s.updatedAt).toLocaleString()} · {s.messages} messages</Text>
              </Menu.Item>
            ))}
          </Menu.Dropdown>
        </Menu>
        <Button variant="default" size="compact-sm" leftSection={<IconPlus size={14} />} onClick={startNew}>New</Button>
      </Group>

      <ScrollArea style={{ flex: 1 }} type="auto">
        <Stack gap="sm" pb="md">
          {messages.length === 0 && !busy && (
            <Paper withBorder p="sm" radius="md">
              <Text size="sm" fw={500} mb={4}>What can I do here?</Text>
              <Text size="xs" c="dimmed">
                {status.tools
                  ? "Ask about your apps and stacks, or tell me to do something: install an app from " +
                    "the store, deploy a stack, stop one, show me why one is unhealthy. I only have " +
                    "the permissions you have, and I say what I did."
                  : "The configured backend is a local CLI, which cannot call functions — so I can " +
                    "answer questions and tell you where to click, but not operate the instance."}
              </Text>
              {status.model && <Text size="10px" c="dimmed" mt={6}>{status.provider ?? "model"}: {status.model}</Text>}
            </Paper>
          )}
          {messages.map(m => <Turn key={m.id} message={m} />)}
          {busy && <Group gap="xs"><Loader size="xs" /><Text size="xs" c="dimmed">thinking, and possibly doing…</Text></Group>}
          {error && (
            <Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />}
              withCloseButton onClose={() => setError(null)}>
              <Text size="xs">{error}</Text>
            </Alert>
          )}
          <div ref={bottom} />
        </Stack>
      </ScrollArea>

      <Textarea mt="xs" autosize minRows={2} maxRows={6} value={prompt} disabled={busy}
        placeholder={status.tools ? "Ask, or tell me what to do…" : "Ask me something…"}
        onChange={e => setPrompt(e.currentTarget.value)}
        onKeyDown={e => {
          if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(); }
        }} />
      <Group justify="space-between" mt={6}>
        <Text size="10px" c="dimmed">Enter sends · Shift+Enter for a new line</Text>
        <Button size="compact-sm" loading={busy} disabled={!prompt.trim()} onClick={send}>Send</Button>
      </Group>
    </Drawer>
  );
}

function Turn({ message }: { message: ChatMessage }) {
  const mine = message.role === "user";
  const tools: ChatToolCall[] = message.tools ? JSON.parse(message.tools) : [];
  const [shown, setShown] = useState<number | null>(null);

  return (
    <Box style={{ alignSelf: mine ? "flex-end" : "flex-start", maxWidth: "92%" }}>
      <Paper withBorder p="xs" radius="md" bg={mine ? "var(--mantine-color-default-hover)" : undefined}>
        <Text size="sm" style={{ whiteSpace: "pre-wrap" }}>{message.content}</Text>
      </Paper>
      {tools.length > 0 && (
        <Stack gap={2} mt={4}>
          {tools.map((t, i) => (
            <div key={i}>
              <Group gap={6} style={{ cursor: "pointer" }} onClick={() => setShown(s => s === i ? null : i)}>
                <IconTool size={12} color={t.ok ? "var(--mantine-color-teal-6)" : "var(--mantine-color-red-6)"} />
                <Text size="10px" c="dimmed" ff="monospace">{t.name}</Text>
                {!t.ok && <Badge size="xs" color="red" variant="light">refused</Badge>}
              </Group>
              {shown === i && (
                <Code block fz="10px" mt={2}>{`${t.arguments}\n\n${t.result}`}</Code>
              )}
            </div>
          ))}
        </Stack>
      )}
    </Box>
  );
}
