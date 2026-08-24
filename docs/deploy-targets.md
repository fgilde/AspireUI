# Deploy targets

A **deploy target** is a place AspireUI can put an app: the machine it runs on, a box over ssh, a
Kubernetes cluster, a managed container platform. Everything that deploys — the builder's *Deploy*
button, the app store, clone hooks — asks which target, and every app in Hosting shows the one it runs
on.

*Settings → Hosting → Deploy targets* is the list. **This machine** is always there, cannot be removed,
and owns every app that existed before targets did.

## Add target vs Create a machine

The two buttons above the list do different halves of the same job:

| | **Add target** | **Create a machine** |
|---|---|---|
| The machine… | already exists | does not exist yet |
| What you supply | address + credentials (ssh host & key, TCP daemon & certificates, kubeconfig, cloud resource group) | a provider API token (or CLI login), a size and a region |
| What AspireUI does | connects and tests — nothing is created | creates an ssh key pair, orders the server, installs docker with cloud-init, waits for the daemon, **then adds an ssh target for it** |
| Costs money | no | yes — at the provider, until you destroy it |
| Available for | every kind: ssh, docker TCP, Kubernetes, Container Apps, Cloud Run, ECS | virtual machines only: Hetzner, DigitalOcean, Linode, Azure VM, AWS EC2, Google Compute Engine |
| Afterwards | a normal target | a normal ssh target, plus a *destroy machine* action in its row |

So: *Add target* registers something, *Create a machine* buys it and then registers it. Kubernetes and
the managed platforms are **always** *Add target* — AspireUI does not create clusters.

## Which kind do I want

| You have | Kind | Notes |
|---|---|---|
| Only this box | `local` | already there, nothing to set up |
| A VPS, a NAS with docker, a Proxmox VM, a Raspberry Pi | **Docker over SSH** | the workhorse. Full feature set |
| A daemon published on TCP with certificates | **Docker over TCP** | same features as ssh |
| A cluster (AKS, EKS, GKE, k3s, Talos …) | **Kubernetes** | deploys the Helm chart `aspire publish` produces |
| Azure, serverless, no VM to babysit | **Azure Container Apps** | one container app per compose service |
| Google, serverless | **Google Cloud Run** | best for a single stateless service |
| AWS, serverless | **AWS ECS (Fargate)** | needs cluster, subnets and a role up front |

### What each kind can do

| | ssh / TCP / local | Kubernetes | Container Apps | Cloud Run | ECS |
|---|---|---|---|---|---|
| Host ports & URLs | ✅ own port range | NodePort / LoadBalancer / Ingress | platform FQDN | platform URL | task public IP |
| Persistent volumes | ✅ docker volumes | ✅ claims on a storage class | ❌ | ❌ | ❌ |
| Volume file browser | ✅ | ❌ | ❌ | ❌ | ❌ |
| Backups / restore | ✅ | ❌ | ❌ | ❌ | ❌ |
| Terminal | ✅ `docker exec` | ✅ `kubectl exec` | ✅ `az containerapp exec` | ❌ | ⚠️ needs ECS Exec enabled |
| Logs | ✅ | ✅ | ✅ | ✅ | ✅ |
| Start / stop | ✅ | scale 0 ⇄ 1 | min-replicas 0 ⇄ 1 | min-instances | desired-count |
| Move/copy **with data** | ✅ between two of them | ❌ | ❌ | ❌ | ❌ |
| Multi-service stack | ✅ compose network | ✅ chart | ✅ names rewired for you | ⚠️ no name DNS | ⚠️ no name DNS |
| Bundled tooling | docker CLI, compose, ssh | **kubectl + helm** | `az` **not** bundled | `gcloud` **not** bundled | `aws` **not** bundled |

---

# Add target, kind by kind

## Docker over SSH

**On the box:** docker installed, an ssh account (root or a user in the `docker` group), and the app
port range reachable from wherever you open the URLs.

**In AspireUI:** *Add target* → *Docker over SSH*.

1. **Name** — free text, e.g. `hetzner-prod`. The id (`hetzner-prod`) is derived from it.
2. **Host / Port / User** — `10.0.0.5`, `22`, `root`.
3. **Private key** — three ways:
   - *Generate a key pair* → AspireUI shows the **public** half. Append that one line to
     `~/.ssh/authorized_keys` of that user on the box:
     ```bash
     ssh root@10.0.0.5 'mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys' <<< 'ssh-ed25519 AAAA… aspireui'
     ```
   - paste an existing private key, or
   - point at one without storing it: `file:/root/.ssh/id_ed25519` or `env:DEPLOY_KEY`.

   Password authentication does not exist here on purpose: docker's ssh transport shells out to `ssh`,
   and a password prompt has nowhere to go.
4. **Public address** — only needed when the box answers under a different name than the ssh host
   (a DNS name, a NAT address). App URLs are built from this.
5. **Host ports from/to** — the range published apps get. Keep the ranges of two targets apart if the
   same reverse proxy serves both; otherwise the default 20000–29999 is fine.
6. **Domains** — see [Domains](#domains-per-target).
7. **Test connection** → expect `reachable — 27.x (amd64), 2.x`. Then **Add target**.

The host key is pinned on first contact (`accept-new`), so a later change fails loudly instead of
trusting a new machine silently. Connections are multiplexed (`ControlPersist`), so status polling on a
remote box is cheap.

**Firewall:** ssh (22) plus the port range. Example for a plain Ubuntu box with ufw:
```bash
ufw allow 22/tcp && ufw allow 20000:29999/tcp && ufw enable
```
If you only ever reach the apps through a reverse proxy on the same box, open 80/443 instead and leave
the range closed.

**Docker missing on the box?** The target's row has *install docker over ssh* (it runs
`curl -fsSL https://get.docker.com | sh` there).

## Docker over TCP (mTLS)

For a daemon that already listens on TCP with certificates (Synology/QNAP, a daemon behind a tunnel).

1. **Docker host** — `tcp://10.0.0.9:2376`.
2. **CA / client certificate / client key** — the three PEM files the daemon was set up with
   (`ca.pem`, `cert.pem`, `key.pem`). They are stored encrypted and written to disk only while a
   command runs.
3. Test → same output as ssh.

Plain, unencrypted `tcp://…:2375` also works if you point at it, but then anyone who reaches that port
owns the machine. Do not do that across a network you do not control.

## Kubernetes

kubectl and helm ship inside the AspireUI image, so only cluster access is needed.

1. **Kubeconfig** — paste one, or leave it empty to use the kubeconfig of the account AspireUI runs as
   (`/root/.kube/config` in the container, e.g. mounted read-only).
2. **Context** — empty means "the current context of that kubeconfig".
3. **Namespace** — created on deploy if missing (`--create-namespace`).

4. **How apps are reached** — the chart Aspire generates has **ClusterIP services only, no Ingress**,
   so this setting decides what AspireUI adds after the install:

   | Mode | What happens | Reachable at |
   |---|---|---|
   | `ClusterIP` | nothing — internal only | inside the cluster |
   | `NodePort` | every app service is patched to NodePort | `http://<node or public address>:<nodePort>` |
   | `LoadBalancer` | patched to LoadBalancer, the cloud assigns an address | `http://<assigned address>` |
   | `Ingress` | one Ingress per app service, with your class | `http://<host from the pattern>` |

   For `Ingress`, set an **ingress class** (`nginx`, `traefik`, …) and a **host pattern** —
   `{service}.apps.example.com` becomes `web.apps.example.com`, `{app}` is the stack's name. Point the
   DNS records at your ingress controller yourself (or use the target's domain provider).
   Our dashboard sidecar is never published.

5. **Storage class** — the generated chart puts every volume in an `emptyDir`, which a pod restart
   empties. Name a storage class (`local-path`, `longhorn`, `gp3`, …) and AspireUI rewrites those into
   `PersistentVolumeClaim`s of that class before installing, one claim per volume, sized by *claim size*
   (default `8Gi`). Leave it empty to keep volumes ephemeral on purpose.

What a deploy runs:
```bash
helm upgrade --install aspireui-<stack> <chart-from-aspire-publish> \
  --namespace <ns> --create-namespace --wait --timeout 5m
# then, depending on the target: kubectl patch svc … / kubectl apply -f <generated ingress>
```
Status comes from the release's pods (`CrashLoopBackOff`, `ImagePullBackOff` and not-ready pods show as
failing/starting, never as green), URLs from Ingress hosts, LoadBalancer addresses or NodePorts. Stop
scales to zero, start scales back, undeploy runs `helm uninstall` **and** removes the Ingress objects
AspireUI created (helm does not own those).

**What is not there:** no volume file browser and no backup/restore for cluster storage, so an app
cannot be moved between a docker target and a cluster with its data.

---

# Azure

Two completely different things carry the name "Azure" here. Pick the one you want:

- **Azure VM** → *Create a machine* → provider `azure`. You get an ordinary Linux box with docker, and
  from then on it behaves exactly like any other ssh target: volumes, backups, terminal, move-with-data.
  **This is the path with the full feature set.**
- **Azure Container Apps** → *Add target* → kind `aca`. Serverless: no VM, no disk, no volumes.

## Azure VM (Create a machine)

**1. Install the az CLI where AspireUI runs.** It is not bundled (a few hundred MB). Either run AspireUI
on a host that has `az`, or build a derived image:

```dockerfile
FROM ghcr.io/fgilde/aspireui:latest
USER root
# Debian-based image: the official installer script, with pip as the fallback on very new Debian
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates \
 && (curl -sL https://aka.ms/InstallAzureCLIDeb | bash \
     || (apt-get install -y --no-install-recommends python3-pip && pip3 install --break-system-packages azure-cli)) \
 && rm -rf /var/lib/apt/lists/* \
 && az version
```

**2. Give it credentials.** Two options:

- *Interactive, kept in a volume* — exec into the container once:
  ```bash
  docker exec -it aspireui az login          # device code, follow the URL
  ```
  Mount `/root/.azure` as a volume so the login survives a restart.
- *Service principal* (recommended for a server, no interactive step):
  ```bash
  az account set --subscription <SUBSCRIPTION_ID>
  az group create -n aspireui-rg -l westeurope          # the scope must exist first
  az ad sp create-for-rbac --name aspireui \
    --role Contributor \
    --scopes /subscriptions/<SUBSCRIPTION_ID>/resourceGroups/aspireui-rg
  ```
  The output has `tenant`, `appId`, `password`. Paste them into AspireUI as one string:
  ```
  <tenant>:<appId>:<password>
  ```
  AspireUI runs `az login --service-principal` with it before every operation. Scope to the
  subscription instead of a resource group if you want AspireUI to create resource groups itself.

**3. Create a machine** → provider *Azure VM*:

| Field | Meaning |
|---|---|
| Name | target name, also the VM name (slugified) |
| Credentials | the `tenant:appId:secret` string, or empty to use `az login` |
| Region | `westeurope`, `germanywestcentral`, … |
| Size | `Standard_B2s` is a sane small default |
| Resource group | created if missing; defaults to `aspireui-<name>` |

What runs, in order:
```bash
az group create -n <rg> -l <region>
az vm create -g <rg> -n <name> --image Ubuntu2404 --size <size> \
  --admin-username azureuser --ssh-key-values <generated.pub> \
  --custom-data <cloud-init that installs docker> --public-ip-sku Standard
```
then AspireUI polls `docker version` over ssh until the cloud-init is done (up to 5 minutes), installs
docker over ssh if cloud-init did not, and finally probes the target.

**4. Open the app ports.** `az vm create` opens 22 only. The published apps need their range:
```bash
az vm open-port -g <rg> -n <name> --port 20000-29999 --priority 1010
```
Skip this if the apps are only reached through a reverse proxy on the VM itself (then open 80/443).

**5. Destroying it.** The target row has *destroy machine*: `az vm delete --yes` for the VM plus the
target entry. The resource group, disk, NIC and public IP that `az vm create` made are **not** all
removed by that call — check the group afterwards, or delete the whole group when you are done:
```bash
az group delete -n <rg> --yes
```

## Azure Container Apps (Add target)

Serverless containers. No VM, no volumes, and a different mental model: each compose service becomes its
own container app inside one *environment*.

**1. Install `az`** — same derived image as above.

**2. Credentials** — same two options as above (`az login` in the container, or `tenant:appId:secret`).
The service principal needs *Contributor* on the resource group (or the subscription).

**3. Register what Container Apps needs**, once per subscription:
```bash
az extension add --name containerapp --upgrade      # AspireUI also does this itself
az provider register --namespace Microsoft.App
az provider register --namespace Microsoft.OperationalInsights
```

**4. Add target** → kind *Azure Container Apps*:

| Field | Required | Meaning |
|---|---|---|
| Resource group | ✅ | created if missing |
| Location | – | `westeurope` if empty |
| Container apps environment | – | `aspireui` if empty; created if missing |
| Subscription id | – | sets `AZURE_SUBSCRIPTION_ID` |
| Service principal | – | `tenant:appId:secret`, or empty for `az login` |
| Registry | – | only for images you build yourself, see limits |

**Test connection** here means: is `az` installed, does the login work, can we see the account. It
prints `az 2.x` when it does.

**5. Deploy.** For every compose service AspireUI runs:
```bash
az containerapp env show   -g <rg> -n <env>                  # created with `env create` if missing
az containerapp create     -g <rg> -n <stack>-<service> --environment <env> \
   --image <image> --target-port <port> --ingress external|internal \
   --env-vars KEY=VALUE …
az containerapp show       -g <rg> -n <stack>-<service> --query properties.configuration.ingress.fqdn
```
The first service that has a port gets **external** ingress and becomes the app's URL
(`https://<app>.<region>.azurecontainerapps.io`); the rest stay internal. A re-deploy uses
`az containerapp update`. Every command lands in the deploy log.

Start/stop maps to min-replicas 0/1, logs to `az containerapp logs show`, the terminal to
`az containerapp exec`, undeploy to `az containerapp delete`.

**Limits — read before you move a real app here:**

- **No volumes.** Anything a compose service declares as a volume is gone on the next revision. The
  deploy log says so per service. Databases belong in Azure Database / Cosmos, not in a container app.
- **Multi-service stacks are rewired for you.** Container apps in one environment reach each other by
  app name, and AspireUI names them `<stack>-<service>` (names must be unique per environment). Every
  environment variable of every service is rewritten accordingly, so a compose value like
  `Host=db;Port=5432` becomes `Host=myapp-db;Port=5432`. Only whole host tokens are replaced —
  `POSTGRES_DB=mydb` is left alone. A service whose port is not HTTP (5432, 6379, …) gets TCP ingress
  (`--transport tcp --exposed-port`), so a database inside the environment is actually reachable.
- **Private registries work** when the target has a registry with a user and a password: the credentials
  are passed at `containerapp create` and set with `az containerapp registry set` on later deploys. A
  registry without credentials only works if the image is public.
- **Custom domains:** the built-in *Azure custom domain* provider needs the container app's name
  recorded on the target, and the wizard has no field for it yet — so bind by hand:
  ```bash
  az containerapp hostname add  -g <rg> -n <app> --hostname app.example.com
  az containerapp hostname bind -g <rg> -n <app> --hostname app.example.com \
    --environment <env> --validation-method CNAME
  ```
  Point a CNAME at the app's FQDN and a TXT `asuid.<sub>` at the verification id Azure prints first.
  For anything simpler, choose *manual DNS* on the target: AspireUI then only tells you where to point.
- **Cost:** a Container Apps environment with a Log Analytics workspace costs money even while idle.

---

# Google Cloud Run (Add target)

**1. Install `gcloud`** in a derived image:
```dockerfile
FROM ghcr.io/fgilde/aspireui:latest
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl gnupg ca-certificates \
 && curl -fsSL https://packages.cloud.google.com/apt/doc/apt-key.gpg | gpg --dearmor -o /usr/share/keyrings/cloud.google.gpg \
 && echo "deb [signed-by=/usr/share/keyrings/cloud.google.gpg] https://packages.cloud.google.com/apt cloud-sdk main" > /etc/apt/sources.list.d/google-cloud-sdk.list \
 && apt-get update && apt-get install -y --no-install-recommends google-cloud-cli \
 && rm -rf /var/lib/apt/lists/* && gcloud version
```

**2. Credentials** — a service-account key JSON, pasted into the target (stored encrypted, written to a
temp file and handed to `gcloud` as `GOOGLE_APPLICATION_CREDENTIALS`):
```bash
gcloud iam service-accounts create aspireui --project <PROJECT>
gcloud projects add-iam-policy-binding <PROJECT> \
  --member serviceAccount:aspireui@<PROJECT>.iam.gserviceaccount.com --role roles/run.admin
gcloud projects add-iam-policy-binding <PROJECT> \
  --member serviceAccount:aspireui@<PROJECT>.iam.gserviceaccount.com --role roles/iam.serviceAccountUser
gcloud iam service-accounts keys create key.json \
  --iam-account aspireui@<PROJECT>.iam.gserviceaccount.com
```
Enable the API once: `gcloud services enable run.googleapis.com --project <PROJECT>`.

**3. Add target** → *Google Cloud Run*: project (required), region, the key JSON.

Deploy runs, per service:
```bash
gcloud run deploy <stack>-<service> --image <image> --region <region> --project <project> \
  --platform managed --allow-unauthenticated --port <port> --set-env-vars K=V,…
```
The URL comes from `status.url`.

**Limits:** no volumes; no service-to-service DNS by name (a multi-service stack needs the generated
URLs pasted into each other's environment); no shell into an instance; `--allow-unauthenticated` means
the service is public — remove it in the console if you want IAM in front.

# AWS ECS on Fargate (Add target)

ECS needs infrastructure to exist first. Create it once:

```bash
aws ecs create-cluster --cluster-name aspireui
# an execution role that may pull images and write logs
aws iam create-role --role-name ecsTaskExecutionRole \
  --assume-role-policy-document '{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"ecs-tasks.amazonaws.com"},"Action":"sts:AssumeRole"}]}'
aws iam attach-role-policy --role-name ecsTaskExecutionRole \
  --policy-arn arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy
# note the public subnets and a security group that allows your app ports inbound
aws ec2 describe-subnets --query 'Subnets[].{id:SubnetId,az:AvailabilityZone,public:MapPublicIpOnLaunch}'
```

**Install `aws`** in a derived image:
```dockerfile
FROM ghcr.io/fgilde/aspireui:latest
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl unzip ca-certificates \
 && curl -fsSL "https://awscli.amazonaws.com/awscli-exe-linux-$(uname -m).zip" -o /tmp/aws.zip \
 && unzip -q /tmp/aws.zip -d /tmp && /tmp/aws/install && rm -rf /tmp/aws /tmp/aws.zip \
 && rm -rf /var/lib/apt/lists/* && aws --version
```

**Add target** → *AWS ECS (Fargate)*: cluster, region, subnets (`subnet-a,subnet-b`), security groups,
execution role ARN, and `accessKeyId:secretAccessKey` (or leave empty to use the CLI's own profile).

Deploy generates a task definition (512 CPU / 1024 MB, awsvpc, awslogs to `/ecs/<name>`), registers it
and creates or force-redeploys the service; the URL is the task's public IP plus the container port.

**Limits:** no volumes; no name-based service discovery (add ECS Service Connect in the console for
that); the terminal needs ECS Exec enabled on the service; the public IP changes when a task is
replaced — put an ALB in front for anything permanent.

---

# Create a machine — per provider

| Provider | Credentials | Where to get them | Ports open by default |
|---|---|---|---|
| **Hetzner Cloud** | API token | console.hetzner.cloud → project → Security → API tokens (**read & write**) | all (no firewall unless you add one) |
| **DigitalOcean** | API token | cloud.digitalocean.com → API → tokens (**write** scope) | all |
| **Akamai Linode** | API token | cloud.linode.com → profile → API tokens (Linodes: read/write) | all |
| **Azure VM** | `az login` or `tenant:appId:secret` | see [Azure](#azure-vm-create-a-machine) | ssh, plus the port range (`az vm open-port` runs for you) |
| **AWS EC2** | `aws configure` or `accessKeyId:secretAccessKey` | IAM user with EC2 rights | ssh and the port range, in a security group AspireUI creates |
| **Google Compute Engine** | `gcloud auth login` or a service-account key | see Cloud Run above, plus `roles/compute.admin` | ssh, plus a tagged firewall rule AspireUI creates |

Common flow for all of them: key pair → server with cloud-init (`curl -fsSL https://get.docker.com | sh`)
→ wait for `docker version` (up to 5 min, then a plain ssh install attempt) → target added → probe.

**What AspireUI opens, and where:**

- **AWS EC2** — a security group `aspireui-<name>` is created (or reused) with ssh and 20000–29999
  inbound, and the instance is launched into it with a public IP. Without that the VPC's default group
  would allow nothing and the box would never answer.
- **Google Compute Engine** — the instance is tagged `aspireui` and a firewall rule `aspireui-apps`
  (tcp:20000-29999 on that tag) is created once per project; ssh is already open on a default network.
- **Azure VM** — `az vm create` opens ssh, then `az vm open-port --port 20000-29999` runs for the range.
- **Hetzner / DigitalOcean / Linode** — no firewall by default; nothing to open.

All of these open the range to `0.0.0.0/0`, which is what a public app URL needs. Narrow it at the
provider if the apps are only reached through a reverse proxy on the box itself.

# Domains, per target

Domains are configured on the target, because the reverse proxy usually lives next to the machine:

- **Nginx Proxy Manager** — full support: list, create, Let's Encrypt certificate, enable/disable,
  delete. This is what "this machine" always used; the old global NPM settings became its configuration
  automatically. Fields: NPM URL (`http://npm.lan:81`), account email, password, and optionally a fixed
  *forward host* if the proxy reaches the box under another name. The forward host of a remote target
  defaults to that target's address, never `localhost`.
- **Azure custom domain** — for `aca` targets; see the limitation and the manual commands above.
- **Manual DNS** — nothing is configured for you: the app's domain dialog shows which address and port
  to point a record at. The honest choice for a VPS with your own Caddy/Traefik in front.
- **None** — the app is reached by `host:port` only.

# Credentials

Every secret a target needs is stored one of two ways:

- **Encrypted in the database** (AES-GCM). The key comes from `ASPIREUI_SECRET_KEY` (base64 32 bytes, or
  any passphrase) when set — use that in a container deploy, so the database alone is worthless —
  otherwise from a key file created next to the workspace on first use, readable by the owner only.
  ```bash
  # generate one
  openssl rand -base64 32
  # docker compose
  environment:
    ASPIREUI_SECRET_KEY: "…"
  ```
- **As a reference**, when you would rather not store it at all: `env:NAME` reads an environment
  variable, `file:/path` reads a file. Nothing secret enters the database.

Secrets never leave the server: the API only ever reports *whether* one is set. Reading the target list
needs any signed-in account (the deploy pickers need it); creating, editing, deleting, provisioning and
destroying a machine is admin-only.

Key material is written to `WORKSPACE_DIR/_targets/<id>/` (ssh key, known_hosts, generated ssh config,
TLS certificates, kubeconfig) with owner-only permissions, because `ssh` and `docker` need real files.
A single `Include` line is added to the running user's `~/.ssh/config` so docker's ssh transport finds
the aliases.

# Deploying, moving, copying

- **Builder → Deploy** shows the targets as tiles; the default one is preselected. Re-deploying an app
  keeps it where it is.
- **App store → Install to…** takes several targets at once. Each gets an independent instance — its own
  stack, data and domain — named `<app> @ <target>`.
- **Clone hooks** can name the target their clones are deployed to.
- **App menu → Move or copy to another target:**
  - **Move** stops the app, streams each volume straight from one daemon to the other (nothing is
    buffered in AspireUI, so a 40 GB volume is fine), deploys on the new target and removes it from the
    old one. If the new deployment fails, the app is started again where it was.
  - **Copy** creates a second, independent instance; the original keeps running. Data is copied only if
    you ask for it.
  - Two targets that turn out to be the **same docker daemon** (say `local` and an ssh alias for the
    same box) are detected by the daemon's id and the move is refused instead of tearing the app down.
  - Data cannot travel to or from an orchestrator target — the dialog says so before you start.

# Ports, URLs, health

Host ports are allocated per target: what other apps on that target use, plus whatever else its daemon
already publishes. A port an app already had is kept, so URLs stay stable across re-deploys — including
across a move.

URLs of a remote target are built from the target's public address, never from the address you happen to
browse AspireUI at. Health is the same verdict everywhere: a container that crash-loops, exits or
reports unhealthy makes the app broken — compose service or Kubernetes pod alike.

# Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `unreachable` on an ssh target | key not in `authorized_keys`, wrong user, or ssh not reachable | `ssh -i <key> user@host docker version` from the AspireUI host |
| ssh target worked, now `Host key verification failed` | the box's host key changed (reinstall) | clear `WORKSPACE_DIR/_targets/<id>/known_hosts` and test again |
| `permission denied while trying to connect to the Docker daemon` | the ssh user is not in the `docker` group | `usermod -aG docker <user>` on the box, then reconnect |
| App is green but the URL times out | the port range is closed in the provider's firewall | see the per-provider table above |
| `docker did not answer within 5 minutes` after *Create a machine* | cloud-init still running, or ssh blocked (AWS/GCP) | open ssh, then *install docker over ssh* on the target row |
| `az`/`aws`/`gcloud` `is not installed or not on PATH` | the CLI is not in the image | derived image as shown above |
| ACA deploy fails with `AuthorizationFailed` | the service principal lacks Contributor on the scope | re-run `az ad sp create-for-rbac` with the right `--scopes` |
| Container app starts, then 502 | wrong target port, or the service needs a companion it cannot resolve | check `--target-port`, and see the multi-service limit |
| `no free host port in <from>-<to>` | the target's range is exhausted | widen the range on the target |
| Backups menu missing | the target has no docker volumes | expected on Kubernetes and the managed platforms |
| Kubernetes app deployed but no URL | *how apps are reached* is still ClusterIP | pick NodePort/LoadBalancer/Ingress on the target |
| Kubernetes app lost its data on restart | no storage class on the target, so volumes are `emptyDir` | set a storage class and re-deploy |
| Container app cannot reach its database | the database has no port in compose, so it got no ingress | expose the port in the stack, or use a managed database |
