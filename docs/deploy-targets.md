# Deploy targets

A **deploy target** is a place AspireUI can put an app: the machine it runs on, a box over ssh, a
Kubernetes cluster, a managed container platform. Everything that deploys — the builder's *Deploy*
button, the app store, clone hooks — asks which target, and every app in Hosting shows the one it runs
on.

*Settings → Hosting → Deploy targets* is the list. **This machine** is always there, cannot be removed,
and owns every app that existed before targets did.

## What a target carries

| | |
|---|---|
| **Address** | how to reach it: ssh host, TCP daemon, kube context, cloud resource group |
| **Public address** | the host name app URLs are built from — set it when the box answers under another name |
| **Port range** | host ports for published apps, per target (default 20000–29999) |
| **Domains** | its own provider: a Nginx Proxy Manager instance, an Azure custom domain, manual DNS, or none |
| **Registry** | where locally built images are pushed, so a remote daemon or a cloud platform can pull them |
| **Credentials** | ssh key, TLS certificates, kubeconfig, cloud credentials — [stored encrypted](#credentials) |
| **Status** | what the last connection test found: docker version, architecture, free disk |

## The kinds

### This machine (`local`)

The docker daemon AspireUI itself talks to. Full feature set, nothing to configure.

### Docker over SSH (`ssh`)

Any VM, NAS or bare-metal box with docker installed. AspireUI writes a private ssh config for the
target and points `DOCKER_HOST=ssh://…` at it, with connection multiplexing so a status poll costs
almost nothing.

**Key authentication only.** Docker's ssh transport shells out to `ssh`, and a password prompt has
nowhere to go. The wizard generates an ed25519 pair for you and shows the public half to paste into the
box's `~/.ssh/authorized_keys`; you can also paste your own key, or point at one that already exists
with `file:/path/to/key` or `env:SOME_VARIABLE`.

The host key is pinned on first contact (`accept-new`), so a later change fails loudly instead of
silently trusting a new machine.

Everything works here: host ports, volume browser, container terminal, backups, restore, domains.

### Docker over TCP (`dockerTcp`)

A daemon listening on TCP with mutual TLS — paste `ca.pem`, `cert.pem` and `key.pem`. Same feature set
as ssh. Use it for a Synology/QNAP NAS or a daemon behind a tunnel.

### Kubernetes (`k8s`)

A cluster reached with `kubectl` and `helm`, both bundled with AspireUI. A stack is published as the
Helm chart `aspire publish` produces and installed as one release per app:

```
helm upgrade --install aspireui-<stack> <chart> --namespace <ns> --create-namespace --wait
```

Status comes from the release's pods (a `CrashLoopBackOff` or `ImagePullBackOff` shows as failing, not
as green), URLs from its Ingress hosts and LoadBalancer services, logs from `kubectl logs`, the
terminal from `kubectl exec`. Stop scales to zero, start scales back, undeploy runs `helm uninstall`.

No host ports and no volume browser — a cluster's storage belongs to the cluster.

### Azure Container Apps (`aca`), Google Cloud Run (`cloudrun`), AWS ECS (`ecs`)

Managed platforms, driven through their own CLI (`az`, `gcloud`, `aws`). The compose file of the stack
is the source of truth: one container app / Cloud Run service / ECS service per compose service, with
its image and environment.

**The CLIs are not bundled** — they are hundreds of megabytes each. Install the one you need in the
container (or run AspireUI where it already is) and sign in; the connection test says plainly when it
cannot find the tool or the login.

What these platforms cannot do, and the UI says so before you deploy:

- **No volumes.** There is no local disk that survives a revision. An app that needs one belongs on a
  compose target, or gets a managed database wired in by hand.
- **Images must be pullable by the platform.** A stack that builds from a Dockerfile needs a registry on
  the target; its image is pushed there before deploying.
- **Service-to-service names** keep working on Container Apps (apps in one environment resolve each
  other by name). On Cloud Run and ECS they do not: a multi-service stack needs its URLs wired by hand.

Every CLI call lands in the deploy log, so a failure can be replayed by hand.

## Adding a machine that does not exist yet

*Create a machine* provisions one and registers it as an ssh target in one go:

| Provider | Auth |
|---|---|
| Hetzner Cloud | API token |
| DigitalOcean | API token |
| Akamai Linode | API token |
| Azure VM | `az login`, or `tenant:appId:secret` |
| AWS EC2 | `aws configure`, or `accessKeyId:secretAccessKey` |
| Google Compute Engine | `gcloud auth login`, or a service-account key |

The flow: generate a key pair → create the server with that key and a cloud-init that installs docker →
wait for the daemon to answer → add the target and test it. It takes a few minutes, and it creates
something that costs money at the provider. A machine created this way can also be destroyed from the
target's row — that deletes the server, not just the entry.

## Deploying to a target

- **Builder** → *Deploy* shows the targets as tiles; the default one is preselected. Re-deploying an app
  keeps it where it is.
- **App store** → the *Install to…* picker takes several targets at once. Each one gets its own
  independent instance (its own stack, data and domain), named `<app> @ <target>`.
- **Clone hooks** → a hook can name the target its clones are deployed to, so one app can have a hook
  per environment.

## Moving and copying

An app's menu has *Move or copy to another target*:

- **Move** stops the app, streams each volume straight from one daemon to the other (nothing is buffered
  in AspireUI, so a 40 GB volume is fine), deploys it on the new target and removes it from the old one.
  If the new deployment fails, the app is started again where it was — a failed move never loses an app.
- **Copy** creates a second, independent instance on the other target; the original keeps running. Data
  is copied only if you ask for it.

Two targets that turn out to be the same docker daemon (say `local` and an ssh alias for the same box)
are detected by the daemon's own id, and the move is refused instead of tearing the app down.

Data cannot travel to or from an orchestrator target — the dialog says so before you start.

## Domains

Domains are configured **per target**, because the reverse proxy usually lives next to the machine:

- **Nginx Proxy Manager** — full support: list, create, Let's Encrypt certificate, enable/disable,
  delete. This is what "this machine" has always used; the old global NPM settings became its
  configuration.
- **Azure custom domain** — binds a hostname on the target's Container Apps environment (CNAME
  validation, managed certificate).
- **Manual DNS** — nothing is configured for you; the app's domain dialog shows which address and port
  to point a record at.

The proxy of a remote target forwards to that target's own address, never to `localhost`.

## Credentials

Every secret a target needs is stored one of two ways:

- **Encrypted in the database** (AES-GCM). The key comes from `ASPIREUI_SECRET_KEY` (base64, 32 bytes,
  or any passphrase) when it is set — use that for a container deploy, so the database alone is
  worthless — otherwise from a key file created next to the workspace on first use, readable by the
  owner only.
- **As a reference**, when you would rather not store it at all: `env:NAME` reads an environment
  variable, `file:/path` reads a file. Nothing secret ever enters the database.

Secrets never leave the server: the API only ever says *whether* one is set.

Reading the target list needs any signed-in account (the deploy pickers need it); creating, editing,
deleting, provisioning and destroying is admin-only.

## Ports, URLs and health

Host ports are allocated per target: what other apps on that target use, plus whatever else its daemon
already publishes. A port an app already had is kept, so URLs stay stable across re-deploys — including
across a move.

URLs for a remote target are built from the target's public address, never from the address you happen
to be browsing AspireUI at. Health is the same verdict everywhere: a container that crash-loops, exits
or reports unhealthy makes the app broken, whether it is a compose service or a Kubernetes pod.
