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
  read the details dialog, click **Install**. Packages ask for any required configuration first (the
  same add dialog as the canvas), then deploy.
- **Editor → Deploy** (advanced): build/import any stack, then use the **Deploy** button (or the
  Publish panel's **Hosting** card) to deploy that exact stack.

Installed apps show up on the overview (Simple mode: "My apps") and on the **Hosting** page.

## Managing a deployment

Every hosted app has the same controls (overview card menu, Hosting page, or the editor):

- **Start / Stop** — `docker compose up -d` / `stop`.
- **Configure (env vars)** — edit each resource's environment, then it stops, applies, and redeploys.
- **View logs** — live-streamed `docker compose logs` for the whole deployment or a single container,
  searchable, copyable, downloadable.
- **Update (pull &amp; recreate)** — pulls newer images and recreates the containers.
- **Back up volumes** — snapshots the app's named volumes.
- **Undeploy** — `docker compose down`. **Named volumes are kept** — your data survives, and a
  re-deploy picks it back up.
- **Undeploy + delete data** — `docker compose down -v`. The app's volumes (database, files) are
  **deleted**. Use this to cleanly reinstall an app that got stuck half-initialized.

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
