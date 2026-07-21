# Running & Deploying

## Running a stack

Hit **Run** in the editor toolbar. AspireUI writes out the stack's generated project into its
workspace directory and shells `dotnet run` on it, tailing the process output. Status moves through
`NotRunning` → `Starting` → `Running` (once the Aspire dashboard URL shows up in the log) →
`Failed` if the process exits non-zero before that. The toolbar's **Open Dashboard** link appears
once running, and **Stop** kills the process tree.

The **Logs** panel shows the same run log live, auto-scrolling, with error-looking lines
(`error`/`exception`/`fail`) highlighted; a banner explains a `Failed` run at a glance.

Beyond that stack-level log, AspireUI shows **real per-resource status, URLs, spawned child
resources, and per-resource log streaming right on the canvas** while the stack runs — see
[Live Resources & Logs](live-resources.md). For full telemetry (traces, metrics, queryable logs) use
the Aspire dashboard link.

Running a stack needs both the **.NET SDK** and **Docker** available wherever AspireUI runs, since
Aspire resources (Postgres, Redis, Ollama, …) commonly start their own containers.

You can also drive run/stop and the dashboard from the **stack cards** on the overview: each card has
a live traffic-light (grey/yellow/green/red, with the error detail in the tooltip on failure) and
▶ / ⏹ / ↗ buttons.

## The Dashboard panel

The editor's **Dashboard** tab gives in-tool access to the Aspire dashboard. When the stack is
running it shows an **Open dashboard** button (opens the real dashboard in a new tab — the reliable
path, since the Blazor dashboard blocks embedding) and an optional **Embed (experimental)** toggle
that renders it in a sandboxed iframe via a built-in reverse proxy. When it isn't running, a Start
button.

## Publishing & deploying

The editor's **Publish / Deploy** panel turns a stack into real deployment artifacts using Aspire's
own publishers — not hand-rolled guesses. The split **Publish** button lets you pick a target:

- **Docker Compose** → `docker-compose.yaml` + `.env` (a Docker Compose environment + `aspire publish`).
- **Kubernetes (Helm)** → a Helm chart (`Chart.yaml`, `values.yaml`, `templates/*`). Uses the preview
  Kubernetes publisher.
- **Azure Bicep** → `main.bicep` + per-resource modules for Azure Container Apps (`azd` / `az deployment`).
- **Aspire Manifest** → `aspire-manifest.json`, a portable deployment descriptor other tools consume.

The primary artifact is shown syntax-highlighted; **Copy** it or **Download bundle** (a zip of every
generated file). For **Compose**, the `.env` lists parameters (e.g. generated passwords) with empty
values — **fill those in before deploying** — and you can **Deploy now (docker compose up -d)** /
**Stop (compose down)** locally, or drop the bundle into Portainer/Coolify. Publishing needs the
**`aspire` CLI** installed (bundled with the Aspire tooling); local Compose deploy needs Docker on the host.

## Open in an IDE

The editor's **Open in…** menu launches the materialized project in **VS Code**, **Rider**, or
**Visual Studio** on the machine AspireUI runs on (it shells the IDE) — handy when the tool runs
locally. If the IDE isn't found on `PATH`, you get a friendly toast rather than an error.

## Running AspireUI in development

```bash
dotnet run --project src/AspireUI.Server
```

Opens at **http://localhost:5158**. Requires the .NET SDK (10.0+).

## Self-hosting with Docker

The repo ships a `Dockerfile`, `docker-compose.yml`, and `install.sh` for running AspireUI itself
as a container — useful for a home server, a Proxmox VM, or any Docker host.

**One-liner (no checkout needed)** — clones/updates the repo and starts the container:

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/fgilde/AspireUI/master/install.sh)"
```

It clones into `$HOME/aspireui` (override with `ASPIREUI_DIR=...`). Re-run to update. Inside a
checkout, just:

```bash
./install.sh
```

or manually:

```bash
docker compose up -d --build
```

Then open **http://localhost:8080**.

`install.sh` checks that Docker and the Compose v2 plugin are present, then runs
`docker compose up -d --build` — it's safe to re-run.

The container's runtime stage keeps the full .NET SDK (not just the ASP.NET runtime), because
running a stack shells `dotnet run` on generated projects. It also mounts the **host's Docker
socket** (`/var/run/docker.sock`) so that stacks launched from inside the container can start their
own containers on the host.

> **Security note:** mounting the Docker socket gives the container root-equivalent control over
> the host. AspireUI requires a login (a first-run wizard creates the admin user), but it's still a
> small-team, local-first tool — only run it on a trusted host and don't expose its port directly to
> the internet without a reverse proxy and TLS in front.

### Configuration

| Variable          | Default                  | Meaning                                            |
|--------------------|---------------------------|-----------------------------------------------------|
| `ASPNETCORE_URLS`  | `http://0.0.0.0:8080`     | Address(es) the server listens on (published build) |
| `DB_PATH`          | `/data/aspireui.db`       | SQLite database file for stacks/settings            |
| `WORKSPACE_DIR`    | `/data/workspace`         | Where generated AppHost projects are written to run  |
| `ASPIREUI_ADMIN_USERNAME` / `ASPIREUI_ADMIN_PASSWORD` | *(unset)* | First-run only: seed an admin user (skipped once any user exists; password stored hashed) |
| `ASPIREUI_SEED_STACK_NAME` + `ASPIREUI_SEED_STACK_PROJECTS` | *(unset)* | Seed a starter stack (once) with one `AddProject` node per `;`/`,`-separated project path |

The `ASPIREUI_*` seeding vars let a container start pre-configured (admin + a starter stack) with no
manual wizard — the basis for running AspireUI itself as a resource inside another stack. A prebuilt
image is published to **`ghcr.io/fgilde/aspireui:latest`** on every push (GitHub Container Registry).

The Docker image sets these for you; `docker-compose.yml` keeps them on a named volume mounted at
`/data` so stacks and settings survive container rebuilds. The AI provider (base URL, key, model) is
configured in-app under **Settings**, not via environment variables — see
[AI Assistant](ai-assistant.md).
