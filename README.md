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



> These deploy the prebuilt image on a managed container platform. Good for trying out AspireUI
> (build, import, publish, browse the catalog), but managed containers don't give you a host Docker
> socket, so the in-app **Run** and **Hosting** features (they launch other containers) need
> self-hosting with Docker instead, see below. AWS, Google Compute, Linode and Hetzner don't have a
> standard one-click button; just use the `docker run` line below on any VM there.

### Self-host with Docker (full features)

Fastest option: pull the prebuilt image and run it, no clone, no build:

```bash
docker run -d --name aspireui -p 8080:8080 \
  -v aspireui-data:/data -v /var/run/docker.sock:/var/run/docker.sock \
  ghcr.io/fgilde/aspireui:latest
```

Or use the installer, which clones the repo and starts it via Compose (re-run it later to update):

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/fgilde/AspireUI/master/install.sh)"
```

Then open **http://localhost:8080**. You'll need Docker plus the Compose v2 plugin. The Docker socket
mount is what lets stacks you launch (and [hosted apps](docs/hosting.md)) start their own containers;
see the security note in `docker-compose.yml` and [Self-hosting](#run-on-a-server-docker).

> **On the host you only need Docker.** The official image bundles the .NET SDK, the Docker CLI and
> Compose plugin, and the `aspire` CLI. The in-app **Run** and **Hosting** features depend on all of
> them, so they're baked into the image rather than left optional. You only need to install .NET or
> Git yourself if you're building from source or running AspireUI outside the image. Keep in mind the
> Docker socket mount (what powers Run/Hosting) is **root-equivalent on the host**, so run AspireUI on
> an isolated, disposable box.

### Umbrel / Unraid

This repo doubles as an **Umbrel community app store** and an **Unraid CA template repo**: add
`https://github.com/fgilde/AspireUI` as a community app store in umbrelOS, or point Unraid at
[`templates/aspireui.xml`](templates/aspireui.xml). Details and the official-store submission steps are
in [Umbrel & Unraid stores](docs/app-stores.md).

### Proxmox VE (one command)

On a Proxmox host, [`deploy/pve-install-aspireui.sh`](deploy/pve-install-aspireui.sh) sets up a clean
Debian VM, installs Docker, and runs AspireUI with the socket mounted, taking you from nothing to a
working URL:

```bash
# on the Proxmox host, as root:
bash pve-install-aspireui.sh
# or override defaults:
IP=192.168.1.50/24 GW=192.168.1.1 RAM=8192 CORES=4 DISK=40 bash pve-install-aspireui.sh
```

It prints `http://<vm-ip>:8080` when done; put that behind a reverse proxy (e.g. Nginx Proxy Manager)
for a real domain, and AspireUI can then manage that same NPM from **Settings → Hosting**. Tear it all
down with `qm stop <vmid> && qm destroy <vmid>`.

> **Why a VM, not an LXC:** AspireUI launches *other* containers through the Docker socket (Hosting),
> which needs real Docker. That's clean in a VM, but fiddly with Docker-in-LXC on PVE. A disposable VM
> also keeps the root-equivalent socket contained in one throwaway place.

## What is AspireUI

AspireUI is a visual canvas for [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) AppHost
projects. Drag out resources, wire up references, tweak properties in a grid, and the generated C#
updates as you go. Run the stack and it opens straight into the Aspire dashboard. Import an existing
AppHost (`.cs` / `.csproj` / `.zip`) to start from what you already have, or use a demo template if
you just want to explore.

Docs site (in progress): **https://fgilde.github.io/AspireUI/**

## Features

- Visual canvas for composing an AppHost, backed by a reflection-based capability catalog
- Dynamic "add resource" dialog driven by the catalog (new Aspire integrations show up automatically),
  with a live C# preview and reference wiring in both directions
- **Setup / macro extensions**: composite helpers like `AddObservabilityStack` / `AddDapr` (they set
  up several resources at once) are auto-discovered and grouped under "Setup"
- Property grid for editing resource arguments and capabilities, with type-filtered
  resource-reference pickers (and inline "create the dependency" ＋), and a server-side path picker
  for project/folder params
- Reference wiring between resources
- Reopen closed dock panels from a **Panels** menu
- Live C# preview of the generated `Program.cs`, kept in sync with the canvas
- **Code editor** (Monaco) with real C# IntelliSense (Roslyn-backed); edits re-parse into the graph
- Run / stop a stack, with a link straight into the Aspire dashboard
- **Live resource view**: while a stack runs, the canvas shows real per-resource status, endpoint
  URLs, and the child resources each builder spawns (from the Aspire resource service), plus
  per-resource console-log streaming
- Publish a stack to **Docker Compose / Kubernetes (Helm) / Azure Bicep / Aspire manifest** (via
  `aspire publish`): view the generated artifact, download the bundle, or deploy Compose locally
- **Hosting**: deploy a stack as a persistent, tracked appliance with a URL, complete with
  start/stop/update/backup and a one-click app store (Umbrel/CasaOS style); see [Hosting](docs/hosting.md)
- **159 preconfigured container apps** (Immich, Jellyfin, Nextcloud, WordPress, Gitea, n8n, Pi-hole, …),
  ready to drop on the canvas or install from the store; see the [App Catalog](docs/apps.md), and
  [add your own](#bring-your-own-app-to-the-store) with one JSON file
- NuGet packages panel for the AppHost project
- Import an existing AppHost from `.cs`, `.csproj`, or a `.zip`, or from a `docker-compose.yml`
- Demo templates to start from a working example
- Built-in AI assistant to help build and modify stacks
- Themes, command palette (Ctrl/⌘+K), saveable dock layouts, undo/redo
- Dockable panels, arrange the workspace the way you like

## Bring your own app to the store

An app is one JSON file — the same file works in three places, so you pick how far you want to go.
Full field reference: **[app manifest](docs/app-manifest.md)**.

```json
{
  "$schema": "https://raw.githubusercontent.com/fgilde/AspireUI/master/src/AspireUI.Server/catalog/presets/aspireui-app.schema.json",
  "id": "my-app",
  "label": "My App",
  "group": "Tools",
  "image": "ghcr.io/acme/my-app:1.4.0",
  "port": 8080,
  "description": "What it is, and how the first login works.",
  "volumes": [["data", "/app/data"]],
  "params": [{ "key": "app-secret", "env": "APP_SECRET", "default": "", "secret": true }],
  "website": "https://example.com",
  "github": "https://github.com/acme/my-app",
  "license": "MIT",
  "submitter": "acme",
  "source": "https://github.com/acme/my-app"
}
```

1. **Ship it with your app.** Commit it as `aspireui-app.json` in your repository. Users then run
   *Install from Store → From Git*, paste your URL, and AspireUI installs the app the way you defined
   it — your image, ports, volumes, env, and a freshly generated secret per install. Nothing to merge,
   no release of ours to wait for.
2. **Publish it as an app source.** Serve the file (or an array of several apps) at any http(s) URL —
   a raw file in your repository does it. An admin adds the URL once under
   *Settings → Hosting → App sources*, and your apps appear in that instance's store, marked
   **Community** with your name on them. Sources are admin-only and refreshed only on request, so
   nothing changes behind anyone's back.
3. **Submit it to the store.** Open a pull request that adds
   `src/AspireUI.Server/catalog/presets/community/<id>.json` (same JSON, plus `submitter`/`source`,
   which the store shows as provenance). `dotnet test tests/AspireUI.Server.Tests` validates every
   app: required fields, that each `${companion}` reference resolves, and that the resource builds.
   One app per pull request.
4. **Keep it private.** Point `EXTRA_PRESETS_DIR` at a folder of such files and your instance shows
   them without touching this repository.

Rules of thumb: publicly pullable image with a real tag, `port` is the port *inside* the container
(AspireUI publishes a free host port itself), everything worth keeping on a named `volume`, secrets
as `params` with an empty default so each install generates its own, and a description that says what
happens on first login.

## Quick start (development)

Requires the .NET SDK (10.0+).

```bash
dotnet run --project src/AspireUI.Server
```

Opens at **http://localhost:5158**.

## Run on a server (Docker)

The included `Dockerfile` / `docker-compose.yml` run AspireUI as a self-contained container. Useful
for a home server, a Proxmox VM, or any other Docker host.

```bash
./install.sh
```

or manually:

```bash
docker compose up -d --build
```

Then open **http://localhost:8080**.

The container mounts the host's Docker socket so stacks launched from AspireUI can start their own
containers on the host; see the security note in `docker-compose.yml`.

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
| `ASPIREUI_SET_<Key>` | *(unset)*              | **Seed any setting** from env (see below) — ships a pre-configured image |
| `ASPIREUI_SET_FORCE` | `false`                | `true` = `ASPIREUI_SET_*` overrides existing values on every start (default: fill only what's unset) |

### Pre-seeding settings (`ASPIREUI_SET_*`)

Every in-app setting can be seeded from the environment, so an image can come up pre-configured with
no setup wizard needed. Use `ASPIREUI_SET_<Key>` with the exact setting key. By default a value is
only written when that setting is still empty (so a user's later change sticks); set
`ASPIREUI_SET_FORCE=true` if you want it to always apply. Known keys:

| Area | Keys |
|------|------|
| **AI assistant** | `AiKind` (`http`/`cli`), `AiBaseUrl`, `AiApiKey`, `AiModel`, `AiProviderLabel`, `AiCliTool` |
| **Hosting dashboard** | `HostDashboard` (`true`/`false`), `DashboardToken` |
| **Nginx Proxy Manager** | `NpmEnabled` (`true`/`false`), `NpmBaseUrl`, `NpmEmail`, `NpmPassword`, `NpmForwardHost` |

```bash
docker run -d -p 8080:8080 -v aspireui-data:/data -v /var/run/docker.sock:/var/run/docker.sock \
  -e ASPIREUI_ADMIN_USERNAME=admin -e ASPIREUI_ADMIN_PASSWORD='change-me' \
  -e ASPIREUI_SET_AiBaseUrl=http://ollama:11434 -e ASPIREUI_SET_AiModel=llama3.2 \
  -e ASPIREUI_SET_NpmEnabled=true -e ASPIREUI_SET_NpmBaseUrl=http://npm:81 \
  ghcr.io/fgilde/aspireui:latest
```

## API &amp; MCP (agents)

The whole product is a REST API. The OpenAPI spec is at **`/openapi/v1.json`** with a browsable
**Scalar** reference UI at **`/scalar`** (account menu → *API reference*).

Auth is either the browser session cookie or a **personal access token**: create one under
**Settings → API &amp; Agents** and send it as `Authorization: Bearer <token>` on any `/api/...` call.

Agents can drive AspireUI through the built-in **MCP server** at **`/api/mcp`** (same Bearer auth). Tools:
inspect stacks (`list_stacks`, `get_stack`), browse the catalog (`search_apps`), author
(`create_stack`, `install_app`, `add_resource`, `delete_stack`), and run/host
(`run_stack`, `stop_run`, `deploy_to_hosting`, `start_hosting`, `stop_hosting`, `hosting_logs`). Add it
to an MCP-capable agent:

```json
{
  "mcpServers": {
    "aspireui": {
      "url": "http://<host>:8080/api/mcp",
      "headers": { "Authorization": "Bearer <your-token>" }
    }
  }
}
```

A prebuilt image is published to **`ghcr.io/fgilde/aspireui:latest`** on every push, so you can
`docker run` it directly instead of building.

## Notes / limitations

- **Running a stack** shells out to `dotnet run` on a generated AppHost project, and Aspire resources
  often start containers too. That means the .NET SDK and Docker both need to be available wherever
  AspireUI runs (the Docker image above includes both).
- Login-gated (a first-run wizard creates the admin user), but it's still a small-team, local-first
  tool. Don't expose its port directly to the internet without a reverse proxy and TLS in front.
- The built-in AI assistant needs a configured OpenAI-compatible endpoint (see Settings) to do anything.

## Screenshots

A running **Supabase + Observability** stack. Builder nodes show live per-resource status and the
child resources they spawned (`supabase-db`, `supabase-auth`, the `monitoring-*` stack, and so on):


<table>
  <tr>
    <td align="center"><b>Live Resources | Running Overview</b></td>
    <td align="center"><b>Complex Application shared user and password</b></td>
  </tr>
  <tr>
    <td>
      <img src="https://github.com/user-attachments/assets/57a8c4d3-f7d2-4193-b6d6-5c657d540a86" alt="AspireUI live running stack" />
    </td>
    <td>
      <img src="https://github.com/user-attachments/assets/3f7e5d9a-1679-47d8-bdcc-161dbb043079" alt="AspireUI screenshot 2" />
    </td>
  </tr>

  <tr>
    <td align="center"><b>Dashboard</b></td>
    <td align="center"><b>Environment edit in Hosting</b></td>
  </tr>
  <tr>
    <td>
      <img src="https://github.com/user-attachments/assets/20e18426-70f1-441b-90a3-c2960e3a9e5f" alt="AspireUI screenshot 3" />
    </td>
    <td>
      <img src="https://github.com/user-attachments/assets/ae7da385-1f04-4ab7-81ec-11b80966712d" alt="AspireUI screenshot 4" />
    </td>
  </tr>

  <tr>
    <td align="center"><b>Install from Marketplace</b></td>
    <td align="center"><b>Git import</b></td>
  </tr>
  <tr>
    <td>
      <img width="2560" height="1385" alt="image" src="https://github.com/user-attachments/assets/53138c7a-57b6-4365-b8f7-18031be50182" />
    </td>
    <td>
      <img width="2560" height="1385" alt="image" src="https://github.com/user-attachments/assets/e06f6b55-a2ba-4bc7-8a9b-5a8254ca3818" />
    </td>
  </tr>

  <tr>
    <td align="center"><b>Hosting Treeview</b></td>
    <td align="center"><b>Settings</b></td>
  </tr>
  <tr>
    <td>
      <img src="https://github.com/user-attachments/assets/bb7d99de-cf7d-4912-8ce3-6530613d785e" alt="AspireUI screenshot 5" />
    </td>
    <td>      
      <img width="1743" height="1335" alt="image" src="https://github.com/user-attachments/assets/a603b545-b314-4268-8ead-1ad257fc957d" />
    </td>
  </tr>

  <tr>
    <td align="center"><b>Add Aspire resource</b></td>
    <td align="center"><b>Code match for selection</b></td>
  </tr>
  <tr>
    <td>
      <img src="https://github.com/user-attachments/assets/49290942-a2d2-431e-b05b-53e34a50632d" alt="AspireUI screenshot 7" />
    </td>
    <td>
      <img src="https://github.com/user-attachments/assets/06faa9b9-abfa-4b47-a5b2-6ec43891d124" alt="AspireUI screenshot 8" />
    </td>
  </tr>
</table>




 



More detail: **https://fgilde.github.io/AspireUI/**

## License

AGPL-3.0 — see [LICENSE](LICENSE). Self-hosting, private changes and commercial use are fine; if you
offer AspireUI (or a modified version) to others over a network, you have to make your source
available to those users. The in-app GitHub link in the header serves as that source offer.
