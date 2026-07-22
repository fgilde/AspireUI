import { useEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { Modal, Stack as MStack, Group, Button, Text, ScrollArea, UnstyledButton, TextInput, Loader } from "@mantine/core";
import { IconFolder, IconFile, IconArrowUp, IconCornerDownLeft, IconSearch } from "@tabler/icons-react";
import * as api from "../api";
import type { FsListing } from "../api";

// Server-side path picker: browses the host filesystem via GET /fs (paths must resolve on the machine
// the stacks run on, not the browser). Internal navigation history + keyboard-first: filter box stays
// focused, arrows move the selection, Enter opens a folder or picks a file, Backspace (empty filter)
// or the Up button steps up a folder.
export function PathPickerModal({ opened, initial, onPick, onClose }:
  { opened: boolean; initial?: string; onPick: (path: string) => void; onClose: () => void }) {
  const [history, setHistory] = useState<(string | null)[]>([null]);
  const [listing, setListing] = useState<FsListing | null>(null);
  const [loading, setLoading] = useState(false);
  const [manual, setManual] = useState("");
  const [filter, setFilter] = useState("");
  const [active, setActive] = useState(0);
  const current = history[history.length - 1] ?? null;
  const filterRef = useRef<HTMLInputElement>(null);
  const activeRef = useRef<HTMLButtonElement>(null);

  const navigate = (path: string | null) => setHistory(h => [...h, path]);
  const back = () => setHistory(h => (h.length > 1 ? h.slice(0, -1) : h));

  const entries = useMemo(() => {
    const f = filter.trim().toLowerCase();
    return (listing?.entries ?? []).filter(e => !f || e.name.toLowerCase().includes(f));
  }, [listing, filter]);

  // Reset history + filter when (re)opened for a new field.
  useEffect(() => { if (opened) { setHistory([initial || null]); setFilter(""); } /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [opened]);

  // Load whenever the current path changes; a bad/empty path falls back to roots (drives / "/").
  useEffect(() => {
    if (!opened) return;
    setLoading(true); setFilter(""); setActive(0);
    api.browseFs(current ?? undefined)
      .then(l => { setListing(l); setManual(l.path ?? ""); setLoading(false); setTimeout(() => filterRef.current?.focus(), 0); })
      .catch(() => {
        if (current) { setHistory(h => (h.length > 1 ? h.slice(0, -1) : [null])); return; }
        setListing({ path: null, parent: null, entries: [] }); setLoading(false);
      });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [history, opened]);

  useEffect(() => { setActive(0); }, [filter]);
  useEffect(() => { activeRef.current?.scrollIntoView({ block: "nearest" }); }, [active]);

  // NB: no browser-history hijack here. An earlier version pushed a dummy history entry + popstate
  // listener so the browser Back button stepped up a folder — but manipulating window.history behind
  // React Router's back desynced its internal location, and after using the picker a "← Stacks"
  // nav("/") updated the URL without re-rendering (dead navigation until a full reload). Folder-up now
  // lives on the Up button, arrow keys, and Backspace (see onFilterKey) — none touch global history.
  const choose = (i: number) => {
    const e = entries[i];
    if (!e) return;
    if (e.isDir) navigate(e.path); else onPick(e.path);
  };
  const onFilterKey = (e: ReactKeyboardEvent) => {
    if (e.key === "ArrowDown") { e.preventDefault(); setActive(a => Math.min(a + 1, entries.length - 1)); }
    else if (e.key === "ArrowUp") { e.preventDefault(); setActive(a => Math.max(a - 1, 0)); }
    else if (e.key === "Enter") { e.preventDefault(); choose(active); }
    else if (e.key === "Backspace" && filter === "") { e.preventDefault(); back(); } // empty filter + backspace = up
  };

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

        <TextInput ref={filterRef} size="xs" placeholder="Filter — ↑/↓ to select, Enter to open/pick" value={filter}
          onChange={e => setFilter(e.currentTarget.value)} onKeyDown={onFilterKey} leftSection={<IconSearch size={13} />} />

        <ScrollArea h={320} style={{ border: "1px solid var(--mantine-color-default-border)", borderRadius: 6 }}>
          <MStack gap={0} p={4}>
            {entries.map((e, i) => (
              <UnstyledButton key={e.path} ref={i === active ? activeRef : undefined}
                px={8} py={5} style={{ borderRadius: 4, fontSize: 13, background: i === active ? "var(--mantine-color-default-hover)" : undefined }}
                onMouseEnter={() => setActive(i)} onClick={() => choose(i)}>
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
          <Text size="xs" c="dimmed">Folder = open, file = pick. Back button / Backspace = up.</Text>
          <Group gap="xs">
            <Button variant="subtle" onClick={onClose}>Cancel</Button>
            <Button disabled={!listing?.path} onClick={() => listing?.path && onPick(listing.path)}>Use this folder</Button>
          </Group>
        </Group>
      </MStack>
    </Modal>
  );
}
