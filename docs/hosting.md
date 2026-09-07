# Hosting (install &amp; forget)

**Hosting** turns AspireUI into a self-hosted **app appliance**: pick an app, hit install, and it runs
as a persistent, tracked deployment on the Docker host AspireUI lives on — with a URL, lifecycle
controls, and updates. No editor, no C#, no `dotnet run` babysitting.

It's the third of three ways AspireUI can ship a stack. They serve different needs:

| Mode | What it does | Use it when |
|------|--------------|-------------|
| **Run** | Shells `dotnet run` on the generated AppHost, tails the log, opens the Aspire dashboard. Stops when you stop it. | You're **developing** a stack and want the live dashboard, traces, and per-resource logs. |
| **Publish** | Runs `aspire publish` and hands you the artifact (Docker Compose / Helm / Bicep / manifest) to deploy yourself. | You want to deploy **elsewhere** (Portainer, a cluster, Azure) or keep the artifact in Git. |
| **Hosting** | Publishes to Compose, then brings it **up on this host** and keeps it running + tracked — restart-on-boot, updatable, with a real URL. | You want the app to just **run and stay up**, like an appliance (Umbrel/CasaOS style). |

## Installing an app

Two entry points, same result:

- **App store** (Simple mode → **Install app**): a browsable store of curated **Apps**, catalog
  **Packages** (n8n, Adminer, Postgres, …), and your saved **Snippets**. Filter by category/type,
  read the details dialog, click **Install**. Apps then ask for the things that matter up front — the
  name and any required secret (session/app secrets are generated for you, editable before install).
  Packages ask for their configuration in the same add dialog as the canvas, then deploy.
- **App store → From Git**: paste any repository with an [`aspireui-app.json`](app-manifest.md), a
  `docker-compose` file, or an Aspire AppHost. Same steps as [Import from Git](importing.md) — branch,
  compose file, services, env vars — but it deploys straight to hosting instead of opening the editor.
  If the repository carries a manifest, AspireUI offers the app exactly as its author defined it.
- **Editor → Deploy** (advanced): build/import any stack, then use the **Deploy** button (or the
  Publish panel's **Hosting** card) to deploy that exact stack.

When a Nginx Proxy Manager is connected (on the target, under **Settings → Hosting → Deploy targets**),
the domain dialog opens right after an install, so the app can go on your own domain (with Let's Encrypt
HTTPS) in the same flow. A hosted app with a domain shows two open buttons: the domain and the internal
`host:port` URL.

Apps do not have to live on this machine: a deploy can go to a box over ssh, a Kubernetes cluster or a
managed container platform, an app can be installed onto several targets at once, and a running app can
be moved or copied — with its data — from one target to another. See
[Deploy targets](deploy-targets.md).

Installed apps show up on the overview (Simple mode: "My apps") and on the **Hosting** page.

## Managing a deployment

Every hosted app has the same controls (overview card menu, Hosting page, or the editor):

- **Start / Stop** — `docker compose up -d` / `stop`.
- **Configure (env vars)** — edit each resource's environment, then it stops, applies, and redeploys.
  The same dialog holds **Limits & health** (see below) and the app's published ports.
- **View logs** — live-streamed `docker compose logs` for the whole deployment or a single container,
  searchable, copyable, downloadable.
- **Update (pull &amp; recreate)** — pulls newer images and recreates the containers.
- **Files (volumes)** — walk the app's named volumes: view a file in a dialog (pdf, images,
  markdown, office documents and audio, through the
  [MudEx](https://www.mudex.org/webcomponents) file viewer), download it, upload one, rename
  anything, create a folder, or delete it. Deleting a folder takes everything under it, and the
  volume root itself is refused — that is what *Undeploy + delete data* is for. An upload is
  streamed straight into the container, so it never lands on the AspireUI host's disk on the way.
  Needs the *browse* permission to look and the *write* permission to change anything, and only
  works on a target with a Docker socket.
- **Back up volumes** — snapshots the app's named volumes.
- **Undeploy** — `docker compose down`. **Named volumes are kept** — your data survives, and a
  re-deploy picks it back up.
- **Undeploy + delete data** — `docker compose down -v`. The app's volumes (database, files) are
  **deleted**. Use this to cleanly reinstall an app that got stuck half-initialized.

## Limits & health

**Configure → Limits & health** decides what an app may use and how it says it is well:

| Field | Compose key | Meaning |
| --- | --- | --- |
| CPUs | `cpus` | Cores the app may use, e.g. `1.5`. |
| Memory (MB) | `mem_limit` | Hard memory cap; the kernel kills the container above it. |
| Processes | `pids_limit` | Cap on processes/threads — a fork bomb in one app stays in that app. |
| Restart | `restart` | `unless-stopped` (default), `always`, `on-failure` or `no`. |
| Health check | `healthcheck` | A shell command per container; a zero exit means healthy. |

Limits go on **every container of the app**, because "this app may have half a core" is the question
people have; a per-service cap is what the compose file itself is for — and a limit the app's own
compose file already sets is left alone. The health check is per container and only added where the
image ships none, so a well-built image keeps its own. An unhealthy container turns the app's badge
red on the overview and the app page, and the reason is in the health detail.

Both are stored with the stack, so they survive a redeploy, travel with an export, and can be seeded.

## Automation

**Configure → Automation** gives an app its own clock. Each line is an action and when it runs:

| Action | What it does |
| --- | --- |
| Restart | Stops and starts the app. |
| Stop / Start | For an app that only needs to be up during office hours. |
| Update (pull & recreate) | The auto-update: pulls newer images and recreates the containers. |
| Check for updates | Pulls to see whether anything is newer and **notifies** — changes nothing. |
| Back up volumes | Same snapshot the Backups dialog takes, on a schedule, with the same retention. |

Either **daily at** a time (UTC, optionally narrowed to days like `mon,wed,fri`) or **every** N
hours. An interval starts counting when you save it, so adding "every 24 h" at five in the afternoon
does not restart the app at five in the afternoon. A daily schedule fires once in the minute it names
and not again that day, and the scheduler ticks every five minutes — so 04:00 means "some time
between 04:00 and 04:05".

Auto-update is a schedule like any other on purpose: one mechanism, one place to look, and the same
notification path as everything else. Turn *Check for updates* on with a webhook or Telegram
configured under **Settings → Notifications** and you get told about a new version without anything
being changed under you.

Schedules are stored with the stack and need no redeploy — saving only automation does not touch the
running app.

## The bundled Aspire dashboard

Deployments can ship with the **Aspire dashboard** container (admin toggle under **Settings →
Hosting**). Set a **browser token** there and AspireUI hands out a one-click login link — no reverse
proxy needed. The dashboard's OTLP telemetry endpoint is secured with the same token.

## Under the hood — why hosting does extra work

Hosting runs `aspire publish` to Compose, but a raw Compose file has **no AppHost orchestrator**, so
things Aspire normally does at *runtime* (in Run mode) don't happen on their own. AspireUI bridges
those gaps after publishing, which is why an app can work under **Run** but need help under
**Hosting**:

- **Host ports** — apps are given a distinct free host port (20000+) so two apps that both listen on
  `:80` don't collide, and the app's URL points at the right one.
- **Parameters** — `aspire publish` writes parameters to `.env` by name but leaves the values blank;
  AspireUI fills them (from your values, or a deterministic secret) so hosted apps don't boot with
  empty passwords/keys.
- **Companion databases** — an integration that declares `AddDatabase("x")` relies on the
  orchestrator to run `CREATE DATABASE` in Run mode; Compose won't. AspireUI sets `POSTGRES_DB` /
  `MYSQL_DATABASE` on the companion so the database exists on first boot (otherwise the app
  crash-loops with *"database x does not exist"*).
- **Restart policy** — every service gets `restart: unless-stopped` so the app comes back after a
  host reboot.
- **Per-app URL path** — apps whose UI lives under a sub-path (Plex `/web`, Pi-hole `/admin`) get it
  appended to the URL.

## Requirements

Hosting needs **Docker** (with the Compose v2 plugin) on the machine AspireUI runs on. When AspireUI
itself runs in a container, it uses the host's Docker socket — see the security note in
`docker-compose.yml`.

See also: [Running &amp; Deploying](running-and-deploying.md) · [Live Resources &amp; Logs](live-resources.md) · [App Catalog](apps.md)
