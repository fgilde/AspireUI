<p align="center"><img src="docs/aspireui_wordmark.svg" alt="AspireUI" width="540" /></p>

<p align="center">
  <a href="https://github.com/fgilde/AspireUI/actions/workflows/docker-publish.yml"><img src="https://github.com/fgilde/AspireUI/actions/workflows/docker-publish.yml/badge.svg" alt="Build"></a>
  <a href="https://github.com/fgilde/AspireUI/pkgs/container/aspireui"><img src="https://img.shields.io/badge/ghcr.io-fgilde%2Faspireui-2496ED?logo=docker&logoColor=white" alt="Container image"></a>
  <a href="https://github.com/fgilde/AspireUI/releases"><img src="https://img.shields.io/github/v/tag/fgilde/AspireUI?label=release&sort=semver" alt="Release"></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white" alt=".NET 10">
  <a href="https://github.com/fgilde/AspireUI/stargazers"><img src="https://img.shields.io/github/stars/fgilde/AspireUI?style=flat" alt="Stars"></a>
  <a href="https://fgilde.github.io/AspireUI/"><img src="https://img.shields.io/badge/docs-online-brightgreen" alt="Docs"></a>
</p>

<p align="center">
  <sub>Powered by</sub>&nbsp;
  <a href="https://www.nuget.org/packages?q=Nextended.Aspire&includeComputedFrameworks=true&prerel=true"> <img src="https://raw.githubusercontent.com/fgilde/Nextended/refs/heads/main/icon.png" height="18" valign="middle" alt="Nextended.Aspire"> <b>Nextended.Aspire</b></a>
  <a href="https://github.com/fgilde/Nextended"><img src="https://img.shields.io/badge/-181717?logo=github&logoColor=white" valign="middle" alt="Nextended on GitHub"></a>
  &nbsp;·&nbsp;
  <a href="https://www.aspire.love/"><img src="https://www.aspire.love/assets/favicon.ico" height="16" valign="middle" alt=""> <b>aspire.love</b></a>
</p>

<hr>

<table>
  <tr>
    <td align="center"><b>GitHub Dark</b></td>
    <td align="center"><b>Blazor</b></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/editor-github-dark.png" alt="AspireUI editor — GitHub Dark theme" /></td>
    <td><img src="docs/screenshots/editor-blazor.png" alt="AspireUI editor — Blazor theme" /></td>
  </tr>
</table>

<sub>Read the <a href="https://fgilde.github.io/AspireUI/">docs</a> to find more.</sub>

<h1>
  <img src="docs/aspireui_transparent.svg" alt="AspireUI" width="50" align="center">
  AspireUI
</h1>

Visually build, import, and run .NET Aspire AppHost projects.

## Deploy

### One-click cloud


<p align="center">
  <a href="https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Ffgilde%2FAspireUI%2Fmaster%2Fdeploy%2Fazuredeploy.json">
    <img src="https://aka.ms/deploytoazurebutton" alt="Deploy to Azure" height="32">
  </a>
  &nbsp;
  <a href="https://render.com/deploy?repo=https://github.com/fgilde/AspireUI">
    <img src="https://render.com/images/deploy-to-render-button.svg" alt="Deploy to Render" height="32">
  </a>
  &nbsp;
  <a href="https://deploy.cloud.run/?git_repo=https://github.com/fgilde/AspireUI">
    <img src="https://deploy.cloud.run/button.svg" alt="Run on Google Cloud" height="32">
  </a>
  &nbsp;
  <!--
  <a href="https://console.aws.amazon.com/cloudformation/home#/stacks/quickcreate?templateURL=https%3A%2F%2Fraw.githubusercontent.com%2Ffgilde%2FAspireUI%2Fmaster%2Fdeploy%2Faws-template.yaml&stackName=AspireUI">
    <img src="https://s3.amazonaws.com/cloudformation-examples/cloudformation-launch-stack.png" alt="Launch Stack on AWS" height="32">
  </a>
  --> 
  <!--
   Actual aws deploy template missing
  -->
</p>



> These spin up the prebuilt image on a managed container platform — perfect for **trying AspireUI**
> (build, import, publish, explore the catalog). Managed containers have **no host Docker socket**, so
> the in-app **Run** and **Hosting** features (which launch other containers) need self-hosting with
> Docker — see below. AWS / Google Compute / Linode / Hetzner have no standard one-click button; use the
> `docker run` line below on any VM there.

### Self-host with Docker (full features)

**Fastest** — pull the prebuilt image and run it (no clone, no build):

```bash
docker run -d --name aspireui -p 8080:8080 \
  -v aspireui-data:/data -v /var/run/docker.sock:/var/run/docker.sock \
  ghcr.io/fgilde/aspireui:latest
```

Or the installer, which clones the repo and starts it via Compose (and re-run to update):

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/fgilde/AspireUI/master/install.sh)"
```

Then open **http://localhost:8080**. Needs Docker + the Compose v2 plugin. The Docker socket mount lets
stacks you launch (and [hosted apps](docs/hosting.md)) start their own containers — see the security
note in `docker-compose.yml` and [Self-hosting](#run-on-a-server-docker).

## What is AspireUI

AspireUI is a visual canvas for [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) AppHost
projects. Drag out resources, wire up references, tweak properties in a grid, and watch the
generated C# update live — then run the stack and jump straight into the Aspire dashboard. Import an
existing AppHost (`.cs` / `.csproj` / `.zip`) to start from what you already have, or spin up a demo
template to explore.

Docs site (in progress): **https://fgilde.github.io/AspireUI/**

## Features

- Visual canvas for composing an AppHost, backed by an intelligent reflection-based capability catalog
- Dynamic "add resource" dialog driven by the catalog (new Aspire integrations show up automatically),
  with a live C# preview and reference wiring in both directions
- **Setup / macro extensions** — composite helpers like `AddObservabilityStack` / `AddDapr` (which set
  up several resources at once) are auto-discovered and grouped under "Setup"
- Property grid for editing resource arguments and capabilities, with type-filtered
  resource-reference pickers (and inline "create the dependency" ＋), and a server-side path picker
  for project/folder params
- Reference wiring between resources
- Reopen closed dock panels from a **Panels** menu
- Live C# preview of the generated `Program.cs`, kept in sync with the canvas
- **Code editor** (Monaco) with real C# IntelliSense (Roslyn-backed); edits re-parse into the graph
- Run / stop a stack, with a link straight into the Aspire dashboard
- **Live resource view** — while a stack runs, the canvas shows real per-resource status, endpoint
  URLs, and the child resources each builder spawns (from the Aspire resource service), plus
  **per-resource console-log streaming**
- Publish a stack to **Docker Compose / Kubernetes (Helm) / Azure Bicep / Aspire manifest** (via
  `aspire publish`): view the generated artifact, download the bundle, or deploy Compose locally
- **Hosting — install &amp; forget**: deploy a stack as a persistent, tracked appliance with a URL,
  start/stop/update/backup, and a one-click **app store** (Umbrel/CasaOS style) — see [Hosting](docs/hosting.md)
- **139+ preconfigured container apps** (Immich, Jellyfin, Nextcloud, Gitea, n8n, Pi-hole, …) ready to
  drop on the canvas or install from the store — see the [App Catalog](docs/apps.md)
- NuGet packages panel for the AppHost project
- Import an existing AppHost from `.cs`, `.csproj`, or a `.zip` — or a `docker-compose.yml`
- Demo templates to start from a working example
- Built-in AI assistant to help build and modify stacks
- Themes, command palette (Ctrl/⌘+K), saveable dock layouts, undo/redo
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
| `ASPIREUI_ADMIN_USERNAME` | *(unset)*          | First-run only: create this admin (skipped once any user exists) |
| `ASPIREUI_ADMIN_PASSWORD` | *(unset)*          | Password for the seeded admin (stored hashed) |
| `ASPIREUI_SEED_STACK_NAME` | *(unset)*         | Seed a starter stack of this name on first start |
| `ASPIREUI_SEED_STACK_PROJECTS` | *(unset)*     | `;`/`,`-separated project paths → one `AddProject` node each in the seeded stack |

The AI provider (OpenAI-compatible endpoint, model, key) is configured in-app under **Settings** — no
environment variables needed for that. The `ASPIREUI_*` vars let a container come up pre-configured
(admin + a starter stack) without the manual setup wizard — handy when running AspireUI itself as a
resource inside another stack.

A prebuilt image is published to **`ghcr.io/fgilde/aspireui:latest`** on every push, so you can
`docker run` it directly instead of building.

## Notes / limitations

- **Running a stack** shells out to `dotnet run` on a generated AppHost project, and Aspire resources
  frequently start containers — this needs the .NET SDK and Docker available wherever AspireUI runs
  (the Docker image above includes both).
- Login-gated (a first-run wizard creates the admin user), but still a small-team, local-first tool —
  don't expose its port directly to the internet without a reverse proxy and TLS in front.
- The built-in AI assistant needs a configured OpenAI-compatible endpoint (see Settings) to do anything.

## Screenshots

A running **Supabase + Observability** stack — builder nodes show live per-resource status and the
child resources they spawned (`supabase-db`, `supabase-auth`, the `monitoring-*` stack, …):

![Live running stack](docs/screenshots/live-resources.png)

<img width="1797" height="1153" alt="image" src="https://github.com/user-attachments/assets/7255f244-773c-4558-858c-93d485993363" />


<img width="2374" height="1259" alt="image" src="https://github.com/user-attachments/assets/b968d27a-0b2a-4a39-bbaf-d00d006a33fb" />

<img width="1446" height="558" alt="image" src="https://github.com/user-attachments/assets/61c908be-7bfd-4a36-b2a7-e04cfe4cf621" />



More detail: **https://fgilde.github.io/AspireUI/**
