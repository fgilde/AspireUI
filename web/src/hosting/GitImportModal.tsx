import { useState } from "react";
import { Modal, Title, Stack, TextInput, PasswordInput, Autocomplete, Group, Button, Text, Alert, Radio, Badge, Checkbox } from "@mantine/core";
import { IconBrandGithub, IconAlertTriangle, IconArrowLeft, IconLock } from "@tabler/icons-react";
import * as api from "../api";
import { toastOk, toastErr } from "../ui";

type ComposeFile = { path: string; content: string };
type Detected = { hasCompose: boolean; hasAppHost: boolean; name?: string; composeFiles: ComposeFile[] };
type EnvVar = { name: string; def: string; secret: boolean };
type Service = { name: string; image: string; proxy: boolean };
type Step = "form" | "choice" | "files" | "services" | "env";

const scanEnv = (contents: string[]): EnvVar[] => {
  const seen = new Map<string, string>();
  const re = /\$\{([A-Za-z0-9_]+)(?::-([^}]*))?\}/g;
  for (const c of contents) { let m: RegExpExecArray | null; while ((m = re.exec(c))) if (!seen.has(m[1])) seen.set(m[1], m[2] ?? ""); }
  return [...seen].map(([name, def]) => ({ name, def, secret: /(PASSWORD|SECRET|KEY|TOKEN|PAT)/i.test(name) }));
};

const parseServices = (contents: string[]): Service[] => {
  const images = new Map<string, string>();
  for (const c of contents) {
    const lines = c.replace(/\r\n/g, "\n").split("\n");
    let inSvc = false, cur: string | null = null;
    for (const ln of lines) {
      if (/^services:\s*$/.test(ln)) { inSvc = true; cur = null; continue; }
      if (inSvc && /^\S/.test(ln)) { inSvc = false; cur = null; }
      if (!inSvc) continue;
      const m = ln.match(/^ {2}([A-Za-z0-9._-]+):\s*$/);
      if (m) { cur = m[1]; if (!images.has(cur)) images.set(cur, ""); continue; }
      const im = ln.match(/^\s+image:\s*["']?([^"'\s]+)/);
      if (im && cur && !images.get(cur)) images.set(cur, im[1]);
    }
  }
  return [...images].map(([name, image]) => ({ name, image, proxy: /(caddy|nginx|traefik|haproxy|envoy)/i.test(image) }));
};

export function GitImportModal({ onClose, onImported }: { onClose: () => void; onImported: (stackId: string) => void }) {
  const [url, setUrl] = useState("");
  const [branch, setBranch] = useState("");
  const [subdir, setSubdir] = useState("");
  const [authToken, setAuthToken] = useState("");
  const [busy, setBusy] = useState(false);
  const [branches, setBranches] = useState<string[]>([]);
  const [loadingBranches, setLoadingBranches] = useState(false);

  const [step, setStep] = useState<Step>("form");
  const [detected, setDetected] = useState<Detected | null>(null);
  const [mode, setMode] = useState("apphost");
  const [selFiles, setSelFiles] = useState<string[]>([]);
  const [services, setServices] = useState<Service[]>([]);
  const [selServices, setSelServices] = useState<string[]>([]);
  const [envVars, setEnvVars] = useState<EnvVar[]>([]);
  const [envVals, setEnvVals] = useState<Record<string, string>>({});

  const req = () => ({ url: url.trim(), branch: branch.trim() || undefined, subdir: subdir.trim() || undefined, authToken: authToken.trim() || undefined });
  const contentsOf = (files: string[]) => (detected?.composeFiles ?? []).filter(f => files.includes(f.path)).map(f => f.content);

  const loadBranches = async () => {
    if (!url.trim()) return;
    setLoadingBranches(true);
    try { setBranches((await api.gitBranches(url.trim(), authToken.trim() || undefined)).branches); }
    catch { setBranches([]); }
    finally { setLoadingBranches(false); }
  };

  const doImport = async (m: string, files?: string[], env?: Record<string, string>, svcs?: string[]) => {
    setBusy(true);
    try {
      const s = await api.gitImport({ ...req(), mode: m, files, env, services: svcs });
      toastOk(`Imported "${s.name}" from Git`);
      onImported(s.id);
    } catch (e) { toastErr(e, "Git import failed"); } finally { setBusy(false); }
  };

  const toEnv = (files: string[], svcNames: string[]) => {
    const vars = scanEnv(contentsOf(files));
    if (vars.length === 0) { doImport("compose", files, {}, svcNames); return; }
    setEnvVars(vars);
    setEnvVals(Object.fromEntries(vars.map(v => [v.name, v.def])));
    setStep("env");
  };

  const toServices = (files: string[]) => {
    const svcs = parseServices(contentsOf(files));
    setServices(svcs);
    const nonProxy = svcs.filter(s => !s.proxy).map(s => s.name);
    const sel = nonProxy.length ? nonProxy : svcs.map(s => s.name);
    setSelServices(sel);
    if (svcs.length > 1) setStep("services");
    else toEnv(files, sel);
  };

  const startCompose = (cf: ComposeFile[]) => {
    const def = cf.find(f => f.path === "docker-compose.yml")?.path ?? cf[0]?.path;
    const initial = def ? [def] : [];
    setSelFiles(initial);
    if (cf.length > 1) setStep("files");
    else toServices(initial);
  };

  const inspect = async () => {
    if (!url.trim()) { toastErr("Enter a repository URL"); return; }
    setBusy(true);
    try {
      const d = await api.gitInspect(req());
      if (!d.hasCompose && !d.hasAppHost) { toastErr("No .NET Aspire AppHost and no docker-compose file found in this repo"); return; }
      setDetected(d);
      if (d.hasAppHost && d.hasCompose) { setMode("apphost"); setStep("choice"); }
      else if (d.hasAppHost) await doImport("apphost");
      else startCompose(d.composeFiles);
    } catch (e) { toastErr(e, "Could not read the repository"); } finally { setBusy(false); }
  };

  const backFromServices = () => setStep((detected?.composeFiles.length ?? 0) > 1 ? "files" : detected?.hasAppHost ? "choice" : "form");
  const backFromEnv = () => setStep(services.length > 1 ? "services" : (detected?.composeFiles.length ?? 0) > 1 ? "files" : detected?.hasAppHost ? "choice" : "form");

  return (
    <Modal opened onClose={onClose} size="lg" title={<Group gap={8}><IconBrandGithub size={18} /><Title order={5}>Import from Git</Title></Group>}>
      {step === "form" && (
        <Stack gap="md">
          <Text size="sm" c="dimmed">Clones a repository and imports it. AspireUI runs an existing <b>.NET Aspire AppHost</b> as-is, or maps a <b>docker-compose</b> file to resources.</Text>
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
      )}

      {step === "choice" && detected && (
        <Stack gap="md">
          <Group gap={8}>
            <Text size="sm" fw={600}>{detected.name}</Text>
            <Badge size="sm" variant="light" color="violet">Aspire AppHost</Badge>
            <Badge size="sm" variant="light" color="blue">Compose</Badge>
          </Group>
          <Text size="sm" c="dimmed">This repo has both — how do you want to import it?</Text>
          <Radio.Group value={mode} onChange={setMode}>
            <Stack gap="xs">
              <Radio value="apphost" label={<span><b>Run the Aspire AppHost</b><Text size="xs" c="dimmed">Keeps and runs the project; edit later via the visual editor.</Text></span>} />
              <Radio value="compose" label={<span><b>Docker Compose</b><Text size="xs" c="dimmed">Maps compose services to AddContainer / AddDockerfile resources you can edit.</Text></span>} />
            </Stack>
          </Radio.Group>
          <Group justify="space-between">
            <Button variant="subtle" color="gray" leftSection={<IconArrowLeft size={14} />} onClick={() => setStep("form")}>Back</Button>
            <Group>
              <Button variant="default" onClick={onClose}>Cancel</Button>
              <Button loading={busy} onClick={() => mode === "compose" ? startCompose(detected.composeFiles) : doImport("apphost")}>Continue</Button>
            </Group>
          </Group>
        </Stack>
      )}

      {step === "files" && detected && (
        <Stack gap="md">
          <Text size="sm" c="dimmed">This repo has several compose files. Pick which one(s) to import. Multiple files are merged as an overlay (like <code>docker compose -f a -f b</code>) — later files win.</Text>
          <Checkbox.Group value={selFiles} onChange={setSelFiles}>
            <Stack gap={6}>{detected.composeFiles.map(f => <Checkbox key={f.path} value={f.path} label={f.path} />)}</Stack>
          </Checkbox.Group>
          <Group justify="space-between">
            <Button variant="subtle" color="gray" leftSection={<IconArrowLeft size={14} />} onClick={() => setStep(detected.hasAppHost ? "choice" : "form")}>Back</Button>
            <Group>
              <Button variant="default" onClick={onClose}>Cancel</Button>
              <Button loading={busy} disabled={selFiles.length === 0} onClick={() => toServices(selFiles)}>Continue</Button>
            </Group>
          </Group>
        </Stack>
      )}

      {step === "services" && (
        <Stack gap="md">
          <Text size="sm" c="dimmed">Pick which services to import. Reverse proxies (caddy/nginx/traefik) are unchecked by default — AspireUI exposes apps directly, so you usually don't need them.</Text>
          <Checkbox.Group value={selServices} onChange={setSelServices}>
            <Stack gap={6}>
              {services.map(s => (
                <Checkbox key={s.name} value={s.name}
                  label={<span><b>{s.name}</b> {s.image && <Text span size="xs" c="dimmed">— {s.image}</Text>} {s.proxy && <Badge size="xs" variant="light" color="orange" ml={4}>proxy</Badge>}</span>} />
              ))}
            </Stack>
          </Checkbox.Group>
          <Group justify="space-between">
            <Button variant="subtle" color="gray" leftSection={<IconArrowLeft size={14} />} onClick={backFromServices}>Back</Button>
            <Group>
              <Button variant="default" onClick={onClose}>Cancel</Button>
              <Button loading={busy} disabled={selServices.length === 0} onClick={() => toEnv(selFiles, selServices)}>Continue</Button>
            </Group>
          </Group>
        </Stack>
      )}

      {step === "env" && (
        <Stack gap="md">
          <Text size="sm" c="dimmed">This compose uses environment variables. Fill them in — values are stored with the stack and written to its <code>.env</code>.</Text>
          <Stack gap="xs">
            {envVars.map(v => {
              const set = (val: string) => setEnvVals(p => ({ ...p, [v.name]: val }));
              return v.secret
                ? <PasswordInput key={v.name} label={v.name} value={envVals[v.name] ?? ""} onChange={e => set(e.currentTarget.value)} />
                : <TextInput key={v.name} label={v.name} placeholder={v.def || undefined} value={envVals[v.name] ?? ""} onChange={e => set(e.currentTarget.value)} />;
            })}
          </Stack>
          <Group justify="space-between">
            <Button variant="subtle" color="gray" leftSection={<IconArrowLeft size={14} />} onClick={backFromEnv}>Back</Button>
            <Group>
              <Button variant="default" onClick={onClose}>Cancel</Button>
              <Button loading={busy} leftSection={<IconBrandGithub size={16} />} onClick={() => doImport("compose", selFiles, envVals, selServices)}>Import</Button>
            </Group>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
