# AspireUI — Publish / Deploy (Docker Compose) — Design

**Date:** 2026-07-19 (Slice 9). Follows Slice 8 (auth). User priority: "publish/deploy, auch mit
compose yaml einsehen".

## Goal

From a stack, generate real Docker Compose deployment artifacts using Aspire's own compose publisher,
**view the generated `docker-compose.yaml` (+ `.env`) in the tool**, download them as a bundle, and
optionally deploy locally with `docker compose up -d`.

## Why Aspire's publisher (not hand-rolled YAML)

`aspire publish` is installed (CLI 13.1.0) and `Aspire.Hosting.Docker` (13.4.6) is available. A spike
confirmed end-to-end: an AppHost with `builder.AddDockerComposeEnvironment("…")` + the Docker package,
run through `aspire publish --project X -o out --non-interactive`, emits a correct
`out/docker-compose.yaml` + `out/.env` (dashboard service, resource services, networks, parameter env
substitution). Hand-rolling YAML from the model would be wrong for project resources and connection
strings — the publisher is authoritative. So we generate through Aspire.

## Approach

The stored stack is never mutated. Publishing works on a **materialized + augmented copy**:

1. Materialize the stack to `WORKSPACE_DIR/{id}/publish/src` with two augmentations (codegen-driven,
   only when a compose-env name is passed):
   - Program.cs: `builder.AddDockerComposeEnvironment("aspireui");` right after the builder line.
   - csproj: add `<PackageReference Include="Aspire.Hosting.Docker" Version="13.4.6" />`.
2. Run `aspire publish --project <csproj> -o <../out> --non-interactive`, capture combined output.
3. On success read `out/docker-compose.yaml` and `out/.env`; return `{ ok, log, composeYaml, envFile,
   outputDir }`. On failure return `{ ok:false, log, composeYaml:null, envFile:null }`.

`outputDir` persists (under WORKSPACE_DIR) so a later local deploy can `docker compose up -d` there.

## Backend

- `CodeGenService`: `GenerateProgram(s, composeEnv=null)` and `GenerateCsproj(s, composeEnv=null)` gain
  an optional compose-env name; `Materialize(s, dir, composeEnv=null)` threads it through. When null,
  output is byte-identical to today (run/preview/export unaffected).
- `PublishService` (new, injectable command factory like `RunService` for testability):
  - `record PublishResult(bool Ok, string Log, string? ComposeYaml, string? EnvFile, string OutputDir)`.
  - `PublishResult Publish(StackModel s, string publishRoot)`: materialize+augment, run the publish
    command (default = `aspire publish`), read artifacts, return. Synchronous (publish is fast once
    restore is warm); a generous timeout (e.g. 5 min) guards a cold restore.
  - Command factory: `Func<string projectPath, string outputPath, ProcessStartInfo>`; default runs
    `aspire publish --project <csproj> -o <out> --non-interactive`.
- `DeployService` (new, reuse `RunService`'s process pattern): `docker compose up -d` /
  `docker compose down` in the persisted `outputDir`; returns combined output. Injectable factory.
- Endpoints (all on the authenticated `app2` group):
  - `POST /stacks/{id}/publish` → `PublishResult` (404 if stack missing).
  - `POST /stacks/{id}/deploy`  → run `docker compose up -d` in the last publish output; 409 if never
    published. Returns `{ ok, log }`.
  - `POST /stacks/{id}/deploy/down` → `docker compose down`. Returns `{ ok, log }`.

## Frontend

- A **Publish / Deploy** panel (dockview panel, opened from a header button in the editor).
  - "Publish (Docker Compose)" button → `POST …/publish`; show a spinner, then:
    - the publish **log** (monospace, scrollable);
    - the generated **`docker-compose.yaml`** with syntax highlighting (reuse the existing highlighter);
    - the **`.env`** (if non-empty) with a note that parameter values must be filled in;
    - **Copy** + **Download bundle** (jszip: `docker-compose.yaml` + `.env`) buttons;
    - the exact command to run: `docker compose up -d` (with the output path).
  - "Deploy now (docker compose up -d)" button (only after a successful publish) → `POST …/deploy`,
    show output; a "Stop (compose down)" button → `.../deploy/down`. Requires Docker on the host —
    surface the docker-missing case from the returned log.
- api.ts: `publishStack(id)`, `deployStack(id)`, `deployDown(id)` + types.

## Non-Goals (this slice)

- Kubernetes / Azure / cloud publishers (Aspire supports them; out of scope here).
- Reverse-proxy wiring, TLS, remote hosts. `.env` parameter *values* are the user's to fill.
- Streaming publish logs live (publish is short; return the captured log at the end).

## Error handling

- Stack missing → 404. Never published then deploy → 409. Publish failure → `ok:false` + full log
  (surfaced verbatim so the user sees the aspire/dotnet error). Docker missing on deploy → `ok:false`
  with the docker error in the log (no 500).

## Testing

Backend (xUnit, injected command factories — no real aspire/docker in tests):
- `GenerateProgram(s, "aspireui")` contains `AddDockerComposeEnvironment("aspireui")`; with null arg it
  does not (regression: default output unchanged).
- `GenerateCsproj(s, "aspireui")` contains the `Aspire.Hosting.Docker` PackageReference; null → absent.
- `PublishService.Publish` with a stub factory that writes a fake `docker-compose.yaml`/`.env` into the
  output dir and exits 0 → `Ok`, `ComposeYaml`/`EnvFile` read back, and the augmented `src/Program.cs` +
  csproj on disk contain the injections.
- Stub factory exits non-zero → `Ok:false`, log captured.
- Endpoint `POST /stacks/{id}/publish` requires auth (401 without cookie) and 404s an unknown id.

Frontend (build gate + a small pure test if a mapper is added).

## Deferred (BACKLOG)

k8s/Azure publishers, live-streamed publish logs, remote deploy targets, reverse-proxy exposure.
