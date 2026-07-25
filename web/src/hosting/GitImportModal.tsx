import { useState } from "react";
import { Modal, Title, Stack, TextInput, Group, Button, Text, Alert } from "@mantine/core";
import { IconBrandGithub, IconAlertTriangle } from "@tabler/icons-react";
import * as api from "../api";
import { toastOk, toastErr } from "../ui";

// Create stack from public git repo w/ docker-compose; remembers URL+branch for re-pull & redeploy.
export function GitImportModal({ onClose, onImported }: { onClose: () => void; onImported: (stackId: string) => void }) {
  const [url, setUrl] = useState("");
  const [branch, setBranch] = useState("");
  const [subdir, setSubdir] = useState("");
  const [busy, setBusy] = useState(false);

  const importRepo = async () => {
    if (!url.trim()) { toastErr("Enter a repository URL"); return; }
    setBusy(true);
    try {
      const s = await api.gitImport({ url: url.trim(), branch: branch.trim() || undefined, subdir: subdir.trim() || undefined });
      toastOk(`Imported "${s.name}" from Git`);
      onImported(s.id);
    } catch (e) { toastErr(e, "Git import failed"); } finally { setBusy(false); }
  };

  return (
    <Modal opened onClose={onClose} size="lg" title={<Group gap={8}><IconBrandGithub size={18} /><Title order={5}>Deploy from Git</Title></Group>}>
      <Stack gap="md">
        <Text size="sm" c="dimmed">Clones a public repository and imports its <b>docker-compose.yml</b> as a new stack. You can then deploy it, and re-pull the latest from the app's page.</Text>
        <TextInput label="Repository URL" placeholder="https://github.com/user/repo(.git)" value={url}
          onChange={e => setUrl(e.currentTarget.value)} data-autofocus />
        <Group grow>
          <TextInput label="Branch" placeholder="default branch" value={branch} onChange={e => setBranch(e.currentTarget.value)} />
          <TextInput label="Subdirectory" placeholder="repo root" value={subdir} onChange={e => setSubdir(e.currentTarget.value)}
            description="If the compose file isn't in the root." />
        </Group>
        <Alert color="gray" p="xs" icon={<IconAlertTriangle size={15} />}>Public repositories only for now — private repos, Dockerfile builds and secrets come later.</Alert>
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>Cancel</Button>
          <Button loading={busy} onClick={importRepo} leftSection={<IconBrandGithub size={16} />}>Import</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
