# AspireUI Publish / Deploy Implementation Plan

> REQUIRED SUB-SKILL: superpowers:subagent-driven-development.

**Goal:** Generate Docker Compose deployment artifacts from a stack via Aspire's own publisher, view the
generated `docker-compose.yaml`/`.env` in the tool, download them, and optionally deploy locally with
`docker compose up -d`.

**Architecture:** Publish works on a materialized+augmented copy of the stack (never mutating the stored
model): inject `AddDockerComposeEnvironment` + the `Aspire.Hosting.Docker` package, run
`aspire publish --non-interactive`, read the emitted artifacts. Services use injectable process-command
factories (same seam as `RunService`) so tests never shell real `aspire`/`docker`.

**Tech Stack:** .NET 10, `aspire` CLI, `Aspire.Hosting.Docker` 13.4.6; React/Mantine/dockview, jszip,
react-syntax-highlighter.

## Global Constraints
- Tool projects net10.0. Aspire version const stays `13.4.6` (CodeGenService.AspireVersion).
- Conventional Commits, NO Co-Authored-By, `git push` after every commit.
- All new app endpoints go on the authenticated group (`app2` in StackEndpoints.cs) — never bare `app`.
- Default (non-publish) codegen output must stay byte-identical: the compose-env param defaults to null.
- Injectable command factories for any process launch (testability) — do not shell real tools in tests.

---

### Task 1: Codegen compose augmentation

**Files:** Modify `src/AspireUI.Server/Services/CodeGenService.cs`; Test
`tests/AspireUI.Server.Tests/CodeGenTests.cs` (add cases; create if absent — check first).

**Interfaces produced:**
- `string GenerateProgram(StackModel s, string? composeEnv = null)`
- `string GenerateCsproj(StackModel s, string? composeEnv = null)`
- `void Materialize(StackModel s, string dir, string? composeEnv = null)`

- [ ] Step 1: Add failing tests: `GenerateProgram(stack, "aspireui")` contains
  `builder.AddDockerComposeEnvironment("aspireui");`; `GenerateProgram(stack)` (null) does NOT contain
  `AddDockerComposeEnvironment`. `GenerateCsproj(stack, "aspireui")` contains
  `Include="Aspire.Hosting.Docker"`; `GenerateCsproj(stack)` does not. Use a minimal stack (one AddRedis
  node) like existing CodeGenTests.
- [ ] Step 2: Run tests, verify fail.
- [ ] Step 3: Implement. In `GenerateProgram`, add param `string? composeEnv = null`; after the
  `sb.AppendLine("var builder = DistributedApplication.CreateBuilder(args);");` line, if
  `composeEnv is not null` append `sb.AppendLine($"builder.AddDockerComposeEnvironment(\"{Escape(composeEnv)}\");");`
  BEFORE the blank line + Begin marker (so it sits outside the aspireui-managed block and round-trip
  import is unaffected). In `GenerateCsproj`, add the param; when non-null, add
  `("Aspire.Hosting.Docker", AspireVersion)` to the emitted package refs (dedupe via the existing
  `resourcePackageIds` set). In `Materialize`, add the param and pass it to both
  `GenerateProgram(s, composeEnv)` and `GenerateCsproj(s, composeEnv)`.
- [ ] Step 4: Run tests, verify pass + the full existing suite stays green.
- [ ] Step 5: Commit `feat: optional docker-compose env augmentation in codegen` + push.

---

### Task 2: PublishService + publish endpoint

**Files:** Create `src/AspireUI.Server/Services/PublishService.cs`; Modify
`src/AspireUI.Server/Endpoints/StackEndpoints.cs`; Test `tests/AspireUI.Server.Tests/PublishTests.cs`.

**Interfaces:**
- Consumes: `CodeGenService.Materialize(s, dir, "aspireui")`.
- Produces:
  - `record PublishResult(bool Ok, string Log, string? ComposeYaml, string? EnvFile, string OutputDir);`
  - `PublishService(Func<string,string,ProcessStartInfo>? commandFactory = null)` where the factory is
    `(projectCsprojPath, outputDir) => ProcessStartInfo`.
  - `PublishResult Publish(StackModel s, string publishRoot)` — materializes to `publishRoot/src`,
    finds the generated `.csproj` there, runs the command with stdout+stderr redirected (combined into
    `Log`), output dir `publishRoot/out`; waits with a 5-min timeout; on exit 0 reads
    `out/docker-compose.yaml` (required for Ok) and `out/.env` (optional, null if absent); returns.
  - Default factory: `FileName="aspire"`, `Arguments=$"publish --project \"{csproj}\" -o \"{outDir}\" --non-interactive"`, `WorkingDirectory` = the src dir.

- [ ] Step 1: Failing tests in PublishTests.cs (xUnit, no ASP.NET host needed — unit-test the service):
  - A stub factory that writes `outDir/docker-compose.yaml` = "services:\n  cache: {}\n" and
    `outDir/.env` = "X=1\n" then runs a trivial exit-0 process (e.g. `cmd /c exit 0` — see note) →
    assert `Ok`, `ComposeYaml` contains "services:", `EnvFile` contains "X=1", and that
    `publishRoot/src/Program.cs` contains `AddDockerComposeEnvironment` and the csproj contains
    `Aspire.Hosting.Docker`.
  - A stub factory whose process exits 1 → `Ok:false`.
  Note for the stub: the factory receives (csproj, outDir); have the STUB itself write the fake
  artifacts into outDir before returning a ProcessStartInfo for `cmd /c exit 0` (Windows) so the read
  step finds them. Keep it OS-simple (this repo runs on win32).
- [ ] Step 2: Run tests, verify fail.
- [ ] Step 3: Implement PublishService per Interfaces. Use `Process` with
  `RedirectStandardOutput/Error=true, UseShellExecute=false, CreateNoWindow=true`; collect lines into a
  list joined into `Log`; `WaitForExit(300_000)`, kill tree on timeout and mark `Ok:false`.
- [ ] Step 4: Wire endpoint in StackEndpoints.cs on `app2`: register a `PublishService` (singleton
  like RunService) and map `POST /stacks/{id}/publish`: load stack (404 if null), compute
  `publishRoot = Path.Combine(wsRoot, id, "publish")`, `Directory.Delete(publishRoot, true)` if exists
  (clean), call `Publish(stack, publishRoot)`, return the result. (wsRoot already computed in the file.)
- [ ] Step 5: Add an endpoint test in PublishTests.cs (or a WebApplicationFactory test) that
  `POST /stacks/{id}/publish` for an unknown id → 404, and (real-auth factory) without cookie → 401.
  Use the existing test factory; you may inject a stub PublishService via the host if practical, else
  assert only the 404/401 wiring (the service logic is covered by the unit tests).
- [ ] Step 6: Run full suite green. Commit `feat: publish stack to docker-compose via aspire` + push.

---

### Task 3: DeployService + deploy endpoints

**Files:** Create `src/AspireUI.Server/Services/DeployService.cs`; Modify `StackEndpoints.cs`; Test
`tests/AspireUI.Server.Tests/PublishTests.cs` (extend) or `DeployTests.cs`.

**Interfaces:**
- `record DeployResult(bool Ok, string Log);`
- `DeployService(Func<string workdir, string args, ProcessStartInfo>? commandFactory = null)` default:
  `FileName="docker"`, `Arguments=args` (e.g. `compose up -d` / `compose down`), `WorkingDirectory=workdir`.
- `DeployResult Up(string outputDir)` → runs `compose up -d`; `DeployResult Down(string outputDir)` →
  `compose down`. Both redirect+capture combined output, `WaitForExit(300_000)`, `Ok = exit==0`.

- [ ] Step 1: Failing tests: stub factory exit-0 → `Ok`, log captured; exit-1 → `Ok:false`. Assert the
  default factory builds `docker compose up -d` args (construct DeployService, expose the psi via the
  factory seam in the test).
- [ ] Step 2: Run, verify fail.
- [ ] Step 3: Implement DeployService (mirror PublishService process handling).
- [ ] Step 4: Endpoints on `app2`: `POST /stacks/{id}/deploy` → `outDir = wsRoot/{id}/publish/out`; if
  not `Directory.Exists(outDir)` or no `docker-compose.yaml` there → 409 (`{message:"publish first"}`);
  else `Up(outDir)` → result. `POST /stacks/{id}/deploy/down` → `Down(outDir)` (409 if outDir missing).
- [ ] Step 5: Test: deploy an un-published id → 409; auth required → 401.
- [ ] Step 6: Full suite green. Commit `feat: local docker-compose deploy/down endpoints` + push.

---

### Task 4: Frontend publish/deploy panel

**Files:** Modify `web/src/api.ts`, `web/src/model.ts` (types); create
`web/src/panels/PublishPanel.tsx` (match existing panel structure — inspect a sibling panel first,
e.g. the run/assist panel and how dockview panels are registered); wire a header button + panel
registration in the editor page. Modify `web/src/App.tsx`/editor only as needed.

**Interfaces consumed:** `PublishResult { ok, log, composeYaml, envFile, outputDir }`,
`DeployResult { ok, log }`.

- [ ] Step 1: api.ts — `publishStack(id): Promise<PublishResult>`, `deployStack(id): Promise<DeployResult>`,
  `deployDown(id): Promise<DeployResult>` (POST via the app `ok()` helper). Add the two result types to
  model.ts.
- [ ] Step 2: PublishPanel.tsx: a "Publish (Docker Compose)" button → `publishStack`; loading state;
  on result show: publish log (monospace `<pre>`/scroll), the `composeYaml` via the existing
  syntax-highlighter component (reuse whatever CodePreview uses; language `yaml`), the `.env` if
  non-empty, Copy button (navigator.clipboard) + Download bundle (jszip: add `docker-compose.yaml` +
  `.env`, save as `{stackName}-compose.zip`), and a code line `docker compose up -d`. After a
  successful publish enable "Deploy now" → `deployStack` (show returned log) and "Stop (compose down)"
  → `deployDown`. Show `ok:false` logs in a clearly-failed style. Follow Mantine usage of sibling panels.
- [ ] Step 3: Register the panel in the editor's dockview setup and add a header button to open it
  (match how the AI assistant / run panels are opened). Inline help: a short `<Tooltip>`/help text
  explaining publish needs the `aspire` CLI and deploy needs Docker on the host.
- [ ] Step 4: `npm run build` clean (strict tsc) + `npm test` green.
- [ ] Step 5: Commit `feat: publish/deploy panel with compose yaml viewer` + push.

---

### Task 5: Docs + real E2E verify

**Files:** Modify `docs/running-and-deploying.md`, `README.md` (a Publish/Deploy line); ledger.

- [ ] Step 1: Document the publish/deploy flow (what it generates, that it uses `aspire publish`, the
  `.env` parameter-filling caveat, docker requirement) in `docs/running-and-deploying.md` + a short
  README mention.
- [ ] Step 2: Real E2E: with the server running, create/import a small stack (e.g. the Redis demo),
  `POST /stacks/{id}/publish`, confirm `ok:true` + `composeYaml` contains `services:` and the resource;
  confirm artifacts exist under `WORKSPACE_DIR/{id}/publish/out`. (Deploy step optional — only if Docker
  is up; otherwise assert the graceful `ok:false`.) Record results.
- [ ] Step 3: `dotnet test` + `npm run build` + `npm test` all green.
- [ ] Step 4: Commit `docs: publish/deploy guide` + push. Update ledger.

## Self-Review
- Coverage: codegen augmentation (T1), publish service+endpoint (T2), deploy service+endpoints (T3),
  frontend panel (T4), docs+E2E (T5). ✔
- Non-breakage: compose-env param defaults null → run/preview/export/import unchanged; new endpoints on
  the authenticated group. ✔
- Security: publish/deploy behind auth; fixed commands, no user input in process args beyond controlled
  paths; stored stack never mutated. ✔
- Types consistent: PublishResult/DeployResult shapes identical across backend records, api.ts, model.ts. ✔
