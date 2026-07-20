<img width="2547" height="1261" alt="image" src="https://github.com/user-attachments/assets/54d51236-c6c0-4cf7-a4a3-ae5b814a879a" />

# AspireUI

Visually build, import, and run .NET Aspire AppHost projects.

## What is AspireUI

AspireUI is a visual canvas for [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) AppHost
projects. Drag out resources, wire up references, tweak properties in a grid, and watch the
generated C# update live — then run the stack and jump straight into the Aspire dashboard. Import an
existing AppHost (`.cs` / `.csproj` / `.zip`) to start from what you already have, or spin up a demo
template to explore.

Docs site (in progress): **https://fgilde.github.io/AspireUI/**

## Features

- Visual canvas for composing an AppHost, backed by an intelligent reflection-based capability catalog
- Dynamic "add resource" dialog driven by the catalog (new Aspire integrations show up automatically)
- Property grid for editing resource arguments and capabilities
- Reference wiring between resources
- Live C# preview of the generated `Program.cs`, kept in sync with the canvas
- **Code editor** (Monaco) with real C# IntelliSense (Roslyn-backed); edits re-parse into the graph
- Run / stop a stack, with a link straight into the Aspire dashboard
- Publish a stack to **Docker Compose** (via `aspire publish`): view the generated `docker-compose.yaml`, download the bundle, or deploy it locally
- NuGet packages panel for the AppHost project
- Run logs for the running stack
- Import an existing AppHost from `.cs`, `.csproj`, or a `.zip`
- Demo templates to start from a working example
- Built-in AI assistant to help build and modify stacks
- Dockable panels — arrange the workspace the way you like

## Quick start (development)

Requires the .NET SDK (10.0+).

```bash
dotnet run --project src/AspireUI.Server
```

Opens at **http://localhost:5158**.

## Run on a server (Docker)

The included `Dockerfile` / `docker-compose.yml` run AspireUI as a self-contained container — useful for
a home server, Proxmox VM, or any Docker host.

```bash
./install.sh
```

or manually:

```bash
docker compose up -d --build
```

Then open **http://localhost:8080**.

The container mounts the host's Docker socket so that stacks launched from AspireUI can start their own
containers on the host — see the security note in `docker-compose.yml`.

## Configuration

| Variable          | Default                  | Meaning                                            |
|--------------------|---------------------------|-----------------------------------------------------|
| `ASPNETCORE_URLS`  | `http://0.0.0.0:8080`     | Address(es) the server listens on (published build) |
| `DB_PATH`          | `/data/aspireui.db`       | SQLite database file for stacks/settings            |
| `WORKSPACE_DIR`    | `/data/workspace`         | Where generated AppHost projects are written to run  |

The AI provider (OpenAI-compatible endpoint, model, key) is configured in-app under **Settings** — no
environment variables needed for that.

## Notes / limitations

- **Running a stack** shells out to `dotnet run` on a generated AppHost project, and Aspire resources
  frequently start containers — this needs the .NET SDK and Docker available wherever AspireUI runs
  (the Docker image above includes both).
- Login-gated (a first-run wizard creates the admin user), but still a small-team, local-first tool —
  don't expose its port directly to the internet without a reverse proxy and TLS in front.
- The built-in AI assistant needs a configured OpenAI-compatible endpoint (see Settings) to do anything.

## Screenshots

<img width="2371" height="1254" alt="image" src="https://github.com/user-attachments/assets/dd24855c-3205-4a8f-bb89-13ef950a5e32" />


<img width="2374" height="1259" alt="image" src="https://github.com/user-attachments/assets/b968d27a-0b2a-4a39-bbaf-d00d006a33fb" />

<img width="1446" height="558" alt="image" src="https://github.com/user-attachments/assets/61c908be-7bfd-4a36-b2a7-e04cfe4cf621" />



More detail: **https://fgilde.github.io/AspireUI/**
