import { useState } from "react";
import { Modal, Title, Stack, TextInput, PasswordInput, Group, Button, Text, Alert, Badge, Tooltip, ActionIcon, Radio } from "@mantine/core";
import { IconDownload, IconRefresh, IconWorld, IconApps } from "@tabler/icons-react";
import type { ContainerPreset } from "../model";
import { presetParamDefault } from "../model";

export interface InstallOptions { name: string; params: Record<string, string>; sourceId?: string }

export function InstallOptionsModal({ preset, npm, busy, onClose, onInstall }: {
  preset: ContainerPreset; npm: boolean; busy: boolean; onClose: () => void; onInstall: (o: InstallOptions) => void;
}) {
  const params = preset.params ?? [];
  const sources = preset.sources ?? [];
  const [sourceId, setSourceId] = useState(sources.find(s => s.default)?.id ?? sources[0]?.id ?? "");
  const image = sources.find(s => s.id === sourceId)?.image ?? preset.image;
  const [name, setName] = useState(preset.label);
  const [vals, setVals] = useState<Record<string, string>>(
    Object.fromEntries(params.map(p => [p.key, presetParamDefault(p)])));

  const set = (key: string, v: string) => setVals(s => ({ ...s, [key]: v }));

  return (
    <Modal opened onClose={onClose} size="lg" zIndex={400}
      title={<Group gap={8}><IconApps size={18} /><Title order={5}>Install {preset.label}</Title></Group>}>
      <Stack gap="md">
        <Group gap={6}>
          <Badge size="sm" variant="light" color="blue">{image}</Badge>
          <Badge size="sm" variant="light" color="gray">port {preset.port}</Badge>
          {(preset.volumes ?? []).map(([v, target]) => <Badge key={v} size="sm" variant="outline" color="gray">{target}</Badge>)}
        </Group>
        {sources.length > 1 && (
          <Radio.Group label="Source" value={sourceId} onChange={setSourceId}
            description="The same app — same port, volumes and settings — from more than one place. Only the image differs.">
            <Stack gap={6} mt={6}>
              {sources.map(s => (
                <Radio key={s.id} value={s.id} label={<span><b>{s.label}</b> <Text span size="xs" c="dimmed" ff="monospace">{s.image}</Text>
                  {s.note && <Text size="xs" c="dimmed">{s.note}</Text>}</span>} />
              ))}
            </Stack>
          </Radio.Group>
        )}
        <TextInput label="App name" value={name} onChange={e => setName(e.currentTarget.value)} data-autofocus
          description="Used for the stack, the container names and its volumes." />
        {params.map(p => {
          const label = p.name || p.env;
          // A secret the app can invent gets one; a secret only the provider can hand out asks for it.
          const generated = p.secret && p.generate !== false;
          const desc = p.hint ?? (generated ? "Generated for you — keep it if you have no reason to change it." : undefined);
          return p.secret
            ? <PasswordInput key={p.key} label={label} description={desc} value={vals[p.key] ?? ""}
                onChange={e => set(p.key, e.currentTarget.value)}
                rightSection={generated
                  ? <Tooltip label="Generate a new value" withArrow>
                      <ActionIcon variant="subtle" color="gray" onClick={() => set(p.key, presetParamDefault({ ...p, default: "" }))} aria-label="Regenerate">
                        <IconRefresh size={15} />
                      </ActionIcon>
                    </Tooltip>
                  : null} />
            : <TextInput key={p.key} label={label} description={p.hint ?? undefined} value={vals[p.key] ?? ""}
                onChange={e => set(p.key, e.currentTarget.value)} />;
        })}
        {npm
          ? <Alert color="blue" p="xs" icon={<IconWorld size={16} />}>
              After the app is up, the domain dialog opens so you can put it on your own domain (with HTTPS).
            </Alert>
          : <Text size="xs" c="dimmed">The app gets a free host port; env vars and ports stay editable afterwards under <b>Configure</b>.</Text>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>Cancel</Button>
          <Button loading={busy} leftSection={<IconDownload size={16} />}
            disabled={!name.trim() || params.some(p => p.secret && p.generate !== false && !(vals[p.key] ?? "").trim())}
            onClick={() => onInstall({ name: name.trim(), params: vals, sourceId: sources.length > 1 ? sourceId : undefined })}>Install</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
