import { useEffect, useState } from "react";
import { Modal, Stack as MStack, Group, Button, Text, ScrollArea, UnstyledButton, TextInput, Loader } from "@mantine/core";
import { IconFolder, IconFile, IconArrowUp, IconCornerDownLeft } from "@tabler/icons-react";
import * as api from "../api";
import type { FsListing } from "../api";

// Server-side path picker: browses the host filesystem via GET /fs (the picker must resolve paths on
// the machine the stacks run on, not the browser). Folders are navigable; "Use this folder" picks the
// current directory, clicking a file picks the file.
export function PathPickerModal({ opened, initial, onPick, onClose }:
  { opened: boolean; initial?: string; onPick: (path: string) => void; onClose: () => void }) {
  const [listing, setListing] = useState<FsListing | null>(null);
  const [loading, setLoading] = useState(false);
  const [manual, setManual] = useState("");

  const go = (path?: string) => {
    setLoading(true);
    api.browseFs(path).then(l => { setListing(l); setManual(l.path ?? ""); }).catch(() => setListing(null)).finally(() => setLoading(false));
  };
  useEffect(() => { if (opened) go(initial || undefined); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [opened]);

  return (
    <Modal opened={opened} onClose={onClose} title="Pick a path" size="lg">
      <MStack gap="xs">
        <Group gap="xs" wrap="nowrap">
          <TextInput style={{ flex: 1 }} value={manual} onChange={e => setManual(e.currentTarget.value)}
            placeholder="Type or paste a path, or browse below"
            onKeyDown={e => { if (e.key === "Enter") go(manual); }} />
          <Button variant="light" onClick={() => go(manual)} leftSection={<IconCornerDownLeft size={14} />}>Go</Button>
        </Group>

        <Group gap="xs">
          <Button size="xs" variant="default" leftSection={<IconArrowUp size={13} />}
            disabled={!listing?.parent && !listing?.path}
            onClick={() => go(listing?.parent ?? undefined)}>Up</Button>
          <Text size="xs" c="dimmed" truncate style={{ flex: 1 }}>{listing?.path ?? "Drives"}</Text>
          {loading && <Loader size="xs" />}
        </Group>

        <ScrollArea h={320} style={{ border: "1px solid var(--mantine-color-default-border)", borderRadius: 6 }}>
          <MStack gap={0} p={4}>
            {(listing?.entries ?? []).map(e => (
              <UnstyledButton key={e.path} px={8} py={5} style={{ borderRadius: 4, fontSize: 13 }} className="ctx-item"
                onClick={() => e.isDir ? go(e.path) : onPick(e.path)}>
                <Group gap={7} wrap="nowrap">
                  {e.isDir ? <IconFolder size={15} color="var(--mantine-color-yellow-text)" /> : <IconFile size={15} color="var(--mantine-color-dimmed)" />}
                  <Text size="sm" truncate>{e.name}</Text>
                </Group>
              </UnstyledButton>
            ))}
            {listing && listing.entries.length === 0 && <Text size="sm" c="dimmed" p={8}>Empty folder</Text>}
          </MStack>
        </ScrollArea>

        <Group justify="space-between">
          <Text size="xs" c="dimmed">Click a folder to open it, a file to pick it.</Text>
          <Group gap="xs">
            <Button variant="subtle" onClick={onClose}>Cancel</Button>
            <Button disabled={!listing?.path} onClick={() => listing?.path && onPick(listing.path)}>Use this folder</Button>
          </Group>
        </Group>
      </MStack>
    </Modal>
  );
}
