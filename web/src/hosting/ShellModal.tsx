import { useEffect, useRef, useState } from "react";
import { Alert, Badge, Group, Modal, Select, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconTerminal2 } from "@tabler/icons-react";
import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import "@xterm/xterm/css/xterm.css";
import type { Deployment } from "../model";
import * as api from "../api";

// A real shell in a container: one WebSocket, bytes both ways, xterm.js drawing the result. The
// one-shot command runner stays for a stopped or crash-looping app — this needs something to attach to.
export function ShellModal({ d, onClose }: { d: Deployment; onClose: () => void }) {
  const [containers, setContainers] = useState<string[]>([]);
  const [container, setContainer] = useState("");
  const [state, setState] = useState<"idle" | "open" | "closed" | "error">("idle");
  const [message, setMessage] = useState<string | null>(null);
  const [pty, setPty] = useState<boolean | null>(null);
  const host = useRef<HTMLDivElement>(null);
  const term = useRef<Terminal | null>(null);
  const socket = useRef<WebSocket | null>(null);

  useEffect(() => {
    api.hostingServices(d.id).then(list => {
      const names = list.map(x => x.name).filter(Boolean);
      setContainers(names);
      setContainer(c => c || names.find(n => !n.includes("dashboard")) || names[0] || "");
    }).catch(() => setContainers([]));
    api.ptyAvailable(d.id).then(r => setPty(r.pty)).catch(() => setPty(null));
  }, [d.id]);

  // One terminal per chosen container: switching container is a new shell, not a cleared screen.
  useEffect(() => {
    if (!container || !host.current) return;

    const t = new Terminal({
      fontSize: 13,
      fontFamily: "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace",
      cursorBlink: true,
      convertEol: false,
      theme: { background: "#111113" },
    });
    const fit = new FitAddon();
    t.loadAddon(fit);
    t.open(host.current);
    fit.fit();
    term.current = t;

    const url = api.ptyUrl(d.id, container, t.cols, t.rows);
    const ws = new WebSocket(url);
    ws.binaryType = "arraybuffer";
    socket.current = ws;

    ws.onopen = () => { setState("open"); setMessage(null); t.focus(); };
    ws.onmessage = e => {
      if (typeof e.data === "string") t.write(e.data);
      else t.write(new Uint8Array(e.data as ArrayBuffer));
    };
    ws.onerror = () => { setState("error"); setMessage("The connection failed."); };
    ws.onclose = () => { setState(s => s === "error" ? s : "closed"); };

    const typed = t.onData(data => { if (ws.readyState === WebSocket.OPEN) ws.send(data); });
    const resized = t.onResize(({ cols, rows }) => {
      if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ resize: { cols, rows } }));
    });
    const onWindow = () => { try { fit.fit(); } catch { /* the modal is closing */ } };
    window.addEventListener("resize", onWindow);

    return () => {
      window.removeEventListener("resize", onWindow);
      typed.dispose();
      resized.dispose();
      try { ws.close(); } catch { /* already gone */ }
      t.dispose();
      term.current = null;
      socket.current = null;
    };
  }, [d.id, container]);

  return (
    <Modal opened onClose={onClose} size="90%" title={
      <Group gap={8}>
        <IconTerminal2 size={18} />
        <Title order={5}>Shell · {d.name}</Title>
        {state === "open" && <Badge size="sm" color="green" variant="light">connected</Badge>}
        {state === "closed" && <Badge size="sm" color="gray" variant="light">closed</Badge>}
        {pty === false && <Badge size="sm" color="yellow" variant="light">no pty</Badge>}
      </Group>
    }>
      <Stack gap="sm">
        <Group gap="sm" align="flex-end">
          <Select label="Container" data={containers} value={container} w={320} allowDeselect={false}
            onChange={v => setContainer(v ?? "")} disabled={containers.length === 0} />
          {containers.length === 0 && (
            <Text size="sm" c="dimmed">Nothing is running to attach to — use <b>Terminal…</b> for a one-off command.</Text>
          )}
        </Group>
        {message && <Alert color="red" icon={<IconAlertTriangle size={16} />} p="xs">{message}</Alert>}
        {pty === false && (
          <Alert color="yellow" p="xs" icon={<IconAlertTriangle size={16} />}>
            This machine cannot allocate a pseudo-terminal (no <code>script</code>), so the session has
            no terminal semantics: full-screen programs like <code>top</code> and <code>vim</code> will
            not draw properly. Everything else works.
          </Alert>
        )}
        <div ref={host} style={{ height: "62vh", background: "#111113", borderRadius: 6, padding: 6 }} />
        <Text size="xs" c="dimmed">
          Ctrl-C, Ctrl-D and the arrow keys go to the shell. Closing the dialog ends the session and
          kills the process.
        </Text>
      </Stack>
    </Modal>
  );
}
