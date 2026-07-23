import type { ReactNode } from "react";
import { Modal, Group, Text, Badge, Button, Stack as MStack, Image, SimpleGrid, Anchor, Box } from "@mantine/core";
import { IconExternalLink, IconWorld } from "@tabler/icons-react";
import { ResourceGlyph, resourceVisual } from "../resourceIcons";

// Normalized view of anything the store / palette can describe (a container preset, a catalog package,
// or a snippet). Only `label` is required — the modal degrades gracefully when the rest is absent.
export interface AppInfo {
  label: string; group?: string | null; icon?: string | null; description?: string | null;
  website?: string | null; image?: string | null; port?: number | null;
  screenshots?: string[] | null; tags?: string[] | null; custom?: boolean; kindLabel?: string | null;
}

// Shared app-details dialog used by both the editor palette ("i" button) and the app store. One design,
// one place to improve. `onAction` is the primary CTA (add-to-canvas / install); omit it for read-only.
export function AppInfoModal({ info, onClose, onAction, actionLabel = "Add", actionIcon, actionLoading }: {
  info: AppInfo; onClose: () => void; onAction?: () => void;
  actionLabel?: string; actionIcon?: ReactNode; actionLoading?: boolean;
}) {
  const color = resourceVisual(info.icon || "").color;
  const shots = info.screenshots?.filter(Boolean) ?? [];
  return (
    <Modal opened onClose={onClose} size="lg" centered padding={0} radius="md" withCloseButton={false}>
      {/* Hero band */}
      <Box p="lg" style={{
        background: `linear-gradient(135deg, ${color}22, transparent 70%)`,
        borderBottom: "1px solid var(--mantine-color-default-border)",
      }}>
        <Group gap="md" wrap="nowrap" align="flex-start">
          <div style={{ width: 52, height: 52, borderRadius: 12, flexShrink: 0, display: "grid", placeItems: "center",
            background: `${color}1f`, border: `1px solid ${color}44` }}>
            <ResourceGlyph addMethod={info.icon || ""} iconKey={info.icon} size={30} />
          </div>
          <div style={{ minWidth: 0, flex: 1 }}>
            <Text fw={700} size="xl" lh={1.2}>{info.label}</Text>
            <Group gap={6} mt={6}>
              {info.group && <Badge size="sm" variant="light">{info.group}</Badge>}
              {info.custom && <Badge size="sm" variant="light" color="grape">Custom</Badge>}
              {info.kindLabel && <Badge size="sm" variant="outline" color="gray">{info.kindLabel}</Badge>}
              {(info.tags ?? []).filter(t => t !== info.group).slice(0, 4).map(t => (
                <Badge key={t} size="sm" variant="dot" color="gray">{t}</Badge>
              ))}
            </Group>
          </div>
        </Group>
      </Box>

      <MStack gap="md" p="lg">
        {info.description && <Text size="sm">{info.description}</Text>}

        {info.website && (
          <Anchor href={info.website} target="_blank" size="sm">
            <Group gap={6} wrap="nowrap"><IconWorld size={15} />{info.website.replace(/^https?:\/\//, "")}<IconExternalLink size={12} /></Group>
          </Anchor>
        )}

        {shots.length > 0 && (
          <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
            {shots.map(src => (
              <Anchor key={src} href={src} target="_blank" style={{ display: "block", overflow: "hidden", borderRadius: 8, border: "1px solid var(--mantine-color-default-border)" }}>
                <Image src={src} fit="cover" h={130} fallbackSrc="" className="store-shot" />
              </Anchor>
            ))}
          </SimpleGrid>
        )}

        {(info.image || info.port) && (
          <Text size="xs" c="dimmed">
            {info.image && <>Image: <Text span ff="monospace" size="xs">{info.image}</Text></>}
            {info.image && info.port ? " · " : ""}
            {info.port ? `Port ${info.port}` : ""}
          </Text>
        )}

        <Group justify="flex-end" mt="xs">
          <Button variant="default" onClick={onClose}>Close</Button>
          {onAction && (
            <Button leftSection={actionIcon} loading={actionLoading}
              onClick={() => { onAction(); }}>{actionLabel}</Button>
          )}
        </Group>
      </MStack>

      <style>{`.store-shot img{transition:transform .25s ease}.store-shot:hover img{transform:scale(1.06)}`}</style>
    </Modal>
  );
}
