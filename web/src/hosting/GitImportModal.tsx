import { useState, useEffect } from "react";
import { Modal, Title, Stack, TextInput, NumberInput, PasswordInput, Autocomplete, Group, Button, Text, Alert, Radio, Badge, Checkbox, Loader, Center, Progress } from "@mantine/core";
import { IconBrandGithub, IconAlertTriangle, IconArrowLeft, IconLock } from "@tabler/icons-react";
import * as api from "../api";
import { toastOk, toastErr } from "../ui";

type ComposeFile = { path: string; content: string };
type Manifest = { file: string; app: string; image: string; port: number };
type Detected = { hasCompose: boolean; hasAppHost: boolean; name?: string; composeFiles: ComposeFile[]; manifest?: Manifest | null };
type EnvVar = { name: string; def: string; secret: boolean };
type Service = { name: string; image: string; proxy: boolean; port?: number };
type Step = "form" | "choice" | "files" | "services" | "env";

const scanEnv = (contents: string[]): EnvVar[] => {
  const seen = new Map<string, string>();
  const re = /\$\{([A-Za-z0-9_]+)(?::-([^}]*))?\}/g;
  for (const c of contents) { let m: RegExpExecArray | null; while ((m = re.exec(c))) if (!seen.has(m[1])) seen.set(m[1], m[2] ?? ""); }
  return [...seen].map(([name, def]) => ({ name, def, secret: /(PASSWORD|SECRET|KEY|TOKEN|PAT)/i.test(name) }));
};

const parseServices = (contents: string[]): Service[] => {
  const images = new Map<string, string>();
  const ports = new Map<string, number>();
  for (const c of contents) {
    const lines = c.replace(/\r\n/g, "\n").split("\n");
    let inSvc = false, cur: string | null = null, inPorts = false;
    for (const ln of lines) {
      if (/^services:\s*$/.test(ln)) { inSvc = true; cur = null; continue; }
      if (inSvc && /^\S/.test(ln)) { inSvc = false; cur = null; }
      if (!inSvc) continue;
      const m = ln.match(/^ {2}([A-Za-z0-9._-]+):\s*$/);
      if (m) { cur = m[1]; inPorts = false; if (!images.has(cur)) images.set(cur, ""); continue; }
      const im = ln.match(/^\s+image:\s*["']?([^"'\s]+)/);
      if (im && cur && !images.get(cur)) images.set(cur, im[1]);
      if (/^\s+(ports|expose):/.test(ln)) { inPorts = true; continue; }
      if (inPorts && !/^\s+-/.test(ln)) inPorts = false;
      if (inPorts && cur && !ports.get(cur)) {
        // "8080:80", "127.0.0.1:8080:80", "80", "80/tcp" — the container port is the last number
        const nums = ln.match(/\d+/g);
        if (nums?.length) ports.set(cur, Number(nums[/\/(tcp|udp)/.test(ln) ? nums.length - 2 : nums.length - 1] ?? nums[nums.length - 1]));
      }
    }
  }
  return [...images].map(([name, image]) => ({ name, image, proxy: /(caddy|nginx|traefik|haproxy|envoy)/i.test(image), port: ports.get(name) }));
};

const parseManifest = (text: string): Manifest | null => {
  try {
    const j = JSON.parse(text);
    const app = Array.isArray(j) ? j[0] : j;
    return app?.image && app?.port ? { file: "aspireui-app.json", app: app.label || app.id, image: app.image, port: app.port } : null;
  } catch { return null; }
};

const repoName = (u: string) => (u.trim().replace(/\/+$/, "").split("/").pop() ?? "").replace(/\.git$/i, "");
const b64Text = (b64: string) => { try { return new TextDecoder().decode(Uint8Array.from(atob(b64), c => c.charCodeAt(0))); } catch { return ""; } };
type LocalSource = { name: string; sources: { path: string; content: string }[] };

export function GitImportModal({ onClose, onImported, local, hosting }: { onClose: () => void; onImported: (stackId: string) => void; local?: LocalSource; hosting?: boolean }) {
  const [name, setName] = useState("");
  const [url, setUrl] = useState("");
  const [branch, setBranch] = useState("");
  const [subdir, setSubdir] = useState("");
  const [authToken, setAuthToken] = useState("");
  const [busy, setBusy] = useState(false);
  const [branches, setBranches] = useState<string[]>([]);
  const [loadingBranches, setLoadingBranches] = useState(false);
  const [branchError, setBranchError] = useState<string | null>(null);

  const [step, setStep] = useState<Step>("form");
  const [detected, setDetected] = useState<Detected | null>(null);
  const [mode, setMode] = useState("apphost");
  const [selFiles, setSelFiles] = useState<string[]>([]);
  const [services, setServices] = useState<Service[]>([]);
  const [selServices, setSelServices] = useState<string[]>([]);
  const [servicePorts, setServicePorts] = useState<Record<string, number | "">>({});
  const [envVars, setEnvVars] = useState<EnvVar[]>([]);
  const [envVals, setEnvVals] = useState<Record<string, string>>({});
  const [uploadPct, setUploadPct] = useState<number | null>(null);

  const req = () => ({ url: url.trim(), branch: branch.trim() || undefined, subdir: subdir.trim() || undefined, authToken: authToken.trim() || undefined });
  const contentsOf = (files: string[]) => (detected?.composeFiles ?? []).filter(f => files.includes(f.path)).map(f => f.content);

  const loadBranches = async () => {
    if (!url.trim()) return;
    setLoadingBranches(true);
    setBranchError(null);
    try {
      const list = (await api.gitBranches(url.trim(), authToken.trim() || undefined)).branches;
      setBranches(list);
      if (list.length === 0) setBranchError("no branches reported — the default branch is used");
    }
    catch (e) { setBranches([]); setBranchError(e instanceof Error && e.message ? e.message : "could not read the branches"); }
    finally { setLoadingBranches(false); }
  };

  // Read them while the URL is still being typed, so the list is there before the field is used.
  useEffect(() => {
    if (local || url.trim().length < 5) return;
    const t = setTimeout(loadBranches, 700);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [url, authToken]);

  const doImport = async (m: string, files?: string[], env?: Record<string, string>, svcs?: string[]) => {
    setBusy(true);
    const ports = Object.fromEntries(Object.entries(servicePorts)
      .filter(([svc, v]) => Number(v) > 0 && (svcs ?? []).includes(svc))
      .map(([svc, v]) => [svc, Number(v)]));
    const withPorts = Object.keys(ports).length ? ports : undefined;
    try {
      const s = local
        ? await api.localImportProgress({ name: name.trim() || local.name, mode: m, sources: local.sources, files, services: svcs, env, servicePorts: withPorts }, setUploadPct)
        : await api.gitImport({ ...req(), name: name.trim() || undefined, mode: m, files, env, services: svcs, servicePorts: withPorts });
      toastOk(`Imported "${s.name}"`);
      onImported(s.id);
    } catch (e) { toastErr(e, "Import failed"); } finally { setBusy(false); setUploadPct(null); }
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
      if (!d.hasCompose && !d.hasAppHost && !d.manifest) { toastErr(`No ${"aspireui-app.json"}, no .NET Aspire AppHost and no docker-compose file found in this repo`); return; }
      setDetected(d);
      const alternatives = [!!d.manifest, d.hasAppHost, d.hasCompose].filter(Boolean).length;
      if (alternatives > 1) { setMode(d.manifest ? "manifest" : "apphost"); setStep("choice"); }
      else if (d.manifest) await doImport("manifest");
      else if (d.hasAppHost) await doImport("apphost");
      else startCompose(d.composeFiles);
    } catch (e) { toastErr(e, "Could not read the repository"); } finally { setBusy(false); }
  };

  const backTo = (target: Step) => { if (target === "form" && local) onClose(); else setStep(target); };
  const backFromServices = () => backTo((detected?.composeFiles.length ?? 0) > 1 ? "files" : detected?.hasAppHost ? "choice" : "form");
  const portless = services.filter(s => !s.port && selServices.includes(s.name));

  const backFromEnv = () => backTo(services.length > 1 ? "services" : (detected?.composeFiles.length ?? 0) > 1 ? "files" : detected?.hasAppHost ? "choice" : "form");

  // Local (folder/zip) source: detect right away, no clone — then the same steps as git.
  useEffect(() => {
    if (!local) return;
    setName(local.name);
    const composeFiles = local.sources.filter(s => !s.path.includes("/") && /compose.*\.ya?ml$/i.test(s.path)).map(s => ({ path: s.path, content: b64Text(s.content) }));
    const hasAppHost = local.sources.some(s => /\.csproj$/i.test(s.path) && /(Aspire\.AppHost\.Sdk|<IsAspireHost>\s*true)/i.test(b64Text(s.content)));
    const manifestSrc = local.sources.find(s => s.path === "aspireui-app.json");
    const manifest = manifestSrc ? parseManifest(b64Text(manifestSrc.content)) : null;
    if (composeFiles.length === 0 && !hasAppHost && !manifest) { toastErr("No aspireui-app.json, no .NET Aspire AppHost and no docker-compose file found in these files"); onClose(); return; }
    const d = { hasCompose: composeFiles.length > 0, hasAppHost, name: local.name, composeFiles, manifest };
    setDetected(d);
    const alternatives = [!!manifest, hasAppHost, d.hasCompose].filter(Boolean).length;
    if (alternatives > 1) { setMode(manifest ? "manifest" : "apphost"); setStep("choice"); }
    else if (manifest) doImport("manifest");
    else if (d.hasAppHost) doImport("apphost");
    else startCompose(d.composeFiles);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <Modal opened onClose={onClose} size="lg" zIndex={400}
      title={<Group gap={8}><IconBrandGithub size={18} /><Title order={5}>{local ? "Import files" : hosting ? "Install from Git" : "Import from Git"}</Title></Group>}>
      {uploadPct !== null && (
        <Stack gap={4} mb="md">
          <Text size="sm">{uploadPct < 100 ? `Uploading ${uploadPct}%…` : "Importing…"}</Text>
          <Progress value={uploadPct} animated={uploadPct >= 100} />
        </Stack>
      )}
      {local && !detected && <Center py={40}><Loader /></Center>}
      {step === "form" && !local && (
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            {hosting
              ? <>Clones a repository and deploys it straight to hosting — a <b>docker-compose</b> file or an existing <b>.NET Aspire AppHost</b>. No editor, and you can put it on a domain right after.</>
              : <>Clones a repository and imports it. AspireUI runs an existing <b>.NET Aspire AppHost</b> as-is, or maps a <b>docker-compose</b> file to resources.</>}
          </Text>
          <TextInput label="Stack name" placeholder="auto from repo name if blank" value={name}
            onChange={e => setName(e.currentTarget.value)} />
          <TextInput label="Repository URL" placeholder="https://github.com/user/repo" value={url}
            onChange={e => setUrl(e.currentTarget.value)} onBlur={() => { loadBranches(); if (!name.trim()) setName(repoName(url)); }} data-autofocus />
          <Group grow>
            <Autocomplete label="Branch" placeholder={loadingBranches ? "loading branches…" : "default branch"}
              data={branches} value={branch} onChange={setBranch} onFocus={() => { if (!branches.length) loadBranches(); }}
              rightSection={loadingBranches ? <Loader size={14} /> : undefined}
              error={branchError}
              // above this modal's own z-index, or the suggestions render behind it
              comboboxProps={{ zIndex: 500, withinPortal: true }} />
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
            {detected.manifest && <Badge size="sm" variant="light" color="teal">aspireui-app.json</Badge>}
            {detected.hasAppHost && <Badge size="sm" variant="light" color="violet">Aspire AppHost</Badge>}
            {detected.hasCompose && <Badge size="sm" variant="light" color="blue">Compose</Badge>}
          </Group>
          <Text size="sm" c="dimmed">This repo offers more than one way in — pick one.</Text>
          <Radio.Group value={mode} onChange={setMode}>
            <Stack gap="xs">
              {detected.manifest && (
                <Radio value="manifest" label={<span><b>App manifest</b> — {detected.manifest.app}
                  <Text size="xs" c="dimmed">The author's own definition: {detected.manifest.image} on port {detected.manifest.port}, with its volumes, env and secrets.</Text></span>} />
              )}
              {detected.hasAppHost && <Radio value="apphost" label={<span><b>Run the Aspire AppHost</b><Text size="xs" c="dimmed">Keeps and runs the project; edit later via the visual editor.</Text></span>} />}
              {detected.hasCompose && <Radio value="compose" label={<span><b>Docker Compose</b><Text size="xs" c="dimmed">Maps compose services to AddContainer / AddDockerfile resources you can edit.</Text></span>} />}
            </Stack>
          </Radio.Group>
          <Group justify="space-between">
            <Button variant="subtle" color="gray" leftSection={<IconArrowLeft size={14} />} onClick={() => backTo("form")}>Back</Button>
            <Group>
              <Button variant="default" onClick={onClose}>Cancel</Button>
              <Button loading={busy} onClick={() => mode === "compose" ? startCompose(detected.composeFiles) : doImport(mode)}>Continue</Button>
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
            <Button variant="subtle" color="gray" leftSection={<IconArrowLeft size={14} />} onClick={() => backTo(detected.hasAppHost ? "choice" : "form")}>Back</Button>
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
                  label={<span><b>{s.name}</b> {s.image && <Text span size="xs" c="dimmed">— {s.image}</Text>} {s.proxy && <Badge size="xs" variant="light" color="orange" ml={4}>proxy</Badge>}
                    {!s.port && !s.proxy && <Badge size="xs" variant="light" color="yellow" ml={4}>no port</Badge>}</span>} />
              ))}
            </Stack>
          </Checkbox.Group>
          {portless.length > 0 && (
            <Alert color="yellow" p="xs" icon={<IconAlertTriangle size={16} />}>
              <Text size="xs" mb={6}>
                {portless.length === 1 ? "This service declares" : "These services declare"} no <code>ports</code> and no <code>expose</code> — in the
                original compose {portless.length === 1 ? "it is" : "they are"} reached through its reverse proxy. Give the container port here and
                AspireUI publishes it; leave it blank and the app runs but stays unreachable. A <code>build:</code> service falls back to
                its Dockerfile's <code>EXPOSE</code>.
              </Text>
              <Stack gap={6}>
                {portless.map(s => (
                  <Group key={s.name} gap="xs" wrap="nowrap">
                    <Text size="xs" style={{ width: 140 }} truncate>{s.name}</Text>
                    <NumberInput size="xs" w={120} min={1} max={65535} hideControls placeholder="e.g. 3000"
                      value={servicePorts[s.name] ?? ""} onChange={v => setServicePorts(p => ({ ...p, [s.name]: v === "" ? "" : Number(v) }))} />
                  </Group>
                ))}
              </Stack>
            </Alert>
          )}
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
              <Button loading={busy} leftSection={<IconBrandGithub size={16} />} onClick={() => doImport("compose", selFiles, envVals, selServices)}>{hosting ? "Install" : "Import"}</Button>
            </Group>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
