import { useState } from "react";
import { Modal, Title, Stack, TextInput, Group, Button, Text, Alert, Radio, Badge } from "@mantine/core";
import { IconBrandGithub, IconAlertTriangle, IconArrowLeft } from "@tabler/icons-react";
import * as api from "../api";
import { toastOk, toastErr } from "../ui";

type Detected = { hasCompose: boolean; hasAppHost: boolean; name?: string };

export function GitImportModal({ onClose, onImported }: { onClose: () => void; onImported: (stackId: string) => void }) {
  const [url, setUrl] = useState("");
  const [branch, setBranch] = useState("");
  const [subdir, setSubdir] = useState("");
  const [busy, setBusy] = useState(false);
  const [detected, setDetected] = useState<Detected | null>(null);
  const [mode, setMode] = useState("");

  const inspect = async () => {
    if (!url.trim()) { toastErr("Enter a repository URL"); return; }
    setBusy(true);
    try {
      const d = await api.gitInspect({ url: url.trim(), branch: branch.trim() || undefined, subdir: subdir.trim() || undefined });
      if (!d.hasCompose && !d.hasAppHost) { toastErr("No docker-compose.yml and no .NET Aspire AppHost found in this repo"); return; }
      setDetected(d);
      setMode(d.hasAppHost ? "runasis" : "compose");
    } catch (e) { toastErr(e, "Could not read the repository"); } finally { setBusy(false); }
  };

  const importRepo = async () => {
    setBusy(true);
    try {
      const s = await api.gitImport({ url: url.trim(), branch: branch.trim() || undefined, subdir: subdir.trim() || undefined, mode });
      toastOk(`Imported "${s.name}" from Git`);
      onImported(s.id);
    } catch (e) { toastErr(e, "Git import failed"); } finally { setBusy(false); }
  };

  const opts: { value: string; label: string; desc: string; badge?: string }[] = [];
  if (detected?.hasAppHost) {
    opts.push({ value: "runasis", label: "Run the AppHost as-is", desc: "Keeps the whole project and runs it unchanged. The visual editor is locked (unlock to edit, which regenerates the code).", badge: "recommended" });
    opts.push({ value: "apphost", label: "Import into the visual builder", desc: "Parses the AppHost into editable nodes. Loops, conditionals and helper methods may be lost — best for simple AppHosts." });
  }
  if (detected?.hasCompose) opts.push({ value: "compose", label: "Docker Compose", desc: "Imports docker-compose.yml as AddContainer/AddDockerfile resources." });

  return (
    <Modal opened onClose={onClose} size="lg" title={<Group gap={8}><IconBrandGithub size={18} /><Title order={5}>Import from Git</Title></Group>}>
      {!detected ? (
        <Stack gap="md">
          <Text size="sm" c="dimmed">Clones a public repository. AspireUI detects a <b>docker-compose.yml</b> and/or a <b>.NET Aspire AppHost</b> and lets you choose how to import.</Text>
          <TextInput label="Repository URL" placeholder="https://github.com/user/repo(.git)" value={url}
            onChange={e => setUrl(e.currentTarget.value)} data-autofocus />
          <Group grow>
            <TextInput label="Branch" placeholder="default branch" value={branch} onChange={e => setBranch(e.currentTarget.value)} />
            <TextInput label="Subdirectory" placeholder="repo root" value={subdir} onChange={e => setSubdir(e.currentTarget.value)} />
          </Group>
          <Alert color="gray" p="xs" icon={<IconAlertTriangle size={15} />}>Public repositories only for now.</Alert>
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose}>Cancel</Button>
            <Button loading={busy} onClick={inspect}>Continue</Button>
          </Group>
        </Stack>
      ) : (
        <Stack gap="md">
          <Group gap={8}>
            <Text size="sm" fw={600}>{detected.name}</Text>
            {detected.hasAppHost && <Badge size="sm" variant="light" color="violet">Aspire AppHost</Badge>}
            {detected.hasCompose && <Badge size="sm" variant="light" color="blue">Compose</Badge>}
          </Group>
          <Radio.Group value={mode} onChange={setMode}>
            <Stack gap="xs">
              {opts.map(o => (
                <Radio key={o.value} value={o.value}
                  label={<span><b>{o.label}</b>{o.badge && <Badge size="xs" variant="light" color="teal" ml={6}>{o.badge}</Badge>}<Text size="xs" c="dimmed">{o.desc}</Text></span>} />
              ))}
            </Stack>
          </Radio.Group>
          <Group justify="space-between">
            <Button variant="subtle" color="gray" leftSection={<IconArrowLeft size={14} />} onClick={() => setDetected(null)}>Back</Button>
            <Group>
              <Button variant="default" onClick={onClose}>Cancel</Button>
              <Button loading={busy} onClick={importRepo} leftSection={<IconBrandGithub size={16} />}>Import</Button>
            </Group>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
