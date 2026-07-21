import { useEffect, useMemo, useState } from "react";
import { Modal, Stack as MStack, Group, Button, Text, ScrollArea, UnstyledButton, TextInput, Loader } from "@mantine/core";
import { IconFolder, IconFile, IconArrowUp, IconCornerDownLeft, IconSearch } from "@tabler/icons-react";
import * as api from "../api";
import type { FsListing } from "../api";

// Server-side path picker: browses the host filesystem via GET /fs (paths must resolve on the machine
// the stacks run on, not the browser). Keeps an internal navigation history so the mouse "back" button
// steps up through visited folders; a filter box narrows the current listing.
export function PathPickerModal({ opened, initial, onPick, onClose }:
  { opened: boolean; initial?: string; onPick: (path: string) => void; onClose: () => void }) {
  const [history, setHistory] = useState<(string | null)[]>([null]);
  const [listing, setListing] = useState<FsListing | null>(null);
  const [loading, setLoading] = useState(false);
  const [manual, setManual] = useState("");
  const [filter, setFilter] = useState("");
  const current = history[history.length - 1] ?? null;

  const navigate = (path: string | null) => setHistory(h => [...h, path]);
  const back = () => setHistory(h => (h.length > 1 ? h.slice(0, -1) : h));

  // Reset the history when (re)opened for a new field.
  useEffect(() => { if (opened) { setHistory([initial || null]); setFilter(""); } /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [opened]);

  // Load whenever the current path changes; a bad/empty path falls back to the roots (drives / "/").
  useEffect(() => {
    if (!opened) return;
    setLoading(true);
    setFilter("");
    api.browseFs(current ?? undefined)
      .then(l => { setListing(l); setManual(l.path ?? ""); setLoading(false); })
      .catch(() => {
        if (current) { setHistory(h => (h.length > 1 ? h.slice(0, -1) : [null])); return; } // pop bad path
        setListing({ path: null, parent: null, entries: [] });
        setLoading(false);
      });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [history, opened]);

  // Mouse "back" button (button 3) steps up the history instead of navigating the whole app away.
  useEffect(() => {
    if (!opened) return;
    const onDown = (e: MouseEvent) => { if (e.button === 3) { e.preventDefault(); back(); } };
    window.addEventListener("mousedown", onDown);
    return () => window.removeEventListener("mousedown", onDown);
  }, [opened]);

  const entries = useMemo(() => {
    const f = filter.trim().toLowerCase();
    return (listing?.entries ?? []).filter(e => !f || e.name.toLowerCase().includes(f));
  }, [listing, filter]);

  return (
    <Modal opened={opened} onClose={onClose} title="Pick a path" size="lg">
      <MStack gap="xs">
        <Group gap="xs" wrap="nowrap">
          <TextInput style={{ flex: 1 }} value={manual} onChange={e => setManual(e.currentTarget.value)}
            placeholder="Type or paste a path, or browse below"
            onKeyDown={e => { if (e.key === "Enter") navigate(manual); }} />
          <Button variant="light" onClick={() => navigate(manual)} leftSection={<IconCornerDownLeft size={14} />}>Go</Button>
        </Group>

        <Group gap="xs">
          <Button size="xs" variant="default" leftSection={<IconArrowUp size={13} />}
            disabled={!listing?.parent && !listing?.path}
            onClick={() => navigate(listing?.parent ?? null)}>Up</Button>
          <Text size="xs" c="dimmed" truncate style={{ flex: 1 }}>{listing?.path ?? "Drives"}</Text>
          {loading && <Loader size="xs" />}
        </Group>

        <TextInput size="xs" placeholder="Filter this folder…" value={filter}
          onChange={e => setFilter(e.currentTarget.value)} leftSection={<IconSearch size={13} />} />

        <ScrollArea h={320} style={{ border: "1px solid var(--mantine-color-default-border)", borderRadius: 6 }}>
          <MStack gap={0} p={4}>
            {entries.map(e => (
              <UnstyledButton key={e.path} px={8} py={5} style={{ borderRadius: 4, fontSize: 13 }} className="ctx-item"
                onClick={() => e.isDir ? navigate(e.path) : onPick(e.path)}>
                <Group gap={7} wrap="nowrap">
                  {e.isDir ? <IconFolder size={15} color="var(--mantine-color-yellow-text)" /> : <IconFile size={15} color="var(--mantine-color-dimmed)" />}
                  <Text size="sm" truncate>{e.name}</Text>
                </Group>
              </UnstyledButton>
            ))}
            {listing && entries.length === 0 && <Text size="sm" c="dimmed" p={8}>{filter ? "No match" : "Empty folder"}</Text>}
          </MStack>
        </ScrollArea>

        <Group justify="space-between">
          <Text size="xs" c="dimmed">Folder to open, file to pick. Mouse back = up.</Text>
          <Group gap="xs">
            <Button variant="subtle" onClick={onClose}>Cancel</Button>
            <Button disabled={!listing?.path} onClick={() => listing?.path && onPick(listing.path)}>Use this folder</Button>
          </Group>
        </Group>
      </MStack>
    </Modal>
  );
}
