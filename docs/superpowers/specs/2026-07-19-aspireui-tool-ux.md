# AspireUI — Real Tool UX + Run/Stop (Design)

**Date:** 2026-07-19
**Slice:** Turn the canvas-to-code core into a usable tool. Stacks overview, schema-driven
property grid, comfortable reference/config editing, live C# preview, run/stop with Aspire
dashboard, and a professional look (Mantine). Builds on the merged canvas-to-code core.

## Goals

1. **Stacks overview** — landing page lists stacks (create / open / delete / run-status); no more dropping straight into one.
2. **Schema-driven property grid** — selecting a node shows typed fields (image, ports, env, volumes, …) from a catalog parameter schema, not raw `WithCall` text.
3. **Comfortable references** — a node picker ("which from where") in addition to drawing edges.
4. **Live code preview** — read-only C#/Aspire preview that updates after each save.
5. **Run / Stop** — start `dotnet run`, see running status, open the Aspire dashboard, stop it.
6. **Professional look** — Mantine app shell, dark theme, custom node cards.

## Non-Goals (this slice)

- Auth / users / first-run wizard
- Deploy (docker compose / server)
- Reverse proxy
- Foreign-node import UI, `AddProject<T>` generics (still deferred)
- Full semantic compile validation (still syntax-only in the save path)

## Architecture

Backend stays ASP.NET Core (net10.0). New/changed services and endpoints; the React SPA is
restructured around Mantine + react-router. The C# project on disk remains source of truth;
`WithCalls` + new `AddArgs` remain the canonical serialization (round-trip preserved).

```
Frontend (Mantine + react-router)
  "/"            StacksOverview   (list, create modal, open, delete, status badge)
  "/stacks/:id"  Editor
       Palette (grouped + search) | Canvas (@xyflow/react custom nodes) | Panel (Tabs: Properties, References)
       CodePreview (read-only, live)
       Toolbar (Run/Stop, dashboard link, Export)

Backend (ASP.NET Core, minimal APIs)
  CatalogService   reflection + overlay -> ResourceType{ addParams[], withs[ {params[]} ] }
  StackStore       (unchanged: SQLite JSON blob)
  CodeGenService   emit AddArgs; Aspire 13.x + net10.0 generated project; GenerateProgram reused by preview
  ImportService    capture AddArgs[1..] (round-trip)
  ExportService    (unchanged)
  RunService       NEW: process lifecycle per stack, parse dashboard URL, status/log tail
```

## Data model changes

```
NodeModel  += AddArgs: List<string>   // positional args after ResourceName, raw C# literals
```
Everything else unchanged. `WithCall{Method,Args}` still holds `WithX` calls; `EdgeModel`
still holds references. The property grid is a typed editor that reads/writes these.

`StackModel.TargetFramework` default becomes `net10.0` (was net9.0) so generated projects
run on the only installed runtime.

## Catalog parameter schema (the property-grid engine)

`CatalogService.GetCatalog()` returns enriched types:
```
CatalogParam  { Name, Type, Required, Default?, Options?, Label }   // Type: string|int|bool|enum
CatalogWith   { Method, Label, Params: CatalogParam[] }
ResourceType  { AddMethod, Label, Icon, Group, AddParams: CatalogParam[], Withs: CatalogWith[] }
```
- **Reflection** supplies parameter names + types for the `AddX` method (beyond the name) and for each surfaced `WithX`.
- **Overlay** (`catalog/*.json`) supplies label/icon/group, which params/withs to surface, defaults, and enum option lists.
- Unknown resources still work generically (no overlay → params inferred from reflection; unknown `WithX` reachable via a generic "add raw call" escape hatch).

Overlays expanded for the core resources:
- **AddContainer**: `addParams` = image (string, required), tag (string); withs = WithHttpEndpoint(port,targetPort), WithVolume(name,target), WithBindMount(source,target), WithEnvironment(name,value).
- **AddPostgres / AddRedis**: withs = WithDataVolume, WithPgAdmin / WithRedisCommander, WithHttpEndpoint, WithEnvironment.

The grid maps fields to serialization:
- `addParams` → `NodeModel.AddArgs` (positional, quoted per type).
- multi-instance withs (env vars, endpoints, volumes) → repeated `WithCall` entries.
- references → `EdgeModel` (WithReference).

## Property grid → editor mapping

| Param type | Mantine control |
|---|---|
| string | TextInput |
| int | NumberInput |
| bool | Switch |
| enum | Select (Options) |
| env (name/value pairs) | key-value list editor |
| endpoint/volume (repeated with) | add/remove row list |
| references | MultiSelect of other node names |

On any change: rebuild the node's `ResourceName`/`AddArgs`/`WithCalls` (and edges for
references), then `saveStack`/`patchNode`. The code preview re-fetches.

## Code preview

`GET /stacks/{id}/preview` → `text/plain` = `CodeGenService.GenerateProgram(stack)`. Since
edits persist immediately, preview after save reflects current state. Rendered read-only,
monospace, in the editor.

## Run / Stop

`RunService` (singleton, in-memory `Dictionary<string, RunHandle>`):
- **Start**: `dotnet run --project workspace/{id}` (Debug), redirect stdout/stderr, capture a rolling log tail. Parse the Aspire dashboard URL from output (regex for `dashboard` login line, e.g. `https?://localhost:\d+/login\?t=\S+`).
- **Status**: `NotRunning | Starting | Running | Failed`, plus `DashboardUrl?` and last N log lines. `Running` once the dashboard URL is seen; `Failed` if the process exits non-zero before that.
- **Stop**: kill the process tree; state → NotRunning.
- Endpoints: `POST /stacks/{id}/run`, `POST /stacks/{id}/stop`, `GET /stacks/{id}/status`.
- Frontend toolbar: Run button → poll status → when Running, show "Open Dashboard" (external link) + Stop. Overview shows a status badge per stack.

`RunService` takes the command as an injectable delegate (default `dotnet run …`) so tests can
drive it with a fast dummy process instead of spawning a real Aspire app.

Generated project must build to run: `CodeGenService.GenerateCsproj` uses Aspire 13.x
(matching installed packages) and `net10.0`, `<ImplicitUsings>enable</ImplicitUsings>`, and
`using Aspire.Hosting;` (already added).

## REST additions

```
GET  /stacks/{id}/preview           text/plain C#
POST /stacks/{id}/run               start; 200 + status
POST /stacks/{id}/stop              stop; 200 + status
GET  /stacks/{id}/status            { state, dashboardUrl?, log[] }
```
Existing routes unchanged.

## Error handling

- Run start when project doesn't build: process exits non-zero → status `Failed` with log tail surfaced in UI.
- Run when already running: return current status (idempotent), don't spawn twice.
- Stop when not running: no-op 200.
- Property grid with unknown resource: generic reflection-inferred fields + raw-call escape hatch; never blocks editing.
- Server shutdown: RunService kills tracked processes on dispose (best-effort) so orphans don't linger.

## Testing

Backend (xUnit):
- Catalog exposes `AddContainer` with an `image` addParam and `WithHttpEndpoint` with `port` param.
- CodeGen emits AddArgs: `builder.AddContainer("web", "nginx");`.
- Round-trip invariant extended: a stack with AddArgs + WithCalls survives `import(generate(m))`.
- Dashboard-URL parser extracts the URL from a sample Aspire stdout line.
- RunService lifecycle with an injected fast dummy command: NotRunning → Starting → Running (on URL match) → Stop → NotRunning.
- `GET /preview` returns generated code.

Frontend (Vitest):
- Existing model mapping tests kept.
- Pure transform: field-values ↔ (AddArgs/WithCalls) round-trips for a container node (image, one port, one env var).

Look/UX polish delivered via Mantine; the build gate is `npm run build` clean.

## Deferred (later slices, tracked)

Auth, wizard/dependency-check, deploy, reverse-proxy, foreign-node import UI, `AddProject<T>`
generics, semantic compile-check, run on remote/host, log streaming (this slice polls status).
