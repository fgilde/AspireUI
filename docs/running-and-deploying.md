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
> the host. Only run this on a trusted host, and don't expose AspireUI's port to the internet —
> there is no authentication yet; this is a single-user, local-first tool.

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
