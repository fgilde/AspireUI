import { useState } from "react";
import { Modal, Title, Stack, TextInput, PasswordInput, Autocomplete, Group, Button, Text, Alert, Radio, Badge } from "@mantine/core";
import { IconBrandGithub, IconAlertTriangle, IconArrowLeft, IconLock } from "@tabler/icons-react";
import * as api from "../api";
import { toastOk, toastErr } from "../ui";

type Detected = { hasCompose: boolean; hasAppHost: boolean; name?: string };

export function GitImportModal({ onClose, onImported }: { onClose: () => void; onImported: (stackId: string) => void }) {
  const [url, setUrl] = useState("");
  const [branch, setBranch] = useState("");
  const [subdir, setSubdir] = useState("");
  const [busy, setBusy] = useState(false);
  const [choice, setChoice] = useState<Detected | null>(null);
  const [mode, setMode] = useState("apphost");
  const [branches, setBranches] = useState<string[]>([]);
  const [loadingBranches, setLoadingBranches] = useState(false);
  const [authToken, setAuthToken] = useState("");

  const req = () => ({ url: url.trim(), branch: branch.trim() || undefined, subdir: subdir.trim() || undefined, authToken: authToken.trim() || undefined });

  const loadBranches = async () => {
    if (!url.trim()) return;
    setLoadingBranches(true);
    try { setBranches((await api.gitBranches(url.trim(), authToken.trim() || undefined)).branches); }
    catch { setBranches([]); }
    finally { setLoadingBranches(false); }
  };

  const doImport = async (m: string) => {
    setBusy(true);
    try {
      const s = await api.gitImport({ ...req(), mode: m });
      toastOk(`Imported "${s.name}" from Git`);
      onImported(s.id);
    } catch (e) { toastErr(e, "Git import failed"); } finally { setBusy(false); }
  };

  const inspect = async () => {
    if (!url.trim()) { toastErr("Enter a repository URL"); return; }
    setBusy(true);
    try {
      const d = await api.gitInspect(req());
      if (!d.hasCompose && !d.hasAppHost) { toastErr("No .NET Aspire AppHost and no docker-compose.yml found in this repo"); return; }
      if (d.hasAppHost && d.hasCompose) { setChoice(d); setMode("apphost"); }
      else await doImport(d.hasAppHost ? "apphost" : "compose");
    } catch (e) { toastErr(e, "Could not read the repository"); } finally { setBusy(false); }
  };

  return (
    <Modal opened onClose={onClose} size="lg" title={<Group gap={8}><IconBrandGithub size={18} /><Title order={5}>Import from Git</Title></Group>}>
      {!choice ? (
        <Stack gap="md">
          <Text size="sm" c="dimmed">Clones a public repository and imports it. AspireUI runs an existing <b>.NET Aspire AppHost</b> as-is, or maps a <b>docker-compose.yml</b> to resources.</Text>
          <TextInput label="Repository URL" placeholder="https://github.com/user/repo" value={url}
            onChange={e => setUrl(e.currentTarget.value)} onBlur={loadBranches} data-autofocus />
          <Group grow>
            <Autocomplete label="Branch" placeholder={loadingBranches ? "loading branches…" : "default branch"}
              data={branches} value={branch} onChange={setBranch} onFocus={() => { if (!branches.length) loadBranches(); }} />
            <TextInput label="Subdirectory" placeholder="repo root" value={subdir} onChange={e => setSubdir(e.currentTarget.value)} />
          </Group>
          <PasswordInput label="Access token" leftSection={<IconLock size={14} />}
            placeholder="only for private repos — leave blank for public"
            value={authToken} onChange={e => setAuthToken(e.currentTarget.value)}
            description="A read-only personal access token (GitHub/GitLab/Gitea). Stored with the stack for updates. Use a scoped token, not your main one." />
          <Alert color="gray" p="xs" icon={<IconAlertTriangle size={15} />}>Public repos work without a token. HTTPS only.</Alert>
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose}>Cancel</Button>
            <Button loading={busy} onClick={inspect}>Continue</Button>
          </Group>
        </Stack>
      ) : (
        <Stack gap="md">
          <Group gap={8}>
            <Text size="sm" fw={600}>{choice.name}</Text>
            <Badge size="sm" variant="light" color="violet">Aspire AppHost</Badge>
            <Badge size="sm" variant="light" color="blue">Compose</Badge>
          </Group>
          <Text size="sm" c="dimmed">This repo has both — how do you want to import it?</Text>
          <Radio.Group value={mode} onChange={setMode}>
            <Stack gap="xs">
              <Radio value="apphost" label={<span><b>Run the Aspire AppHost</b><Text size="xs" c="dimmed">Keeps and runs the project as-is; the visual editor is locked (unlock to edit).</Text></span>} />
              <Radio value="compose" label={<span><b>Docker Compose</b><Text size="xs" c="dimmed">Maps docker-compose.yml to AddContainer / AddDockerfile resources you can edit.</Text></span>} />
            </Stack>
          </Radio.Group>
          <Group justify="space-between">
            <Button variant="subtle" color="gray" leftSection={<IconArrowLeft size={14} />} onClick={() => setChoice(null)}>Back</Button>
            <Group>
              <Button variant="default" onClick={onClose}>Cancel</Button>
              <Button loading={busy} onClick={() => doImport(mode)} leftSection={<IconBrandGithub size={16} />}>Import</Button>
            </Group>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
