# Importing

Bring an existing Aspire AppHost into AspireUI as an editable stack, from a **.cs** file, a
**.csproj**, or a **.zip** of the project — or convert a **docker-compose.yml** into an Aspire stack.

Use the **Import** menu on the Stacks overview (next to New Stack / demos).

## Docker Compose → Aspire

The Import menu's **docker-compose.yml** option converts a Compose file into a stack of
`AddContainer(...)` nodes. AspireUI parses the YAML and maps:

- each **service** → an `AddContainer("name", "image")` node,
- **ports** → `WithHttpEndpoint(port:, targetPort:)`,
- **environment** (list or map form) → `WithEnvironment(...)`,
- **volumes** → `WithBindMount(...)` (host path) or `WithVolume(...)` (named),
- **command** → `WithArgs(...)`,
- **depends_on** → `WaitFor` edges between the resulting nodes.

It's a starting point, not a perfect 1:1 translation — review the generated stack and adjust (Aspire
models some things differently, and Compose features without an Aspire equivalent are skipped). From
there it's a normal editable stack.

## What gets parsed

AspireUI reads the AppHost's `Program.cs` (whichever file contains
`DistributedApplication.CreateBuilder`, or the one you pick) and understands:

- **Node declarations** — `var api = builder.AddProject("api")`, including a fluent chain after it
  (`.WithReference(db).WithEnvironment(...)`): the root `AddX` call becomes the node, each
  `.WithReference(x)` / `.WaitFor(x)` in the chain becomes a reference/wait-for edge, everything else
  in the chain becomes a capability call on the node.
- **Standalone statements** on a variable — `api.WithReference(db)`, `api.WaitFor(db)`, etc.
- **Unknown `AddX` calls** — a custom or unrecognized resource still becomes a node (with whatever
  `AddMethod` name it used) even though the palette can't offer it directly; it still round-trips
  and generates correctly, as long as its definition ships with the project (see below).
- **Anything else** in the file (helper variables, expression statements the parser doesn't
  recognize) is preserved verbatim as a raw statement, in order — nothing you don't recognize gets
  silently dropped.

Child-resource statements (e.g. `var db = pg.AddDatabase("db")`, a call rooted on another resource
rather than `builder`) are currently kept as raw statements rather than becoming their own modeled
node.

## Custom code and extra packages

If the imported project has its own `.cs` files (defining custom extension methods the AppHost
calls) and extra NuGet references in its `.csproj`, those come along for the ride:

- Extra source files are carried with the stack and written back out next to `Program.cs` when the
  stack is run or exported, so the project still compiles.
- Extra `PackageReference`s from the source `.csproj` are merged into the generated project (skipping
  ones AspireUI already adds itself). A `ProjectReference` to a sibling project is resolved to a
  known NuGet package where possible; otherwise it's recorded as a `// TODO import: unresolved
  project reference` comment so you know to add it by hand.
- Imported custom files show up read-only in the Packages panel's "Custom files" section — they
  travel with the stack but aren't editable in the UI.

## Folder access for `.cs` imports

A single `.cs` file usually isn't the whole picture — its `.csproj` and any helper files live next
to it. Importing:

- **.zip** — read entirely in the browser (no folder permission needed); every file in the archive
  is available for parsing.
- **.csproj** — AspireUI reads its containing folder.
- **.cs** — if your browser supports it (Chromium-based), you'll be prompted to grant folder access
  (`showDirectoryPicker`) so AspireUI can also pick up the sibling `.csproj` and extra `.cs` files.
  If that API isn't available, it falls back to a multi-file folder picker, or — as a last resort —
  a single-file import with a warning that extra code and package references won't be resolved.

After a successful import you land straight in the editor with the imported stack.
