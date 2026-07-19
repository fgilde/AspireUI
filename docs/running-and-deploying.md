# Running & Deploying

## Running a stack

Hit **Run** in the editor toolbar. AspireUI writes out the stack's generated project into its
workspace directory and shells `dotnet run` on it, tailing the process output. Status moves through
`NotRunning` → `Starting` → `Running` (once the Aspire dashboard URL shows up in the log) →
`Failed` if the process exits non-zero before that. The toolbar's **Open Dashboard** link appears
once running, and **Stop** kills the process tree.

The **Logs** panel shows the same run log live, auto-scrolling, with error-looking lines
(`error`/`exception`/`fail`) highlighted; a banner explains a `Failed` run at a glance. For full
per-resource health and endpoint detail beyond the stack-level running/failed indicator, use the
Aspire dashboard link — deep per-resource status on the canvas itself is on the backlog.

Running a stack needs both the **.NET SDK** and **Docker** available wherever AspireUI runs, since
Aspire resources (Postgres, Redis, Ollama, …) commonly start their own containers.

## Publishing to Docker Compose & deploying

The editor's **Publish / Deploy** panel turns a stack into real deployment artifacts using Aspire's
own compose publisher — not a hand-rolled guess. Hitting **Publish (Docker Compose)** materializes a
copy of the stack augmented with a Docker Compose environment, runs `aspire publish` on it, and shows
the generated **`docker-compose.yaml`** (syntax-highlighted) plus the **`.env`** of parameters. You
can **Copy** the YAML or **Download bundle** (a zip of `docker-compose.yaml` + `.env`).

The `.env` lists parameters (e.g. generated passwords) with empty values — **fill those in before
deploying**. Then either run `docker compose up -d` yourself in the shown output directory, or hit
**Deploy now (docker compose up -d)** to have AspireUI run it locally (**Stop (compose down)** tears
it back down). Deploying needs Docker running on the host; publishing needs the **`aspire` CLI**
installed (bundled with the Aspire tooling).

Kubernetes/Azure and other Aspire publishers, remote deploy targets, and reverse-proxy exposure are
not wired into the UI yet — but the generated compose bundle is standard and portable.

## Running AspireUI in development

```bash
dotnet run --project src/AspireUI.Server
```

Opens at **http://localhost:5158**. Requires the .NET SDK (10.0+).

## Self-hosting with Docker

The repo ships a `Dockerfile`, `docker-compose.yml`, and `install.sh` for running AspireUI itself
as a container — useful for a home server, a Proxmox VM, or any Docker host.

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

The Docker image sets these for you; `docker-compose.yml` keeps them on a named volume mounted at
`/data` so stacks and settings survive container rebuilds. The AI provider (base URL, key, model) is
configured in-app under **Settings**, not via environment variables — see
[AI Assistant](ai-assistant.md).
