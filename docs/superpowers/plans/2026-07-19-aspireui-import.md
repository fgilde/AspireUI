# AspireUI Import Implementation Plan

> REQUIRED SUB-SKILL: superpowers:subagent-driven-development.

**Goal:** Import an Aspire AppHost from .cs/.csproj/.zip into an editable stack, parsing real (markerless, fluent-chained) Program.cs, carrying custom code + extra packages so it rebuilds.

## Global Constraints
- Tool + generated net10.0; Aspire 13.4.6.
- Serialization of nodes/edges/raws unchanged; `import(generate(m))==m` invariant stays scoped to nodes/edges/raws (ExtraFiles/ExtraPackages excluded).
- Conventional Commits, NO Co-Authored-By, `git push` after every commit.

---

### Task 1: Model ExtraFiles/ExtraPackages + CodeGen

**Files:** `Models/StackModel.cs`, `Services/CodeGenService.cs`; tests `CodeGenTests.cs`.

- `StackModel` += `List<ExtraFile> ExtraFiles, List<PackageRef> ExtraPackages` (LAST two positional params). New records `ExtraFile(string Name, string Content)`, `PackageRef(string Id, string Version)`. Fix all `new StackModel(...)` sites (append `[], []`).
- `Materialize`: after writing Program.cs/csproj, write each `ExtraFile` (relative Name → path under dir; create subdirs; guard against `..` path traversal — skip/normalize names escaping the dir).
- `GenerateCsproj`: merge `ExtraPackages` into the `<ItemGroup>` (dedupe by Id against the resource→package refs already emitted and AppHost).
- Tests: a stack with an ExtraPackage → csproj contains it; a stack with an ExtraFile → Materialize writes it. Round-trip test unchanged (add `[],[]` to fixtures).
- Commit `feat: extra files and packages in stack model` + push.

---

### Task 2: ImportService — markerless whole-file + fluent chains + unknown AddX

**Files:** `Services/ImportService.cs`; tests `ImportTests.cs`.

**Algorithm:**
- Span: if `CodeGenService.Begin`/`End` present → that text span (as today); else the whole compilation unit (all top-level statements).
- For each `LocalDeclarationStatementSyntax` with an `InvocationExpression` initializer: **walk the chain**. Starting at the initializer (outermost call), collect `(methodName, argumentList)` while `inv.Expression is MemberAccessExpressionSyntax ma` and descend `ma.Expression`; stop when the innermost expression is `builder.AddX(...)` (MemberAccess on identifier `builder`) OR an identifier. Reverse collected calls → the FIRST is the `AddX` root (→ node: AddMethod, ResourceName from first string-literal arg, AddArgs from the rest), the REST are chain modifiers applied to that var.
  - If the chain root is `builder.AddX` → node. varName = declared identifier. Register var→nodeId.
  - If the chain root is NOT `builder` (e.g. `pg.AddDatabase(...)`) → whole statement to RawStatements (child resources deferred). 
- For each `ExpressionStatementSyntax` that is an invocation chain on a known var `v` (`v.WithY()...`): apply the same chain-walk; each `.WithReference(id)`/`.WaitFor(id)` with known id → edge (reference/waitFor); each other `.WithY(args)` → WithCall on v's node. If receiver var unknown → RawStatements.
- Chain modifiers in a DECLARATION (Node from `var v = builder.AddX(..).WithY(..).WithReference(w)`): the `.WithY`/`.WithReference`/`.WaitFor` after the root become WithCalls/edges on v, same as standalone.
- Unknown `builder.AddSomething(...)` (AddMethod not in any catalog) → STILL a node (we don't consult the catalog in import; any `builder.AddX` is a node).
- Anything else (other statement kinds, invocations we can't attribute) → RawStatements verbatim, order preserved.
- Keep the existing marker-based path working (it's the same logic now generalized).

**Tests (ImportTests):**
```
FluentChain_ParsesNodeWithChain:
  code (no markers):
    var builder = DistributedApplication.CreateBuilder(args);
    var db = builder.AddPostgres("db");
    var web = builder.AddContainer("web", "nginx").WithHttpEndpoint(8080).WithReference(db);
    builder.Build().Run();
  assert: web node addArgs ["\"nginx\""], a WithCall WithHttpEndpoint args ["8080"], and a reference edge web->db.
Markerless_UnknownAddIsNode:
  var x = builder.AddMyCustomThing("x");  → node addMethod "AddMyCustomThing".
Unparseable_becomesRaw:
  a line like `var expr = ReferenceExpression.Create($"{db.Resource}");` → RawStatements contains it.
Existing round-trip (with markers) still passes.
```
- Commit `feat: import markerless programs, fluent chains, custom adds` + push.

---

### Task 3: Bundle import endpoint

**Files:** `Endpoints/StackEndpoints.cs`, maybe `Services/BundleImporter.cs` (new); tests `ApiTests.cs`.

- `record ImportBundleRequest(string Name, List<BundleFile> Files, string? ProgramPath); record BundleFile(string Path, string Content);`
- `POST /stacks/import-bundle`:
  1. Total content size cap 5 MB → 413 if exceeded.
  2. Pick AppHost program: `ProgramPath` match, else first file whose content contains `DistributedApplication.CreateBuilder`; none → 422 "no AppHost Program.cs found".
  3. `ImportService.Import(newId, Name, programContent, "")` → nodes/edges/raws.
  4. Find `.csproj` (first file ending `.csproj`): parse `<PackageReference Include=.. Version=..>` → ExtraPackages, skipping ids already in the resource→package map values and `Aspire.Hosting.AppHost`. `<ProjectReference>`: base filename without `.csproj` → if it matches a known overlay package id, add that PackageRef (version from overlay/AspireVersion); else append a RawStatement `// TODO import: unresolved project reference <name>`.
  5. Other `.cs` files (not the chosen program) → ExtraFiles (Path→Name, Content).
  6. Set model ExtraFiles/ExtraPackages; Persist.
- Test: bundle with a Program.cs (`builder.AddRedis("cache");`), a csproj with `<PackageReference Include="Some.Pkg" Version="1.2.3"/>`, and an extra `Helpers.cs` → resulting stack has node cache, ExtraPackages contains Some.Pkg@1.2.3, ExtraFiles contains Helpers.cs; generated csproj includes Some.Pkg; Materialize writes Helpers.cs. Missing-program bundle → 422.
- Commit `feat: bundle import endpoint (.cs/.csproj/.zip source)` + push.

---

### Task 4: Frontend import UI

**Files:** `web/package.json` (+jszip), `web/src/api.ts`, `web/src/model.ts` (Stack extraFiles/extraPackages types + a pure `buildBundle` helper), `web/src/model.test.ts`, `web/src/pages/StacksOverview.tsx`, maybe `web/src/importBundle.ts`.

- `npm i jszip`.
- model.ts: `Stack` += `extraFiles: {name,content}[]`, `extraPackages: {id,version}[]`; include `[]` in client-side creates. Pure helper `pickAppHost(files: {path,content}[]): string | undefined` (returns path of the file containing `DistributedApplication.CreateBuilder`) + vitest.
- api.ts: `importBundle(name, files, programPath?)` → POST /stacks/import-bundle.
- Overview Import menu (Mantine Menu) with items:
  - **ZIP**: hidden `<input type=file accept=".zip">`; on pick, JSZip loads it, read every `.cs`/`.csproj` entry to `{path,content}`; call importBundle; navigate.
  - **Folder (.cs/.csproj)**: if `window.showDirectoryPicker` → prompt folder (the "ask to read folder" step), recursively read `.cs`/`.csproj` files to the bundle; else fallback `<input type=file multiple>` (or webkitdirectory) and read selected files; build bundle; import; navigate. Toast if falling back that references may be incomplete.
  - name = derived from zip/folder name.
- Editor: show imported `stack.extraFiles` names read-only in the Packages panel (a "Custom files" subsection) — small addition.
- Gates: `npm run build` clean + `npm test` green.
- Commit `feat: import UI (zip, folder, file) with bundle assembly` + push.

---

### Task 5: E2E verify
- `dotnet test` + frontend build/test green.
- Live: build a small bundle via curl (Program.cs with AddRedis + a csproj with an extra package + a Helpers.cs) → import-bundle → GET stack → preview shows AddRedis; GET workspace shows Helpers.cs written + csproj has the extra package. Then import the REAL demo: read `C:\dev\privat\github\Nextended\Tests\TestProjects\AiStack.AppHost\Program.cs` as the program (plus its csproj) via curl bundle → confirm it imports without crashing, nodes for ollama/localai/n8n/AddGithubRepository present, the pg/AddDatabase + ReferenceExpression parts land as raws, no 500. Report the resulting preview.
- Fix issues in small pushed commits; report.

## Self-Review
- Coverage: model extras (T1), real parse + chains + custom adds (T2), bundle endpoint + csproj/extra-files (T3), import UI zip/folder + folder-permission (T4), verify incl. real demo (T5). ✔
- Round-trip invariant preserved for nodes/edges/raws; ExtraFiles/ExtraPackages excluded (documented). ✔
- Consistency: `ExtraFile`/`PackageRef` (C#) ↔ `extraFiles`/`extraPackages` (TS); `pickAppHost` mirrors backend AppHost selection. ✔
- Risk: chain-walk correctness (the crux) — strong tests cover it; child-resource chains (`pg.AddDatabase`) intentionally become raws this slice.
