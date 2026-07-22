# Feature backlog (accepted 2026-07-22)

User accepted all of these (dropped only "GitHub-export"). Build slice-by-slice, cheapest/highest-
value first. Each slice: build → test → commit/push → restart. Tick as done.

## Priority order

### P1 — cheap, high value
- [x] **Per-resource restart/command** — Aspire `ExecuteResourceCommand` (proto already present; Resource
      snapshot carries `Commands`). Buttons in the mini-dashboard + on live nodes. **← doing first**
- [x] **Node notes/comments + boundary groups** — canvas: free-text sticky notes + labeled rectangles
      ("Backend"/"Media"). Persisted on the stack model (new arrays); no codegen impact.
- [x] **Validation: duplicate/dangling checks** — duplicate ports, duplicate resourceNames, edges to
      missing nodes → surfaced as a badge before run.

### P2 — codegen / env
- [x] **.env import** → WithEnvironment rows; secret values → `AddParameter(secret:true)` instead of plaintext.
- [x] **Secrets as parameters** — mark an env value secret → parameter resource, not literal.
- [ ] **Health-check quick setting** — `WithHttpHealthCheck` etc. as a toggle; reflected in live status.
- [x] **Save current stack as template**.
- [ ] **AddProject<T> / project references** from the workspace (real .NET projects, not just containers). Big.
- [ ] **Connection-string explorer** — per resource, show exposed env/connection strings; click to reference.

### P3 — live/runtime
- [ ] **Metrics sparklines** per node (CPU/RAM from docker stats).
- [ ] **Log search/filter + download**; combined multi-resource log.
- [ ] **Container exec/terminal** (docker exec → xterm in drawer). Security note.

### P4 — sharing / appliance prep
- [ ] **Stack diagram export** (PNG/SVG of the canvas).
- [ ] **Stack import via URL/Gist**; read-only share link of a running instance.
- [ ] **App dependency templates** — a preset pulls its companions (Immich → Postgres+Redis+ML). Also the
      springboard for the Appliance/Store mode (see 2026-07-22-appliance-store-mode.md).
- [ ] **Richer preset metadata** — default volumes, required env, needs-GPU/host-net flags.
- [ ] **Command palette can add resources/presets**, not just navigate.

Explicitly NOT doing: GitHub-repo export.
