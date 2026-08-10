import { useState } from "react";
import { Modal, Title, Stack, TextInput, PasswordInput, Group, Button, Text, Alert, Badge, Tooltip, ActionIcon } from "@mantine/core";
import { IconDownload, IconRefresh, IconWorld, IconApps } from "@tabler/icons-react";
import type { ContainerPreset } from "../model";
import { presetParamDefault } from "../model";

export interface InstallOptions { name: string; params: Record<string, string> }

export function InstallOptionsModal({ preset, npm, busy, onClose, onInstall }: {
  preset: ContainerPreset; npm: boolean; busy: boolean; onClose: () => void; onInstall: (o: InstallOptions) => void;
}) {
  const params = preset.params ?? [];
  const [name, setName] = useState(preset.label);
  const [vals, setVals] = useState<Record<string, string>>(
    Object.fromEntries(params.map(p => [p.key, presetParamDefault(p)])));

  const set = (key: string, v: string) => setVals(s => ({ ...s, [key]: v }));

  return (
    <Modal opened onClose={onClose} size="lg" zIndex={400}
      title={<Group gap={8}><IconApps size={18} /><Title order={5}>Install {preset.label}</Title></Group>}>
      <Stack gap="md">
        <Group gap={6}>
          <Badge size="sm" variant="light" color="blue">{preset.image}</Badge>
          <Badge size="sm" variant="light" color="gray">port {preset.port}</Badge>
          {(preset.volumes ?? []).map(([v, target]) => <Badge key={v} size="sm" variant="outline" color="gray">{target}</Badge>)}
        </Group>
        <TextInput label="App name" value={name} onChange={e => setName(e.currentTarget.value)} data-autofocus
          description="Used for the stack, the container names and its volumes." />
        {params.map(p => {
          const label = p.env;
          const desc = p.secret ? "Generated for you — keep it if you have no reason to change it." : undefined;
          return p.secret
            ? <PasswordInput key={p.key} label={label} description={desc} value={vals[p.key] ?? ""}
                onChange={e => set(p.key, e.currentTarget.value)}
                rightSection={
                  <Tooltip label="Generate a new value" withArrow>
                    <ActionIcon variant="subtle" color="gray" onClick={() => set(p.key, presetParamDefault({ ...p, default: "" }))} aria-label="Regenerate">
                      <IconRefresh size={15} />
                    </ActionIcon>
                  </Tooltip>} />
            : <TextInput key={p.key} label={label} value={vals[p.key] ?? ""} onChange={e => set(p.key, e.currentTarget.value)} />;
        })}
        {npm
          ? <Alert color="blue" p="xs" icon={<IconWorld size={16} />}>
              After the app is up, the domain dialog opens so you can put it on your own domain (with HTTPS).
            </Alert>
          : <Text size="xs" c="dimmed">The app gets a free host port; env vars and ports stay editable afterwards under <b>Configure</b>.</Text>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>Cancel</Button>
          <Button loading={busy} leftSection={<IconDownload size={16} />}
            disabled={!name.trim() || params.some(p => p.secret && !(vals[p.key] ?? "").trim())}
            onClick={() => onInstall({ name: name.trim(), params: vals })}>Install</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
