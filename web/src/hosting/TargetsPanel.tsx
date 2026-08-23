import { useEffect, useMemo, useState } from "react";
import {
  Alert, Badge, Button, Code, CopyButton, Divider, Group, Loader, Modal, NumberInput, PasswordInput,
  Select, Stack as MStack, Switch, Table, Text, Textarea, TextInput, Tooltip, ActionIcon, SegmentedControl,
  ScrollArea,
} from "@mantine/core";
import {
  IconAlertCircle, IconCheck, IconCloud, IconCloudUpload, IconCopy, IconPlus, IconRefresh, IconServer2,
  IconTrash, IconTerminal2, IconWorld, IconKey, IconStar, IconStarFilled,
} from "@tabler/icons-react";
import type { DeployTarget, TargetKindInfo, TargetProbe } from "../model";
import * as api from "../api";
import { confirmDelete, toastErr, toastOk } from "../ui";

const KIND_HINT: Record<string, string> = {
  ssh: "A docker daemon reached over ssh: any VM, NAS or bare-metal box. Key authentication only — docker's ssh transport has nowhere to type a password.",
  dockerTcp: "A docker daemon listening on TCP with mutual TLS. Paste the three PEM files the daemon was set up with.",
  k8s: "A Kubernetes cluster. Stacks are deployed as the Helm chart `aspire publish` produces; kubectl and helm ship with AspireUI.",
  aca: "Azure Container Apps, driven with the az CLI. No volumes, no host ports — one container app per compose service.",
  cloudrun: "Google Cloud Run, driven with the gcloud CLI. Best for a single stateless service; there is no service-to-service DNS by name.",
  ecs: "AWS ECS on Fargate, driven with the aws CLI. Needs a cluster, subnets and an execution role that may pull images.",
};

const CLI_NOTE: Record<string, string> = {
  aca: "az",
  cloudrun: "gcloud",
  ecs: "aws",
};

export function targetIcon(kind: string) {
  if (kind === "local") return <IconServer2 size={15} />;
  if (kind === "ssh" || kind === "dockerTcp") return <IconTerminal2 size={15} />;
  if (kind === "k8s") return <IconCloudUpload size={15} />;
  return <IconCloud size={15} />;
}

function ProbeBadge({ t }: { t: DeployTarget }) {
  const p = t.probe;
  if (!p) return <Badge size="sm" variant="light" color="gray">not checked</Badge>;
  if (!p.ok) return (
    <Tooltip label={p.error ?? "unreachable"} withArrow multiline w={320}>
      <Badge size="sm" color="red" variant="light">unreachable</Badge>
    </Tooltip>
  );
  const bits = [p.version, p.arch, p.diskFreeMb ? `${Math.round(p.diskFreeMb / 1024)} GB free` : null]
    .filter(Boolean).join(" · ");
  return (
    <Tooltip label={bits || "reachable"} withArrow>
      <Badge size="sm" color="green" variant="light">reachable</Badge>
    </Tooltip>
  );
}

// One list of every place apps can be deployed to, plus the two ways to add one: connect a machine
// you already have, or have one created at a provider.
export function TargetsSection() {
  const [targets, setTargets] = useState<DeployTarget[] | null>(null);
  const [kinds, setKinds] = useState<TargetKindInfo[]>([]);
  const [edit, setEdit] = useState<DeployTarget | "new" | null>(null);
  const [provision, setProvision] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);

  const load = () => api.listTargets().then(setTargets).catch(() => setTargets([]));
  useEffect(() => { load(); api.targetKinds().then(setKinds).catch(() => setKinds([])); }, []);

  // A target nobody ever tested says nothing useful, so check the unchecked ones once, in the background.
  useEffect(() => {
    const pending = (targets ?? []).filter(t => !t.probe).map(t => t.id);
    if (pending.length === 0) return;
    let alive = true;
    (async () => {
      for (const id of pending) {
        try { await api.probeTarget(id); } catch { /* the row keeps saying "not checked" */ }
      }
      if (alive) load();
    })();
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [targets?.length]);

  const probe = async (t: DeployTarget) => {
    setBusy(t.id);
    try { setTargets((prev) => (prev ?? []).map(x => x.id === t.id ? { ...x, probe: undefined } : x)); await api.probeTarget(t.id); await load(); }
    catch (e) { toastErr(e); }
    finally { setBusy(null); }
  };

  const remove = async (t: DeployTarget) => {
    if (!await confirmDelete(`Remove the target "${t.name}"?`)) return;
    try { await api.deleteTarget(t.id); toastOk(`${t.name} removed`); load(); }
    catch (e) { toastErr(e); }
  };

  const destroy = async (t: DeployTarget) => {
    if (!await confirmDelete(`Destroy the machine behind "${t.name}" at ${t.provider?.kind}? This deletes the server, not just the target.`)) return;
    setBusy(t.id);
    try {
      const r = await api.destroyMachine(t.id);
      if (r.ok) toastOk(`${t.name} destroyed`); else toastErr(r.log);
      load();
    } catch (e) { toastErr(e); } finally { setBusy(null); }
  };

  const setDefault = async (t: DeployTarget) => {
    try { await api.makeTargetDefault(t.id); load(); } catch (e) { toastErr(e); }
  };

  if (!targets) return <Loader size="sm" />;

  return (
    <MStack gap="sm">
      <Group justify="space-between" align="end">
        <Text size="sm" c="dimmed" maw={620}>
          Where stacks and store apps can be deployed. “This machine” is always here and cannot be removed;
          everything else is a box you connect or a machine AspireUI creates for you. Each target keeps its
          own port range, its own domain provider and its own credentials.
        </Text>
        <Group gap="xs">
          <Button size="xs" variant="default" leftSection={<IconCloud size={14} />} onClick={() => setProvision(true)}>
            Create a machine
          </Button>
          <Button size="xs" leftSection={<IconPlus size={14} />} onClick={() => setEdit("new")}>Add target</Button>
        </Group>
      </Group>

      <ScrollArea>
        <Table striped highlightOnHover withTableBorder miw={860}>
          <Table.Thead>
            <Table.Tr>
              <Table.Th miw={200}>Target</Table.Th>
              <Table.Th w={130}>Kind</Table.Th>
              <Table.Th w={190}>Address</Table.Th>
              <Table.Th w={120}>Domains</Table.Th>
              <Table.Th w={80}>Apps</Table.Th>
              <Table.Th w={120}>Status</Table.Th>
              <Table.Th w={150} />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {targets.map(t => (
              <Table.Tr key={t.id}>
                <Table.Td>
                  <Group gap={6} wrap="nowrap">
                    {targetIcon(t.kind)}
                    <div style={{ minWidth: 0 }}>
                      <Group gap={6} wrap="nowrap">
                        <Text size="sm" fw={500} style={{ whiteSpace: "nowrap" }}>{t.name}</Text>
                        {t.default && <Badge size="xs" variant="light">default</Badge>}
                      </Group>
                      <Text size="xs" c="dimmed" truncate>{t.id}{t.notes ? ` · ${t.notes}` : ""}</Text>
                    </div>
                  </Group>
                </Table.Td>
                <Table.Td><Text size="xs">{kinds.find(k => k.kind === t.kind)?.label ?? t.kind}</Text></Table.Td>
                <Table.Td>
                  <Text size="xs" style={{ wordBreak: "break-all" }}>
                    {t.kind === "local" ? "this container's docker"
                      : t.kind === "ssh" ? `${t.ssh?.user}@${t.ssh?.host}:${t.ssh?.port}`
                      : t.kind === "dockerTcp" ? t.dockerHost
                      : t.kind === "k8s" ? `${t.kube?.context ?? "current context"} / ${t.kube?.namespace ?? "default"}`
                      : [t.cloud?.resourceGroup, t.cloud?.project, t.cloud?.cluster, t.cloud?.location].filter(Boolean).join(" · ")}
                  </Text>
                </Table.Td>
                <Table.Td>
                  <Badge size="xs" variant="light" color={t.domains.kind === "none" ? "gray" : "blue"}>
                    {t.domains.kind}
                  </Badge>
                </Table.Td>
                <Table.Td><Text size="xs">{t.deployments}</Text></Table.Td>
                <Table.Td>{busy === t.id ? <Loader size="xs" /> : <ProbeBadge t={t} />}</Table.Td>
                <Table.Td>
                  <Group gap={2} justify="flex-end" wrap="nowrap">
                    <Tooltip label={t.default ? "Default target" : "Make default"} withArrow>
                      <ActionIcon variant="subtle" size="sm" onClick={() => setDefault(t)} disabled={t.default}>
                        {t.default ? <IconStarFilled size={14} /> : <IconStar size={14} />}
                      </ActionIcon>
                    </Tooltip>
                    <Tooltip label="Test the connection" withArrow>
                      <ActionIcon variant="subtle" size="sm" onClick={() => probe(t)}><IconRefresh size={14} /></ActionIcon>
                    </Tooltip>
                    <Tooltip label="Edit" withArrow>
                      <ActionIcon variant="subtle" size="sm" onClick={() => setEdit(t)}><IconWorld size={14} /></ActionIcon>
                    </Tooltip>
                    {t.provider?.serverId && (
                      <Tooltip label={`Destroy the ${t.provider.kind} machine`} withArrow>
                        <ActionIcon variant="subtle" size="sm" color="orange" onClick={() => destroy(t)}><IconCloud size={14} /></ActionIcon>
                      </Tooltip>
                    )}
                    {!t.isLocal && (
                      <Tooltip label="Remove target" withArrow>
                        <ActionIcon variant="subtle" size="sm" color="red" onClick={() => remove(t)}><IconTrash size={14} /></ActionIcon>
                      </Tooltip>
                    )}
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>

      {edit && (
        <TargetEditor
          target={edit === "new" ? null : edit}
          kinds={kinds}
          onClose={() => setEdit(null)}
          onSaved={() => { setEdit(null); load(); }}
        />
      )}
      {provision && <ProvisionModal onClose={() => setProvision(false)} onDone={() => { setProvision(false); load(); }} />}
    </MStack>
  );
}

function TargetEditor({ target, kinds, onClose, onSaved }:
  { target: DeployTarget | null; kinds: TargetKindInfo[]; onClose: () => void; onSaved: () => void }) {
  const isNew = target === null;
  const [kind, setKind] = useState(target?.kind ?? "ssh");
  const [name, setName] = useState(target?.name ?? "");
  const [publicHost, setPublicHost] = useState(target?.publicHost ?? "");
  const [notes, setNotes] = useState(target?.notes ?? "");
  const [portFrom, setPortFrom] = useState(target?.portFrom ?? 20000);
  const [portTo, setPortTo] = useState(target?.portTo ?? 29999);

  const [sshHost, setSshHost] = useState(target?.ssh?.host ?? "");
  const [sshPort, setSshPort] = useState(target?.ssh?.port ?? 22);
  const [sshUser, setSshUser] = useState(target?.ssh?.user ?? "root");
  const [sshKey, setSshKey] = useState("");
  const [pubKey, setPubKey] = useState("");

  const [dockerHost, setDockerHost] = useState(target?.dockerHost ?? "");
  const [ca, setCa] = useState(""); const [cert, setCert] = useState(""); const [key, setKey] = useState("");

  const [kubeContext, setKubeContext] = useState(target?.kube?.context ?? "");
  const [kubeNs, setKubeNs] = useState(target?.kube?.namespace ?? "");
  const [kubeconfig, setKubeconfig] = useState("");

  const [subscription, setSubscription] = useState(target?.cloud?.subscriptionId ?? "");
  const [resourceGroup, setResourceGroup] = useState(target?.cloud?.resourceGroup ?? "");
  const [location, setLocation] = useState(target?.cloud?.location ?? "");
  const [project, setProject] = useState(target?.cloud?.project ?? "");
  const [cluster, setCluster] = useState(target?.cloud?.cluster ?? "");
  const [environment, setEnvironment] = useState(target?.cloud?.environment ?? "");
  const [subnets, setSubnets] = useState(target?.cloud?.subnets ?? "");
  const [securityGroups, setSecurityGroups] = useState(target?.cloud?.securityGroups ?? "");
  const [execRole, setExecRole] = useState(target?.cloud?.executionRoleArn ?? "");
  const [credentials, setCredentials] = useState("");

  const [registryUrl, setRegistryUrl] = useState(target?.registry?.url ?? "");
  const [registryUser, setRegistryUser] = useState(target?.registry?.user ?? "");
  const [registryPassword, setRegistryPassword] = useState("");

  const [domainKind, setDomainKind] = useState(target?.domains.kind ?? "none");
  const [npmUrl, setNpmUrl] = useState(target?.domains.npm?.baseUrl ?? "");
  const [npmEmail, setNpmEmail] = useState(target?.domains.npm?.email ?? "");
  const [npmPassword, setNpmPassword] = useState("");
  const [npmForward, setNpmForward] = useState(target?.domains.npm?.forwardHost ?? "");

  const [probe, setProbe] = useState<TargetProbe | null>(null);
  const [busy, setBusy] = useState(false);

  const compose = kind === "ssh" || kind === "dockerTcp" || kind === "local";

  const body = (): api.TargetRequest => ({
    name, kind, publicHost: publicHost || null, notes: notes || null,
    portFrom, portTo,
    ssh: kind === "ssh" ? { host: sshHost, port: sshPort, user: sshUser, privateKey: sshKey || undefined } : null,
    dockerHost: kind === "dockerTcp" ? dockerHost : null,
    tls: kind === "dockerTcp" && (ca || cert || key) ? { ca: ca || undefined, cert: cert || undefined, key: key || undefined } : null,
    kube: kind === "k8s" ? { context: kubeContext, namespace: kubeNs, kubeconfig: kubeconfig || undefined } : null,
    cloud: kind === "aca" || kind === "cloudrun" || kind === "ecs" ? {
      subscriptionId: subscription, resourceGroup, location, project, cluster, environment,
      subnets, securityGroups, executionRoleArn: execRole,
      credentials: credentials || undefined,
    } : null,
    registry: registryUrl || registryUser || registryPassword
      ? { url: registryUrl, user: registryUser, password: registryPassword || undefined } : null,
    domains: {
      kind: domainKind,
      npm: domainKind === "npm" ? { baseUrl: npmUrl, email: npmEmail, password: npmPassword || undefined, forwardHost: npmForward } : null,
    },
  });

  const test = async () => {
    setBusy(true); setProbe(null);
    try { setProbe(await api.testTarget(body())); }
    catch (e) { toastErr(e); }
    finally { setBusy(false); }
  };

  const save = async () => {
    setBusy(true);
    try {
      const saved = isNew ? await api.createTarget(body()) : await api.updateTarget(target!.id, body());
      toastOk(`${saved.name} saved`);
      onSaved();
    } catch (e) { toastErr(e); }
    finally { setBusy(false); }
  };

  const genKey = async () => {
    try {
      const k = await api.generateSshKey();
      setSshKey(k.privateKey);
      setPubKey(k.publicKey);
    } catch (e) { toastErr(e); }
  };

  return (
    <Modal opened onClose={onClose} size="lg" title={isNew ? "Add a deploy target" : `Edit ${target!.name}`}>
      <MStack gap="sm">
        {isNew && (
          <Select label="Kind" data={kinds.filter(k => k.kind !== "local").map(k => ({ value: k.kind, label: k.label }))}
            value={kind} onChange={v => setKind(v ?? "ssh")} allowDeselect={false} />
        )}
        {KIND_HINT[kind] && <Text size="xs" c="dimmed">{KIND_HINT[kind]}</Text>}
        {CLI_NOTE[kind] && (
          <Alert color="yellow" variant="light" icon={<IconAlertCircle size={15} />} p="xs">
            <Text size="xs">
              This target is driven with the <Code>{CLI_NOTE[kind]}</Code> CLI, which is not bundled with
              AspireUI. Install it in the container (or run AspireUI where it already is) and sign in — the
              connection test tells you if it cannot be found.
            </Text>
          </Alert>
        )}

        <TextInput label="Name" placeholder="Hetzner prod" value={name} onChange={e => setName(e.currentTarget.value)} withAsterisk />

        {kind === "ssh" && (
          <>
            <Group grow>
              <TextInput label="Host" placeholder="10.0.0.5 or box.example.com" value={sshHost} onChange={e => setSshHost(e.currentTarget.value)} withAsterisk />
              <NumberInput label="Port" value={sshPort} onChange={v => setSshPort(Number(v) || 22)} w={110} />
              <TextInput label="User" value={sshUser} onChange={e => setSshUser(e.currentTarget.value)} withAsterisk />
            </Group>
            <Textarea label="Private key" minRows={3} autosize maxRows={6}
              placeholder={target?.ssh?.hasKey ? "a key is stored — paste a new one to replace it" : "-----BEGIN OPENSSH PRIVATE KEY-----"}
              value={sshKey} onChange={e => setSshKey(e.currentTarget.value)}
              description="Stored encrypted. You can also point at a file or an environment variable with file:/path or env:NAME." />
            <Group gap="xs">
              <Button size="xs" variant="default" leftSection={<IconKey size={14} />} onClick={genKey}>Generate a key pair</Button>
              {pubKey && (
                <CopyButton value={pubKey}>
                  {({ copied, copy }) => (
                    <Button size="xs" variant="light" leftSection={copied ? <IconCheck size={14} /> : <IconCopy size={14} />} onClick={copy}>
                      {copied ? "Public key copied" : "Copy the public key"}
                    </Button>
                  )}
                </CopyButton>
              )}
            </Group>
            {pubKey && (
              <Alert variant="light" p="xs">
                <Text size="xs">Add this line to <Code>~/.ssh/authorized_keys</Code> of <Code>{sshUser}@{sshHost || "the box"}</Code>, then test the connection.</Text>
                <Code block style={{ wordBreak: "break-all", fontSize: 11 }}>{pubKey}</Code>
              </Alert>
            )}
          </>
        )}

        {kind === "dockerTcp" && (
          <>
            <TextInput label="Docker host" placeholder="tcp://10.0.0.9:2376" value={dockerHost} onChange={e => setDockerHost(e.currentTarget.value)} withAsterisk />
            <Textarea label="CA certificate (ca.pem)" autosize minRows={2} maxRows={4} value={ca} onChange={e => setCa(e.currentTarget.value)}
              placeholder={target?.tls?.hasCa ? "stored — paste to replace" : "-----BEGIN CERTIFICATE-----"} />
            <Textarea label="Client certificate (cert.pem)" autosize minRows={2} maxRows={4} value={cert} onChange={e => setCert(e.currentTarget.value)}
              placeholder={target?.tls?.hasCert ? "stored — paste to replace" : "-----BEGIN CERTIFICATE-----"} />
            <Textarea label="Client key (key.pem)" autosize minRows={2} maxRows={4} value={key} onChange={e => setKey(e.currentTarget.value)}
              placeholder={target?.tls?.hasKey ? "stored — paste to replace" : "-----BEGIN PRIVATE KEY-----"} />
          </>
        )}

        {kind === "k8s" && (
          <>
            <Group grow>
              <TextInput label="Context" placeholder="leave empty for the current context" value={kubeContext} onChange={e => setKubeContext(e.currentTarget.value)} />
              <TextInput label="Namespace" placeholder="default" value={kubeNs} onChange={e => setKubeNs(e.currentTarget.value)} />
            </Group>
            <Textarea label="Kubeconfig" autosize minRows={3} maxRows={8} value={kubeconfig} onChange={e => setKubeconfig(e.currentTarget.value)}
              placeholder={target?.kube?.hasKubeconfig ? "stored — paste to replace" : "apiVersion: v1\nclusters: …"}
              description="Optional: without one, the kubeconfig of the account AspireUI runs as is used." />
          </>
        )}

        {kind === "aca" && (
          <>
            <Group grow>
              <TextInput label="Resource group" value={resourceGroup} onChange={e => setResourceGroup(e.currentTarget.value)} withAsterisk />
              <TextInput label="Location" placeholder="westeurope" value={location} onChange={e => setLocation(e.currentTarget.value)} />
            </Group>
            <Group grow>
              <TextInput label="Container apps environment" placeholder="aspireui" value={environment} onChange={e => setEnvironment(e.currentTarget.value)} />
              <TextInput label="Subscription id" value={subscription} onChange={e => setSubscription(e.currentTarget.value)} />
            </Group>
            <PasswordInput label="Service principal" placeholder="tenant:appId:secret — or empty to use `az login`"
              value={credentials} onChange={e => setCredentials(e.currentTarget.value)} />
          </>
        )}

        {kind === "cloudrun" && (
          <>
            <Group grow>
              <TextInput label="Project" value={project} onChange={e => setProject(e.currentTarget.value)} withAsterisk />
              <TextInput label="Region" placeholder="europe-west3" value={location} onChange={e => setLocation(e.currentTarget.value)} />
            </Group>
            <Textarea label="Service account key (JSON)" autosize minRows={2} maxRows={6} value={credentials}
              onChange={e => setCredentials(e.currentTarget.value)}
              placeholder={target?.cloud?.hasCredentials ? "stored — paste to replace" : "empty to use `gcloud auth login`"} />
          </>
        )}

        {kind === "ecs" && (
          <>
            <Group grow>
              <TextInput label="Cluster" value={cluster} onChange={e => setCluster(e.currentTarget.value)} withAsterisk />
              <TextInput label="Region" placeholder="eu-central-1" value={location} onChange={e => setLocation(e.currentTarget.value)} />
            </Group>
            <TextInput label="Subnets" placeholder="subnet-aaa,subnet-bbb" value={subnets} onChange={e => setSubnets(e.currentTarget.value)} withAsterisk />
            <Group grow>
              <TextInput label="Security groups" placeholder="sg-aaa" value={securityGroups} onChange={e => setSecurityGroups(e.currentTarget.value)} />
              <TextInput label="Execution role" placeholder="arn:aws:iam::…:role/ecsTaskExecutionRole" value={execRole} onChange={e => setExecRole(e.currentTarget.value)} withAsterisk />
            </Group>
            <PasswordInput label="Access key" placeholder="accessKeyId:secretAccessKey — or empty to use the CLI's own profile"
              value={credentials} onChange={e => setCredentials(e.currentTarget.value)} />
          </>
        )}

        <Divider my={4} label="How apps on this target are reached" labelPosition="left" />
        <TextInput label="Public address" placeholder={kind === "ssh" ? "defaults to the ssh host" : "hostname or IP for app URLs"}
          value={publicHost} onChange={e => setPublicHost(e.currentTarget.value)}
          description="The host name app URLs are built from — set it when the box is reached under a different name than the one above." />
        {compose && (
          <Group grow>
            <NumberInput label="Host ports from" value={portFrom} onChange={v => setPortFrom(Number(v) || 20000)} />
            <NumberInput label="to" value={portTo} onChange={v => setPortTo(Number(v) || 29999)} />
          </Group>
        )}

        <Divider my={4} label="Domains" labelPosition="left" />
        <SegmentedControl fullWidth size="xs" value={domainKind} onChange={setDomainKind}
          data={[
            { value: "none", label: "None" },
            { value: "npm", label: "Nginx Proxy Manager" },
            ...(kind === "aca" ? [{ value: "azure", label: "Azure custom domain" }] : []),
            { value: "manual", label: "Manual DNS" },
          ]} />
        {domainKind === "npm" && (
          <>
            <Group grow>
              <TextInput label="NPM URL" placeholder="http://npm.lan:81" value={npmUrl} onChange={e => setNpmUrl(e.currentTarget.value)} />
              <TextInput label="Account email" value={npmEmail} onChange={e => setNpmEmail(e.currentTarget.value)} />
            </Group>
            <Group grow>
              <PasswordInput label="Password" placeholder={target?.domains.npm?.hasPassword ? "stored — type to replace" : ""}
                value={npmPassword} onChange={e => setNpmPassword(e.currentTarget.value)} />
              <TextInput label="Forward host" placeholder="defaults to this target's address"
                value={npmForward} onChange={e => setNpmForward(e.currentTarget.value)} />
            </Group>
          </>
        )}
        {domainKind === "manual" && (
          <Text size="xs" c="dimmed">Nothing is configured for you: the app's page shows which address and port to point a DNS record at.</Text>
        )}

        <Divider my={4} label="Registry (optional)" labelPosition="left" />
        <Text size="xs" c="dimmed">
          Needed when a stack builds its own image: it is pushed here first, because a remote daemon or a
          managed platform cannot pull from your machine.
        </Text>
        <Group grow>
          <TextInput label="Registry" placeholder="ghcr.io/acme" value={registryUrl} onChange={e => setRegistryUrl(e.currentTarget.value)} />
          <TextInput label="User" value={registryUser} onChange={e => setRegistryUser(e.currentTarget.value)} />
          <PasswordInput label="Password / token" placeholder={target?.registry?.hasPassword ? "stored" : ""}
            value={registryPassword} onChange={e => setRegistryPassword(e.currentTarget.value)} />
        </Group>

        <TextInput label="Note" placeholder="what this target is for" value={notes} onChange={e => setNotes(e.currentTarget.value)} />

        {probe && (
          <Alert color={probe.ok ? "green" : "red"} variant="light" icon={probe.ok ? <IconCheck size={16} /> : <IconAlertCircle size={16} />}>
            <Text size="sm">
              {probe.ok
                ? `Reachable${probe.version ? ` — ${probe.version}` : ""}${probe.arch ? ` (${probe.arch})` : ""}${probe.compose ? `, ${probe.compose}` : ""}`
                : probe.error}
            </Text>
          </Alert>
        )}

        <Group justify="space-between">
          <Button variant="default" onClick={test} loading={busy} leftSection={<IconRefresh size={15} />}>Test connection</Button>
          <Group gap="xs">
            <Button variant="subtle" onClick={onClose}>Cancel</Button>
            <Button onClick={save} loading={busy} disabled={!name.trim()}>{isNew ? "Add target" : "Save"}</Button>
          </Group>
        </Group>
      </MStack>
    </Modal>
  );
}

// Creates the machine as well: a server at the provider, docker on it, and the target that points at it.
function ProvisionModal({ onClose, onDone }: { onClose: () => void; onDone: () => void }) {
  const [providers, setProviders] = useState<api.ProvisionProvider[]>([]);
  const [kind, setKind] = useState("hetzner");
  const [name, setName] = useState("");
  const [credentials, setCredentials] = useState("");
  const [region, setRegion] = useState("");
  const [size, setSize] = useState("");
  const [resourceGroup, setResourceGroup] = useState("");
  const [project, setProject] = useState("");
  const [makeDefault, setMakeDefault] = useState(false);
  const [busy, setBusy] = useState(false);
  const [log, setLog] = useState("");

  useEffect(() => { api.provisionProviders().then(setProviders).catch(() => setProviders([])); }, []);
  const p = useMemo(() => providers.find(x => x.kind === kind), [providers, kind]);
  useEffect(() => { if (p) { setRegion(p.defaultRegion); setSize(p.defaultSize); } }, [p]);

  const create = async () => {
    setBusy(true); setLog("");
    try {
      const r = await api.provisionMachine({
        provider: kind, name, credentials, region, size,
        resourceGroup: resourceGroup || undefined, project: project || undefined,
        zone: kind === "gcp" ? region : undefined, makeDefault,
      });
      setLog(r.log);
      if (r.ok) { toastOk(`${name} is up at ${r.host}`); onDone(); }
      else toastErr("the machine could not be prepared — see the log");
    } catch (e) { toastErr(e); }
    finally { setBusy(false); }
  };

  return (
    <Modal opened onClose={onClose} size="lg" title="Create a machine and use it as a target">
      <MStack gap="sm">
        <Select label="Provider" data={providers.map(x => ({ value: x.kind, label: x.label }))}
          value={kind} onChange={v => setKind(v ?? "hetzner")} allowDeselect={false} />
        {p && <Text size="xs" c="dimmed">Credentials: {p.docs}</Text>}
        <TextInput label="Name" placeholder="hetzner-prod" value={name} onChange={e => setName(e.currentTarget.value)} withAsterisk />
        {p?.auth === "token"
          ? <PasswordInput label="API token" value={credentials} onChange={e => setCredentials(e.currentTarget.value)} withAsterisk />
          : <PasswordInput label="Credentials" placeholder="empty to use the CLI's own login"
              value={credentials} onChange={e => setCredentials(e.currentTarget.value)} />}
        <Group grow>
          <Select label={kind === "gcp" ? "Zone" : "Region"} data={p?.regions ?? []} value={region} onChange={v => setRegion(v ?? "")} searchable />
          <Select label="Size" data={p?.sizes ?? []} value={size} onChange={v => setSize(v ?? "")} searchable />
        </Group>
        {kind === "azure" && <TextInput label="Resource group" placeholder="created if missing" value={resourceGroup} onChange={e => setResourceGroup(e.currentTarget.value)} />}
        {kind === "gcp" && <TextInput label="Project" value={project} onChange={e => setProject(e.currentTarget.value)} withAsterisk />}
        <Switch label="Make this the default target" checked={makeDefault} onChange={e => setMakeDefault(e.currentTarget.checked)} />
        <Alert variant="light" color="blue" p="xs">
          <Text size="xs">
            A key pair is generated here, the machine is created with it, docker is installed through
            cloud-init, and the target is added once docker answers. This takes a few minutes — and it
            creates something that costs money at the provider.
          </Text>
        </Alert>
        {log && <Code block style={{ maxHeight: 240, overflow: "auto", fontSize: 11 }}>{log}</Code>}
        <Group justify="flex-end" gap="xs">
          <Button variant="subtle" onClick={onClose}>Close</Button>
          <Button onClick={create} loading={busy} disabled={!name.trim() || (p?.auth === "token" && !credentials.trim())}>
            Create machine
          </Button>
        </Group>
      </MStack>
    </Modal>
  );
}

// Picks a target; used by the deploy button, the store install and the move dialog.
export function TargetPicker({ value, onChange, targets, label = "Deploy to", disabled, description }:
  {
    value: string | null; onChange: (id: string | null) => void; targets: DeployTarget[];
    label?: string; disabled?: boolean; description?: string;
  }) {
  return (
    <Select label={label} description={description} disabled={disabled} allowDeselect={false}
      data={targets.map(t => ({
        value: t.id,
        label: `${t.name}${t.default ? " (default)" : ""}${t.probe && !t.probe.ok ? " — unreachable" : ""}`,
      }))}
      value={value} onChange={onChange} />
  );
}

// Move an app to another target, or copy it there as a second instance.
export function MoveAppModal({ stackId, name, current, onClose, onDone }:
  { stackId: string; name: string; current?: string | null; onClose: () => void; onDone: () => void }) {
  const [targets, setTargets] = useState<DeployTarget[]>([]);
  const [to, setTo] = useState<string | null>(null);
  const [mode, setMode] = useState("move");
  const [withData, setWithData] = useState(true);
  const [busy, setBusy] = useState(false);
  const [log, setLog] = useState("");

  useEffect(() => { api.listTargets().then(list => setTargets(list.filter(t => t.id !== (current ?? "local")))).catch(() => setTargets([])); }, [current]);
  useEffect(() => { if (mode === "copy") setWithData(false); else setWithData(true); }, [mode]);

  const run = async () => {
    if (!to) return;
    setBusy(true); setLog("");
    try {
      if (mode === "move") {
        const r = await api.moveHosting(stackId, to, withData);
        setLog(r.log);
        toastOk(`${name} now runs on ${targets.find(t => t.id === to)?.name}`);
        onDone();
      } else {
        const r = await api.copyHosting(stackId, to, withData);
        setLog(r.log);
        toastOk(`${name} copied to ${targets.find(t => t.id === to)?.name}`);
        onDone();
      }
    } catch (e) { toastErr(e); }
    finally { setBusy(false); }
  };

  const target = targets.find(t => t.id === to);
  const crossKind = target && !target.compose;

  return (
    <Modal opened onClose={onClose} size="lg" title={`${mode === "move" ? "Move" : "Copy"} ${name}`}>
      <MStack gap="sm">
        <SegmentedControl fullWidth value={mode} onChange={setMode} data={[
          { value: "move", label: "Move it" },
          { value: "copy", label: "Copy it" },
        ]} />
        <Text size="xs" c="dimmed">
          {mode === "move"
            ? "The app is stopped, its data is streamed to the new target, and it is removed from the old one. If the new deployment fails, it is started again where it was."
            : "A second, independent instance with its own stack and its own data — the original keeps running."}
        </Text>
        <TargetPicker label="Target" value={to} onChange={setTo} targets={targets} />
        <Switch label="Take the data along" checked={withData} onChange={e => setWithData(e.currentTarget.checked)}
          description={mode === "move"
            ? "Volumes are streamed directly between the two machines while the app is stopped."
            : "Copies the current data into the new instance — it starts as a clone instead of empty."} />
        {crossKind && (
          <Alert color="yellow" variant="light" icon={<IconAlertCircle size={15} />} p="xs">
            <Text size="xs">
              {target?.name} has no docker volumes, so data cannot be carried over — the app starts empty
              there, and volumes it declares will not survive a restart.
            </Text>
          </Alert>
        )}
        {log && <Code block style={{ maxHeight: 220, overflow: "auto", fontSize: 11 }}>{log}</Code>}
        <Group justify="flex-end" gap="xs">
          <Button variant="subtle" onClick={onClose}>Close</Button>
          <Button onClick={run} loading={busy} disabled={!to}>{mode === "move" ? "Move" : "Copy"}</Button>
        </Group>
      </MStack>
    </Modal>
  );
}
