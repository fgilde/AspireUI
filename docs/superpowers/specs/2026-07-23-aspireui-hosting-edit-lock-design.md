# Spec: Hosting foundation + edit-lock (Appliance Slice 1)

Date: 2026-07-23. Status: **approved for planning.**
Part of the [Appliance / App-Store Mode](../plans/2026-07-22-appliance-store-mode.md) epic — this is
**Slice 1** (renamed from the plan's "Slice A", generalized per owner refinement 2026-07-23).

## Goal

Let a user **deploy any stack persistently into a "Hosting" area** (survives AppHost restarts, unlike
dev Run), manage its lifecycle (status / URL / logs / start / stop / undeploy), and **lock the stack
for editing while it's hosted-and-running** so a live deployment can't be changed by accident.

This is the load-bearing slice. View-mode RBAC + Simple UI (Slice 2), reverse-proxy ingress/domains/TLS
(Slice 3), and updates/backups/autostart (Slice 4) build on it and are out of scope here.

## Context / reuse

- `PublishService.Publish(stack, publishRoot, "compose")` → `aspire publish` produces a
  `docker-compose.yaml` in a per-stack publish dir.
- `DeployService.Up(outputDir)` / `Down(outputDir)` run `docker compose up -d` / `down` there.
- Existing endpoints `/api/stacks/{id}/publish`, `/deploy`, `/deploy/down` already chain these
  (ephemeral, untracked). Slice 1 adds a **tracked, persistent** deployment on top.
- Dev **Run** (`RunService`, `dotnet run` an AppHost + resource-service gRPC) stays a **separate path**
  — do not entangle the two (plan risk note). Hosting status/logs come from Docker/compose, not the
  resource service.

## Assumptions

- AspireUI and the hosted stacks run on the **same Docker host** (the owner's Proxmox box). Single-host
  only; no remote targets. Linux-first (compose `restart: unless-stopped` + boot policy work there).
- One stack maps to **at most one** deployment. Re-deploying updates it in place.

## Backend design

### DeploymentStore (new, SQLite)
Table `deployments`, mirroring the existing store pattern (own `*.db` shared with the app db path):
`id TEXT PK, stack_id TEXT UNIQUE, name TEXT, compose_dir TEXT, project TEXT, state TEXT,
urls TEXT (json array), created_at TEXT, updated_at TEXT, last_error TEXT`.
Methods: `Upsert`, `GetByStack(stackId)`, `Get(id)`, `List()`, `Delete(id)`, `SetState(id, state, error?)`.

`Deployment` record: `Id, StackId, Name, ComposeDir, Project, State, Urls (List<string>), CreatedAt,
UpdatedAt, LastError`. `state` ∈ `deploying | running | stopped | failed`.

### HostingService (new)
Wraps publish+deploy into tracked lifecycle. Compose project name `aspireui-<stackId[..8]>` (stable, so
start/stop/down target the same project). Compose dir under the existing publish root per stack.

- `Deploy(stack)`: set/insert deployment `deploying` → `PublishService.Publish` → **post-process the
  generated `docker-compose.yaml`**: add `restart: unless-stopped` to each service → `DeployService.Up`
  with an explicit `-p <project>` → parse published host port mappings into `urls` → `running` (or
  `failed` + `last_error`). Idempotent (re-deploy = publish again + `up -d` recreates).
- `Stop(id)`: `docker compose -p <project> stop` → `stopped`.
- `Start(id)`: `docker compose -p <project> up -d` (or `start`) → `running`.
- `Undeploy(id)`: `docker compose -p <project> down` (+ volumes? **no** — keep data by default) →
  delete deployment row.
- `Status(id)`: `docker compose -p <project> ps` (json) → per-service running/exited; reconcile the
  stored `state` (e.g. detect crashed). Used to refresh the Hosting list.
- `LogsAsync(id, service?)`: stream `docker compose -p <project> logs -f` as SSE (same shape the
  ResourceLogDrawer already consumes).

`DeployService` gains a `-p <project>` option (or a small `Compose(project, args, dir)` helper) so
stop/start/ps/logs target the tracked project deterministically.

URL derivation (Slice 1, raw): from the compose file's published `ports` (`HOST:CONTAINER`), emit
`http://<host>:<HOST>` for each mapped port (host = request host or configured base). Domains/TLS = Slice 3.

### Endpoints (under `/api`, auth-gated like the rest)
- `POST /stacks/{id}/hosting/deploy` → `{ deployment }`
- `POST /stacks/{id}/hosting/stop`, `.../start`, `.../undeploy`
- `GET  /hosting` → `[deployment]` (all), each refreshed via `Status`
- `GET  /hosting/{id}/logs` (SSE)
- `GET  /stacks/{id}` **also returns** a `deployment` field (`null` | `{state, urls}`) so the editor
  knows the lock state without a second call.

### Edit-lock (server-authoritative)
A stack is **locked** when its deployment exists and `state == running` (or `deploying`).
Mutating endpoints return **409 Conflict** `{ message, deployment }` when locked:
`PUT /stacks/{id}`, `PATCH /stacks/{id}/nodes/{nodeId}`, `POST /stacks/{id}/edges`,
`DELETE /stacks/{id}/edges/{edgeId}`, `POST /stacks/{id}/run`, `POST /stacks/{id}/code/save`,
`DELETE /stacks/{id}`. A shared `EnsureUnlocked(stackId)` guard checks the store.
`Stop` is the escape hatch — it unlocks. (Read endpoints/preview/validate stay allowed.)

## Frontend design

### Hosting page (`/hosting`, new route + account-menu entry)
Cards/table of deployments: status traffic-light (running/stopped/failed), the app name, its URL(s)
(open in new tab), a **Logs** button (drawer, reuse ResourceLogDrawer against `/hosting/{id}/logs`),
and **Stop / Start / Undeploy** + **Open in editor**. Polls `GET /hosting` (~4s) like the overview.

### Deploy action
"Deploy to hosting" button in the editor header (and the overview card ⋯ menu). Confirms, calls the
deploy endpoint, then routes to `/hosting` (or shows progress). Distinct from the existing dev **Run**.

### Edit-lock UI
`GET /stacks/{id}` returns `deployment`. When locked, the editor renders a **read-only banner**
("Running in hosting — stop it to edit") with a **Stop & edit** button (confirm → stop → refetch →
unlocked), and disables mutating affordances (palette drops, property edits, canvas edits, Save in the
code panel). 409s from any slipped-through write surface the same banner.

## Testing

- **DeploymentStore**: CRUD + `GetByStack` uniqueness (unit).
- **Edit-lock**: with a running deployment row, `PUT /stacks/{id}` / node patch / `/run` → 409; after
  `stop` → 200 (integration, fake/seeded store; no real docker needed — gate reads the store).
- **HostingService** compose post-process: generated compose gets `restart: unless-stopped` on services;
  project name stable (unit over a sample compose file).
- **URL parse**: `ports: ["8096:8096"]` → `http://<host>:8096` (unit).
- Docker-dependent paths (Up/Down/ps/logs) verified manually / behind the existing deploy tests'
  shell-stub pattern — not in CI.

## Non-goals (Slice 1)
Reverse proxy / domains / TLS; view-mode RBAC + Simple shell; updates/backups/autostart; multi-host;
per-service (vs per-stack) start/stop in the UI (compose handles the project as a unit here).

## Risks
- Keep hosting (compose-deploy) and dev-run (aspire) paths cleanly separate.
- Docker-socket / always-on exposure → hardening matters more; real ingress + TLS is Slice 3.
- Windows/Docker-Desktop vs Linux differences in compose port/host resolution — target Linux (Proxmox).
