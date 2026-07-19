# AspireUI — Import (.cs / .csproj / .zip) + Custom Code (Design)

**Date:** 2026-07-19 (Slice 5)
**Builds on:** demo-templates slice. Reference: the Nextended AiStack demo is a realistic import target.

## Goals

1. Import an existing Aspire AppHost into an editable stack from: a **.cs** file, a **.csproj**, or a
   **.zip**.
2. **Real parsing** of an external Program.cs (no aspireui markers): whole-file builder statements,
   including **fluent chains** (`var api = builder.AddProject("api").WithReference(db).WithEnvironment(...)`
   → node + withCalls + edges). Unknown `AddX` (custom extension methods) still become nodes.
   Anything unparseable → raw statements (verbatim, preserved).
3. **Custom code travels with the stack**: extra `.cs` files from the imported project (which define
   custom extension methods / helpers) are carried as `ExtraFiles` and written into the generated
   project so it compiles. Extra NuGet `PackageReference`s from the source `.csproj` are carried as
   `ExtraPackages`.
4. **Folder permission** for single-file `.cs` import: use the browser File System Access API to read
   the sibling folder (find the `.csproj` + extra `.cs` files). ZIP: read entries in-browser (no folder
   prompt). `.csproj`: read its directory.

## Non-Goals (this slice)

- Resolving `ProjectReference`s to sibling source projects on disk (best-effort: map a referenced
  project name to a known NuGet package via the overlay; otherwise record it as an unresolved note).
  Full project-graph import is deferred.
- Editing imported custom code in the UI (it's carried verbatim, shown read-only).
- Settings/AI (Slice 6), auth, deploy, etc. (BACKLOG).

## Model additions

```
record ExtraFile(string Name, string Content);      // extra source file, written next to Program.cs
record PackageRef(string Id, string Version);        // extra NuGet ref merged into the generated csproj
StackModel += List<ExtraFile> ExtraFiles, List<PackageRef> ExtraPackages
```
- `Materialize` writes each ExtraFile into the project dir; `GenerateCsproj` merges ExtraPackages
  (deduped against the ones it already emits from the resource→package map).
- ExtraFiles/ExtraPackages are import-bundle metadata, NOT derived from Program.cs, so they are outside
  the `import(generate(m)) == m` invariant (which stays scoped to nodes/edges/raws). Documented.

## Import parsing (ImportService)

Generalize the existing marker parser:
- Locate the builder statements: if `aspireui:begin/end` markers exist, use that span (as today);
  otherwise parse the whole top-level program (Roslyn `GetCompilationUnitRoot`, all top-level
  statements / the `Main` body).
- **Node from a declaration**: `var v = builder.AddX(<name>, args...)` — even when followed by a
  fluent chain. Walk the invocation chain from the outermost call down to the `builder.AddX` root:
  the root is the node (AddMethod, ResourceName, AddArgs); each `.WithReference(id)` / `.WaitFor(id)`
  in the chain (id = a known var) → edge; each other `.WithY(args)` → a WithCall on the node.
- **Standalone modifications**: `v.WithY(...)`, `v.WithReference(w)`, `v.WaitFor(w)` (as today).
- **Unknown AddX**: any `builder.AddSomething("name")` becomes a node with `AddMethod="AddSomething"`
  even if not in the catalog (palette can't offer it, but it round-trips + generates; its definition
  comes from ExtraFiles / a package).
- **Anything else** inside the parsed span → `RawStatements` verbatim (order preserved).
- Chains whose root is not `builder.AddX` (e.g. `pg.AddDatabase(...)` — a child on another resource):
  treat the whole statement as a raw statement this slice (child-resource modeling is deferred).

## Bundle import endpoint

`POST /stacks/import-bundle` body:
```
{ name: string, files: [{ path: string, content: string }], programPath?: string }
```
Backend:
1. Choose the AppHost `Program.cs`: `programPath` if given, else the file whose content contains
   `DistributedApplication.CreateBuilder`.
2. Parse it via ImportService → nodes/edges/raws.
3. Find the `.csproj` in the bundle → its `PackageReference`s become `ExtraPackages` (skip ones the
   resource→package map already emits; skip `Aspire.Hosting.AppHost`). `ProjectReference`s: if the
   referenced project's name matches a known overlay package id, add that package; else add a raw
   comment note (a RawStatement `// TODO import: unresolved project reference <name>`).
4. All other `.cs` files (not the AppHost Program.cs) → `ExtraFiles` (verbatim).
5. Persist (syntax compile-check + materialize).

`GET`/existing `POST /stacks/import` (the old text-only import) stays for back-compat.

## Frontend

Overview gets an **Import** menu (next to New Stack / demos):
- **From .zip**: `<input type="file" accept=".zip">` → read entries in-browser (JSZip); build the files
  bundle; POST import-bundle.
- **From .cs / .csproj (folder)**: if `window.showDirectoryPicker` exists (Chromium), prompt to pick
  the project folder (this is the "ask to read the folder" step), read all `.cs` + `.csproj` files,
  build the bundle, POST. If the API is unavailable, fall back to a multi-file `<input type="file"
  multiple webkitdirectory>` picker, or a single-file `.cs` import (bundle = just that file; extra
  code/refs unavailable → warn).
- After import → navigate to the editor. Imported custom `ExtraFiles` are listed read-only in a small
  "Custom files" section of the Packages panel (or a dedicated panel).

Dependency: `jszip` (client-side zip reading).

## Error handling

- No AppHost Program.cs found in the bundle → 422 with a clear message.
- Program.cs that Roslyn can't parse at all → 422 with the parse error.
- Files bundle too large → cap total size (e.g. 5 MB) server-side; 413 with message.
- Folder permission denied / API unavailable → frontend falls back to single-file import + a toast
  explaining extensions/refs won't be resolved.
- Imported project with unresolved ProjectReferences → still imports; the note raw-comment + a UI
  warning badge; generated project may not build until the user adds the package (documented).

## Testing

Backend: fluent-chain parse (`var api = builder.AddContainer("web","img").WithHttpEndpoint(8080).WithReference(db)`
→ node with AddArgs + WithCall + reference edge); markerless whole-file parse; unknown AddX → node;
bundle import endpoint (files → stack with ExtraFiles + ExtraPackages, generated csproj includes an
extra package, Materialize writes an extra file); round-trip invariant for nodes/edges/raws still holds
(ExtraFiles excluded). Importing the real AiStack demo Program.cs text → produces ollama/localai/n8n
nodes + the github/pg parts as nodes/raws without crashing.
Frontend: vitest for the zip/folder → bundle assembly (pure function turning a list of {path,content}
into the request payload, picking the AppHost program).

## Deferred (BACKLOG)

ProjectReference source-project import, child-resource nodes (`var db = pg.AddDatabase`), editing custom
code in-UI, settings/AI, auth, deploy, reverse-proxy, semantic compile-check.
