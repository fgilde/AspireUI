# Plan: Appliance / App-Store Mode (runtipi / Umbrel-style)

Status: **DRAFT / not started.** Captured so we both remember the intent + the intended approach.
Owner discussion: 2026-07-22. Build only after explicit go.

## Vision

A second "mode" for AspireUI that reframes the existing pieces as a self-hosted **app store +
appliance dashboard** — install curated apps one click, manage them (start/stop/update/logs/URLs),
expose them with URLs/domains — WITHOUT losing the builder. The differentiator vs Umbrel/runtipi/
CasaOS: everything is real **.NET Aspire / Docker Compose** underneath, visually composable, and
**exportable** (C# AppHost + compose bundle). "An app store that's also a visual Aspire builder."

Two modes over ONE backend:
- **Builder** (today): canvas, codegen, run via Aspire, publish/deploy.
- **Appliance** (new): app grid (store) + installed-apps dashboard; install = persistent compose deploy.

## What we already have (reuse ~60%)

- Curated app catalog: container presets (`catalog/presets/container-presets.json`) + reflected Aspire resources.
- Docker underneath; run/stop; **live per-resource status/URLs/log-streaming** (resource-service gRPC).
- **Compose publish** (`aspire publish` → docker-compose.yaml) + **local deploy** (`docker compose up -d` / `down`).
- Auth (cookie, admin, users), env editing, per-resource pickers, path picker, seeding.
- Prebuilt image + one-liner installer + `AddAspireUI` (self-hosting AspireUI itself).

## The core pivot: persistent runtime

Builder "Run" = `dotnet run` on an Aspire AppHost = **dev-time, ephemeral, no boot-autostart**. Wrong
for "install & forget". Appliance "Install" must use the path we already have: **generate → compose
publish → `docker compose up -d`** with `restart: unless-stopped`, tracked as an *installed app*.
So an installed app is a long-lived compose project, not an Aspire run.

## Slices (each shippable; stop anywhere)

### Slice A — Install = persistent compose deploy (foundation)
- New concept **InstalledApp** (persisted): id, name, source (preset id / stack id), compose dir, state.
- "Install" action: build a minimal stack (1 preset node, or any stack) → compose publish → `docker compose up -d`.
- Track installed apps in the DB; list them.
- Reuse existing deploy/down services; add `restart: unless-stopped` to generated compose for appliance installs.
- **Verify**: install nginx preset → container persists across an AppHost restart; uninstall = `compose down`.

### Slice B — Appliance UI shell (the "store" + "my apps")
- Mode toggle (Builder ⇄ Appliance) in the header; Appliance hides canvas/codegen.
- **Store**: the app grid (presets + curated, categories, search/tags — reuse palette catalog).
- **My Apps**: installed-apps dashboard — status traffic-light, open-URL, logs (reuse ResourceLogDrawer/live views but over compose `docker` inspect/logs), start/stop/uninstall.
- Install dialog: name + the few env/params that matter (admin creds, volumes, port).

### Slice C — Ingress (URLs/domains/TLS)
- Add a managed reverse proxy (Traefik or Caddy) as an appliance system service.
- Per-app route: `-.local` / subpath / custom domain; optional TLS (self-signed or ACME).
- Auto-wire installed apps' endpoints into the proxy; show the real URL in My Apps.
- Biggest new brick — do last of the "core".

### Slice D — Lifecycle polish
- **Updates**: pull newer image + `compose up -d` recreate; "update available" hint.
- **Backups**: snapshot named volumes (tar) + restore.
- **Autostart on boot**: rely on compose `restart` policy + document host setup; optional systemd unit for AspireUI itself.
- Multi-arch image (arm64) so it runs on a Pi/NAS.

### Slice E — Catalog depth
- Bigger curated app catalog with rich metadata (description, category/tags, icon, required env, default volumes, "needs Postgres/Redis" dependency hints → auto-add companion resources).
- Community/extensible catalog (load extra preset JSON from a folder or URL).

## Non-goals (for now)
- Multi-node/orchestration (k8s) — single Docker host only.
- Replacing the builder — appliance mode sits alongside it.
- Marketplace/payments.

## Risks / open questions
- Runtime split (compose-deploy vs aspire-run) must stay clean — don't entangle the two run paths.
- Docker-socket security already noted; appliance mode makes AspireUI more "always-on prod", so auth/exposure hardening matters more (reverse proxy + TLS + strong admin).
- Ingress on Windows/Docker-Desktop vs Linux hosts differs — target Linux hosts primarily.
- Apps needing sidecars (Immich, Paperless-ngx: Postgres+Redis) → dependency templates, not bare single containers.

## Sequencing recommendation
A → B give a usable "install curated apps, see/manage them" product on top of what exists (small).
C is the heavy lift that makes it a real Umbrel rival. D/E are ongoing polish/breadth.
