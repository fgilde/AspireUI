# AspireUI — Polish: capabilities fix, install/README, docs+help, live node status (Design)

**Date:** 2026-07-19 (Slice 7 — polish batch)

## Goals

1. **Fix AddModel-type capabilities.** Fluent extension methods on a resource builder that start with
   `Add` (e.g. `ollama.AddModel("llama3.2")`, `pg.AddDatabase("db")`) must appear as selectable
   capabilities in the property grid, exactly like `With*` methods. Currently the catalog only collects
   `With*` methods, so `AddModel` is emittable (templates use it) but not selectable in the UI.
2. **README (English) + self-host install.** A real README (screenshot, what/why, features, quick start,
   configuration) plus a container-based server install: `Dockerfile`, `docker-compose.yml`, and an
   `install.sh` for Linux/Proxmox.
3. **Documentation site + in-app help.** A `docs/` site servable via GitHub Pages (docsify — no build),
   linked from the tool; plus lightweight in-app help (a Help modal + tooltips on key controls).
4. **Live node status on the canvas.** When a stack is running, each node shows a run-state indicator;
   the Aspire dashboard link stays prominent. (Full per-resource health/URL parity with the Aspire
   dashboard needs the Aspire resource gRPC service — see Non-Goals.)

## Non-Goals (this slice)

- Full per-resource state + endpoint URLs like the Aspire dashboard (needs the Aspire resource service
  / dashboard OTLP integration — a focused follow-up). This slice shows stack-run-state per node, not
  per-resource health.
- Auth, wizard, deploy, reverse-proxy (separate backlog slices).

## 1. Capabilities: include `Add*` fluent methods

`CatalogService`: the resource-capability discovery currently filters methods on `IResourceBuilder<W>`
by `Name.StartsWith("With")`. Broaden to `StartsWith("With") || StartsWith("Add")` (still: extension
method, first param `IResourceBuilder<W>`, returns an `IResourceBuilder<>`). This captures `AddModel`,
`AddDatabase`, etc. as capabilities without affecting the top-level `AddX` discovery (those have receiver
`IDistributedApplicationBuilder`, a different branch). The `CatalogMethod.Label` strips the `With`/`Add`
prefix. Serialization/codegen unchanged — the grid emits `{var}.AddModel(args)` as a WithCall, which
CodeGen already renders verbatim. Test: `AddOllama`'s `withs` include a method `AddModel`.

## 2. README + install

- `README.md`: keep the screenshot at top; sections — What is AspireUI, Features (bulleted, per slice),
  Quick start (`dotnet run` for dev), **Run on a server (Docker)**, Configuration (env: `ASPNETCORE_URLS`,
  `DB_PATH`, `WORKSPACE_DIR`, and the AI settings in-app), Building a stack / importing / the AI assistant
  (short), Notes/limitations (Run needs Docker + the .NET SDK; single-user local tool, no auth yet).
- `Dockerfile`: multi-stage. Build stage `mcr.microsoft.com/dotnet/sdk:10.0` builds the SPA (npm) + the
  server; **runtime stage keeps the full SDK** (not just aspnet runtime) because the "Run a stack" feature
  shells `dotnet run` on generated projects. Set `ASPNETCORE_URLS=http://0.0.0.0:8080`, `DB_PATH=/data/aspireui.db`,
  `WORKSPACE_DIR=/data/workspace`. Expose 8080.
- `docker-compose.yml`: the image; port `8080:8080`; a named volume mounted at `/data`; mount
  `/var/run/docker.sock:/var/run/docker.sock` so launched stacks can start containers on the host Docker.
- `install.sh`: POSIX script — check Docker present, warn if not; `docker compose up -d --build`; print the
  URL. Idempotent, minimal.

## 3. Docs + help

- `docs/` docsify site: `index.html` (docsify CDN — but the CSP-free public Pages context allows the CDN;
  this is GitHub Pages, not an in-tool artifact), `README.md` (home), `getting-started.md`, `building-stacks.md`,
  `importing.md`, `ai-assistant.md`, `running-and-deploying.md`, `_sidebar.md`. Content derived from the specs.
  Served via GitHub Pages "deploy from branch → /docs". Target URL `https://fgilde.github.io/AspireUI/`.
- In-app: a **Help** button (overview header + editor header) opening a Mantine Modal with a concise
  how-to (create/import/AI/run) and a link to the docs site. Tooltips on the main controls (New Stack,
  From demo, Import, Settings, Run, Add capability). Keep copy short.

## 4. Live node status

- Editor already polls `runStatus` into EditorContext. Canvas nodes read it: when `state === "Running"`,
  each node shows a small green "running" dot/badge; `Failed` → red; otherwise neutral. A tooltip on the
  badge shows the state. The RunToolbar's dashboard link stays the way to see full per-resource detail.
- Document that per-resource status/URLs (true Aspire-dashboard parity on the node) is deferred.

## Error handling / testing

- Backend: catalog test asserts `AddOllama` capabilities include `AddModel`; existing tests stay green.
- Frontend: build gate; a small vitest if a pure helper is added (e.g. node-state → color). Node badge is
  visual (build-gated).
- Docker/install: not unit-tested; `install.sh` is documented + the Dockerfile builds. Verify the image
  builds in CI-less fashion is optional/best-effort (may be slow) — at minimum `dotnet publish` succeeds.

## Deferred (BACKLOG)

Per-resource Aspire status/URLs on nodes, auth, wizard, deploy pipeline, reverse-proxy, Proxmox beyond the
compose install.
