# AspireUI Tool UX + Run/Stop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax. Frontend tasks: also apply superpowers:frontend-design for the look.

**Goal:** Turn the canvas-to-code core into a usable tool — stacks overview, schema-driven property grid, reference picker, live C# preview, run/stop with Aspire dashboard, Mantine look.

**Architecture:** ASP.NET Core (net10.0) backend adds a catalog parameter schema, `AddArgs` on the node model, a preview endpoint, and a `RunService` (process lifecycle). React SPA rebuilt on Mantine + react-router: overview route + editor route with palette, custom-node canvas, tabbed property/reference panel, and code preview.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, Roslyn, Microsoft.Data.Sqlite, xUnit; React 18 + TS + Vite, @xyflow/react, **Mantine v7** (+ @tabler/icons-react), react-router-dom, Vitest.

## Global Constraints

- Tool projects target `net10.0`.
- **Generated stacks now target `net10.0`** (was net9.0) and use **Aspire 13.x** packages matching the installed `Aspire.Hosting.*` (Redis resolved as 13.4.6) so `dotnet run` builds on the only installed runtime. Generated csproj sets `<ImplicitUsings>enable</ImplicitUsings>`; generated Program.cs keeps `using Aspire.Hosting;` and the `aspireui:begin/end` marker block.
- `WithCalls` + new `AddArgs` remain the canonical serialization — do NOT rewrite CodeGen/Import to a structured-config model. The property grid is a typed editor over them.
- Catalog parameter schemas come from the JSON **overlay** (curated); reflection still only discovers which `AddX` resources exist. (Simplification vs spec's "reflection supplies types": overload/optional-param reflection is not worth it now; typed editors are overlay-driven, generic resources get name + raw-call escape hatch.)
- Commit style: Conventional Commits, **no `Co-Authored-By` footer**, and **`git push` after every commit**.
- The SPA build stays Release-conditioned (Debug/test builds must not run npm).

---

## File Structure

```
src/AspireUI.Server/
  Models/StackModel.cs            + AddArgs on NodeModel
  Services/CatalogService.cs      enriched records (CatalogParam, CatalogWith.Params, ResourceType.AddParams)
  Services/CodeGenService.cs      emit AddArgs; Aspire 13.x + net10.0 csproj
  Services/ImportService.cs       capture AddArgs
  Services/RunService.cs          NEW process lifecycle + dashboard-url parser
  Endpoints/StackEndpoints.cs     + preview/run/stop/status routes, register RunService
  catalog/aspire-hosting.json     expanded param schemas
tests/AspireUI.Server.Tests/
  CatalogTests.cs                 + param-schema assertions
  CodeGenTests.cs                 + AddArgs
  ImportTests.cs                  + AddArgs round-trip
  RunServiceTests.cs              NEW (dummy command + url parser)
  ApiTests.cs                     + preview
web/
  package.json                    + mantine, @tabler/icons-react, react-router-dom
  src/main.tsx                    MantineProvider + Router
  src/model.ts                    + addArgs; catalog types; config<->serialization transform
  src/model.test.ts               + transform tests
  src/api.ts                      + preview/run/stop/status
  src/theme.ts                    Mantine theme
  src/pages/StacksOverview.tsx    NEW route "/"
  src/pages/Editor.tsx            route "/stacks/:id" (was App)
  src/editor/Palette.tsx          grouped + search
  src/editor/Canvas.tsx           custom node cards
  src/editor/PropertyPanel.tsx    tabs: Properties + References
  src/editor/PropertyGrid.tsx     schema-driven fields
  src/editor/CodePreview.tsx      read-only C#
  src/editor/RunToolbar.tsx       run/stop/status/dashboard
  src/App.tsx                     <Routes>
```

---

### Task 1: NodeModel.AddArgs + CodeGen/Import + generated-project modernization

**Files:**
- Modify: `src/AspireUI.Server/Models/StackModel.cs`, `Services/CodeGenService.cs`, `Services/ImportService.cs`
- Test: `tests/AspireUI.Server.Tests/CodeGenTests.cs`, `ImportTests.cs`

**Interfaces:**
- `NodeModel` gains `List<string> AddArgs` as the LAST positional record parameter.
- CodeGen emits: `var {var} = builder.{AddMethod}("{name}"{, addArg, ...});` (AddArgs are raw C# literals already quoted where needed).
- Import captures `AddArgs` = the invocation args after the first (name) arg, as their source text.

- [ ] **Step 1: Update NodeModel** — add `AddArgs` (append, keep existing params/order):

```csharp
public record NodeModel(
    string Id,
    string VarName,
    string AddMethod,
    string ResourceName,
    List<WithCall> WithCalls,
    double X,
    double Y,
    List<string> AddArgs);   // positional args after ResourceName, raw C# literals e.g. "\"nginx\""
```

Fix existing constructions: `StackStoreTests`, `CodeGenTests`, `ImportTests`, `ApiTests` build `NodeModel(...)`. Because `AddArgs` is a new required positional param, update every existing `new NodeModel(...)` in tests to pass a final `[]`. (Do this as part of Steps below where those files are edited; for StackStoreTests add the trailing `[]`.)

- [ ] **Step 2: Update failing CodeGen test** — extend `CodeGenTests.cs` fixture + add an AddArgs assertion:

Replace the fixture and add a test:
```csharp
    private static StackModel Fixture() => new("s1", "Demo", "net10.0",
        [
            new NodeModel("n1", "db", "AddPostgres", "db", [new WithCall("WithDataVolume", [])], 0, 0, []),
            new NodeModel("n2", "cache", "AddRedis", "cache", [], 0, 0, []),
            new NodeModel("n3", "web", "AddContainer", "web", [], 0, 0, ["\"nginx\""])
        ],
        [new EdgeModel("e1", "n1", "n2", "reference")]);

    [Fact]
    public void Generate_EmitsAddArgs()
    {
        var code = new CodeGenService().GenerateProgram(Fixture());
        Assert.Contains("var web = builder.AddContainer(\"web\", \"nginx\");", code);
    }
```
(Keep the existing `Generate_EmitsMarkerBlockInCanonicalOrder` and `Materialize_WritesFilesAndSidecar` tests; update their expectations only if the `net9.0`→`net10.0` fixture value matters — they don't assert TFM, so leaving them is fine. Update the fixture references in those tests to the new one.)

- [ ] **Step 3: Run — expect FAIL** (`AddArgs` not defined / compile errors)

Run: `dotnet test tests/AspireUI.Server.Tests --filter CodeGenTests`
Expected: FAIL to compile.

- [ ] **Step 4: Implement CodeGen changes** in `CodeGenService.cs`:

Change the declaration emit line inside `GenerateProgram`:
```csharp
        foreach (var n in s.Nodes)
        {
            var args = new List<string> { $"\"{Escape(n.ResourceName)}\"" };
            args.AddRange(n.AddArgs);
            sb.AppendLine($"var {n.VarName} = builder.{n.AddMethod}({string.Join(", ", args)});");
        }
```
Add the escape helper (also fixes the earlier unescaped-name note):
```csharp
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
```
Modernize `GenerateCsproj` — Aspire 13.x + net10.0 + implicit usings:
```csharp
    public string GenerateCsproj(StackModel s) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <Sdk Name="Aspire.AppHost.Sdk" Version="13.4.6" />
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>{s.TargetFramework}</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <IsAspireHost>true</IsAspireHost>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Aspire.Hosting.AppHost" Version="13.4.6" />
          </ItemGroup>
        </Project>
        """;
```
(If `Aspire.AppHost.Sdk`/`Aspire.Hosting.AppHost` 13.4.6 is not the resolvable version, use the version that the server project actually restored — check `src/AspireUI.Server/AspireUI.Server.csproj`. Keep server and generated versions consistent. Document the version used.)

Ensure `GenerateProgram` still starts with `using Aspire.Hosting;` then the `var builder = ...` line and the marker block.

- [ ] **Step 5: Run — expect PASS** (CodeGenTests)

Run: `dotnet test tests/AspireUI.Server.Tests --filter CodeGenTests`
Expected: PASS.

- [ ] **Step 6: Update Import to capture AddArgs** — in `ImportService.cs`, Pass 1 declaration loop, after computing `resourceName`:

```csharp
            var addArgs = inv.ArgumentList.Arguments.Skip(1).Select(a => a.Expression.ToString()).ToList();
            var nodeId = "n" + (++nId);
            varToNodeId[varName] = nodeId;
            nodes.Add(new NodeModel(nodeId, varName, addMethod, resourceName, [], 0, 0, addArgs));
```
(The `with { WithCalls = ... }` and position-restore lines already use `with`, so they carry AddArgs through unchanged.)

- [ ] **Step 7: Extend round-trip test** — in `ImportTests.cs`, update the fixture to include the container node with AddArgs and assert AddArgs survive:

Update `Fixture()` to match Task-1 CodeGen fixture (three nodes incl. `web`/AddContainer/`["\"nginx\""]`, TFM `net10.0`), and extend the node `Key` to include AddArgs:
```csharp
        string Key(NodeModel n) => $"{n.VarName}|{n.AddMethod}|{n.ResourceName}|" +
            string.Join(",", n.AddArgs) + "|" +
            string.Join(",", n.WithCalls.Select(w => w.Method + "(" + string.Join(";", w.Args) + ")"));
```

- [ ] **Step 8: Run — expect PASS** (ImportTests + full suite)

Run: `dotnet test`
Expected: all PASS (update any remaining `new NodeModel(...)` in StackStoreTests/ApiTests with trailing `[]` if the build complains).

- [ ] **Step 9: Commit + push**

```bash
git add src/AspireUI.Server tests/AspireUI.Server.Tests
git commit -m "feat: node AddArgs, escaped names, modern generated project (net10/Aspire13)"
git push
```

---

### Task 2: Catalog parameter schema

**Files:**
- Modify: `src/AspireUI.Server/Services/CatalogService.cs`, `catalog/aspire-hosting.json`
- Test: `tests/AspireUI.Server.Tests/CatalogTests.cs`

**Interfaces (later tasks + frontend depend on these):**
```csharp
public record CatalogParam(string Name, string Type, bool Required, string? Default, List<string>? Options, string Label);
public record CatalogWith(string Method, string Label, List<CatalogParam> Params);
public record ResourceType(string AddMethod, string Label, string? Icon, string? Group, List<CatalogParam> AddParams, List<CatalogWith> Withs);
```
`Type` ∈ `"string" | "int" | "bool" | "enum"`.

- [ ] **Step 1: Expand the overlay** `catalog/aspire-hosting.json` to the new shape:

```json
{
  "AddContainer": {
    "label": "Container", "icon": "docker", "group": "Generic",
    "addParams": [
      { "name": "image", "type": "string", "required": true, "label": "Image" },
      { "name": "tag", "type": "string", "required": false, "label": "Tag" }
    ],
    "withs": [
      { "method": "WithHttpEndpoint", "label": "HTTP Endpoint", "params": [
        { "name": "port", "type": "int", "label": "Port" },
        { "name": "targetPort", "type": "int", "label": "Target Port" } ] },
      { "method": "WithEnvironment", "label": "Env Var", "params": [
        { "name": "name", "type": "string", "label": "Name" },
        { "name": "value", "type": "string", "label": "Value" } ] },
      { "method": "WithVolume", "label": "Volume", "params": [
        { "name": "name", "type": "string", "label": "Name" },
        { "name": "target", "type": "string", "label": "Target Path" } ] },
      { "method": "WithBindMount", "label": "Bind Mount", "params": [
        { "name": "source", "type": "string", "label": "Source" },
        { "name": "target", "type": "string", "label": "Target" } ] }
    ]
  },
  "AddPostgres": {
    "label": "Postgres", "icon": "postgres", "group": "Database",
    "addParams": [],
    "withs": [
      { "method": "WithDataVolume", "label": "Data Volume", "params": [] },
      { "method": "WithPgAdmin", "label": "pgAdmin", "params": [] },
      { "method": "WithEnvironment", "label": "Env Var", "params": [
        { "name": "name", "type": "string", "label": "Name" },
        { "name": "value", "type": "string", "label": "Value" } ] }
    ]
  },
  "AddRedis": {
    "label": "Redis", "icon": "redis", "group": "Cache",
    "addParams": [],
    "withs": [
      { "method": "WithDataVolume", "label": "Data Volume", "params": [] },
      { "method": "WithRedisCommander", "label": "Redis Commander", "params": [] }
    ]
  }
}
```

- [ ] **Step 2: Update failing test** — `CatalogTests.cs`, add:

```csharp
    [Fact]
    public void Container_HasImageParam_AndTypedWith()
    {
        var asm = typeof(Aspire.Hosting.RedisBuilderExtensions).Assembly;
        var catalog = new CatalogService(asm).GetCatalog();
        var container = catalog.First(r => r.AddMethod == "AddContainer");
        Assert.Contains(container.AddParams, p => p.Name == "image" && p.Type == "string" && p.Required);
        var httpWith = container.Withs.First(w => w.Method == "WithHttpEndpoint");
        Assert.Contains(httpWith.Params, p => p.Name == "port" && p.Type == "int");
    }
```
Note: `AddContainer` lives in the core `Aspire.Hosting` assembly which is force-loaded by `LoadDefault()`; the default `new CatalogService()` (no args, used by the endpoint) discovers it. For this test, construct with the Redis assembly (force-loads its refs) OR use `new CatalogService()` — use `new CatalogService()` so the container (core) resource is present. Adjust: `var catalog = new CatalogService().GetCatalog();`.

- [ ] **Step 3: Run — expect FAIL**

Run: `dotnet test tests/AspireUI.Server.Tests --filter CatalogTests`
Expected: FAIL (records don't have AddParams; overlay parse differs).

- [ ] **Step 4: Rewrite the records + overlay parsing** in `CatalogService.cs`.

Replace the record definitions (top of file) with the three records from **Interfaces** above. Replace the overlay-consuming part of `GetCatalog()`:
```csharp
            var over = _overlay.TryGetValue(m.Name, out var o) ? o : (JsonElement?)null;
            result.Add(new ResourceType(
                m.Name,
                over?.TryGetProperty("label", out var lbl) == true ? lbl.GetString()! : m.Name[3..],
                over?.TryGetProperty("icon", out var i) == true ? i.GetString() : null,
                over?.TryGetProperty("group", out var g) == true ? g.GetString() : "Other",
                ParseParams(over, "addParams"),
                ParseWiths(over)));
```
Add helpers:
```csharp
    private static List<CatalogParam> ParseParams(JsonElement? over, string prop)
    {
        var list = new List<CatalogParam>();
        if (over?.TryGetProperty(prop, out var arr) == true)
            foreach (var p in arr.EnumerateArray()) list.Add(ReadParam(p));
        return list;
    }

    private static CatalogParam ReadParam(JsonElement p) => new(
        p.GetProperty("name").GetString()!,
        p.TryGetProperty("type", out var t) ? t.GetString()! : "string",
        p.TryGetProperty("required", out var r) && r.GetBoolean(),
        p.TryGetProperty("default", out var d) ? d.GetString() : null,
        p.TryGetProperty("options", out var o) ? o.EnumerateArray().Select(x => x.GetString()!).ToList() : null,
        p.TryGetProperty("label", out var l) ? l.GetString()! : p.GetProperty("name").GetString()!);

    private static List<CatalogWith> ParseWiths(JsonElement? over)
    {
        var list = new List<CatalogWith>();
        if (over?.TryGetProperty("withs", out var arr) == true)
            foreach (var w in arr.EnumerateArray())
                list.Add(new CatalogWith(
                    w.GetProperty("method").GetString()!,
                    w.TryGetProperty("label", out var l) ? l.GetString()! : w.GetProperty("method").GetString()!,
                    w.TryGetProperty("params", out var ps) ? ps.EnumerateArray().Select(ReadParam).ToList() : []));
        return list;
    }
```
Remove the old `CatalogWith(string Method, List<string> Params)` usages.

- [ ] **Step 5: Run — expect PASS** (CatalogTests + full suite)

Run: `dotnet test`
Expected: PASS (the existing `Reflection_FindsAddRedis` still passes; ApiTests `ResourceTypeDto(AddMethod,Label)` still binds — extra JSON fields are ignored).

- [ ] **Step 6: Commit + push**

```bash
git add src/AspireUI.Server tests/AspireUI.Server.Tests
git commit -m "feat: catalog parameter schemas for property grid"
git push
```

---

### Task 3: Code preview endpoint

**Files:**
- Modify: `src/AspireUI.Server/Endpoints/StackEndpoints.cs`
- Test: `tests/AspireUI.Server.Tests/ApiTests.cs`

- [ ] **Step 1: Add failing test** to `ApiTests.cs`:

```csharp
    [Fact]
    public async Task Preview_ReturnsGeneratedCode()
    {
        var create = await _c.PostAsJsonAsync("/stacks",
            new StackModel("", "PrevStack", "net10.0", [], []));
        var created = await create.Content.ReadFromJsonAsync<StackModel>();
        var code = await _c.GetStringAsync($"/stacks/{created!.Id}/preview");
        Assert.Contains("DistributedApplication.CreateBuilder", code);
        Assert.Contains("aspireui:begin", code);
    }
```

- [ ] **Step 2: Run — expect FAIL** (404)

Run: `dotnet test tests/AspireUI.Server.Tests --filter Preview_ReturnsGeneratedCode`
Expected: FAIL.

- [ ] **Step 3: Add the route** in `StackEndpoints.cs` (after the export route):

```csharp
        app.MapGet("/stacks/{id}/preview", (string id) =>
            store.Get(id) is { } s ? Results.Text(gen.GenerateProgram(s), "text/plain") : Results.NotFound());
```

- [ ] **Step 4: Run — expect PASS**; commit + push

```bash
git add src/AspireUI.Server tests/AspireUI.Server.Tests
git commit -m "feat: code preview endpoint"
git push
```

---

### Task 4: RunService + run/stop/status endpoints

**Files:**
- Create: `src/AspireUI.Server/Services/RunService.cs`
- Modify: `src/AspireUI.Server/Endpoints/StackEndpoints.cs`
- Test: `tests/AspireUI.Server.Tests/RunServiceTests.cs`

**Interfaces:**
```csharp
public enum RunState { NotRunning, Starting, Running, Failed }
public record RunStatus(RunState State, string? DashboardUrl, List<string> Log);
public class RunService {
    public RunService(Func<string, System.Diagnostics.ProcessStartInfo>? commandFactory = null) { }
    public RunStatus Start(string id, string workdir);   // idempotent if already running
    public RunStatus Stop(string id);
    public RunStatus Status(string id);
    public static string? ParseDashboardUrl(string line);
}
```
`ParseDashboardUrl` matches an Aspire dashboard login URL. `commandFactory(workdir)` builds the process start info; default runs `dotnet run --project <workdir>`. Tests inject a fast dummy command.

- [ ] **Step 1: Write failing tests** `RunServiceTests.cs`:

```csharp
using AspireUI.Server.Services;

public class RunServiceTests
{
    [Fact]
    public void ParseDashboardUrl_ExtractsLoginUrl()
    {
        var line = "Login to the dashboard at https://localhost:17123/login?t=abc123def";
        Assert.Equal("https://localhost:17123/login?t=abc123def", RunService.ParseDashboardUrl(line));
        Assert.Null(RunService.ParseDashboardUrl("nothing here"));
    }

    [Fact]
    public void Lifecycle_StartRunsThenStop()
    {
        // Dummy command that prints a dashboard line then sleeps, cross-platform via dotnet fsi is heavy;
        // use a shell that echoes the URL and stays alive.
        var svc = new RunService(_ =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo();
            if (OperatingSystem.IsWindows())
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c echo Login to the dashboard at https://localhost:18888/login?t=tok && ping -n 30 127.0.0.1 > NUL";
            }
            else
            {
                psi.FileName = "/bin/sh";
                psi.Arguments = "-c \"echo Login to the dashboard at https://localhost:18888/login?t=tok; sleep 30\"";
            }
            return psi;
        });

        svc.Start("s1", ".");
        // poll up to ~5s for the dashboard url to be parsed
        RunStatus st = svc.Status("s1");
        for (int i = 0; i < 50 && st.DashboardUrl is null; i++) { System.Threading.Thread.Sleep(100); st = svc.Status("s1"); }
        Assert.Equal("https://localhost:18888/login?t=tok", st.DashboardUrl);
        Assert.Equal(RunState.Running, st.State);

        var stopped = svc.Stop("s1");
        Assert.Equal(RunState.NotRunning, stopped.State);
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test tests/AspireUI.Server.Tests --filter RunServiceTests`
Expected: FAIL (RunService missing).

- [ ] **Step 3: Implement RunService.cs**

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AspireUI.Server.Services;

public enum RunState { NotRunning, Starting, Running, Failed }
public record RunStatus(RunState State, string? DashboardUrl, List<string> Log);

public class RunService : IDisposable
{
    private static readonly Regex DashboardRx =
        new(@"https?://localhost:\d+/login\?t=\S+", RegexOptions.Compiled);

    private class Handle
    {
        public Process Process = default!;
        public RunState State = RunState.Starting;
        public string? DashboardUrl;
        public readonly List<string> Log = new();
    }

    private readonly ConcurrentDictionary<string, Handle> _runs = new();
    private readonly Func<string, ProcessStartInfo> _commandFactory;

    public RunService(Func<string, ProcessStartInfo>? commandFactory = null)
        => _commandFactory = commandFactory ?? DefaultCommand;

    private static ProcessStartInfo DefaultCommand(string workdir) => new()
    {
        FileName = "dotnet",
        Arguments = $"run --project \"{workdir}\"",
        WorkingDirectory = workdir,
    };

    public static string? ParseDashboardUrl(string line)
    {
        var m = DashboardRx.Match(line);
        return m.Success ? m.Value : null;
    }

    public RunStatus Start(string id, string workdir)
    {
        if (_runs.TryGetValue(id, out var existing) &&
            existing.State is RunState.Running or RunState.Starting)
            return Snapshot(existing);

        var psi = _commandFactory(workdir);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        var h = new Handle();
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        h.Process = proc;

        void OnLine(string? line)
        {
            if (line is null) return;
            lock (h.Log)
            {
                h.Log.Add(line);
                if (h.Log.Count > 200) h.Log.RemoveAt(0);
            }
            var url = ParseDashboardUrl(line);
            if (url is not null) { h.DashboardUrl = url; h.State = RunState.Running; }
        }
        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
        proc.Exited += (_, _) =>
        {
            if (h.State != RunState.Running) h.State = proc.ExitCode == 0 ? RunState.NotRunning : RunState.Failed;
        };

        _runs[id] = h;
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return Snapshot(h);
    }

    public RunStatus Stop(string id)
    {
        if (_runs.TryRemove(id, out var h))
        {
            try { if (!h.Process.HasExited) h.Process.Kill(entireProcessTree: true); } catch { }
            h.State = RunState.NotRunning;
        }
        return new RunStatus(RunState.NotRunning, null, h?.Log ?? new());
    }

    public RunStatus Status(string id) =>
        _runs.TryGetValue(id, out var h) ? Snapshot(h) : new RunStatus(RunState.NotRunning, null, new());

    private static RunStatus Snapshot(Handle h)
    {
        lock (h.Log) return new RunStatus(h.State, h.DashboardUrl, new List<string>(h.Log));
    }

    public void Dispose()
    {
        foreach (var h in _runs.Values)
            try { if (!h.Process.HasExited) h.Process.Kill(entireProcessTree: true); } catch { }
    }
}
```
`ponytail:` status is polled, log capped at 200 lines, single process per stack. Upgrade to streamed logs / multi-instance if needed later.

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test tests/AspireUI.Server.Tests --filter RunServiceTests`
Expected: PASS (2 tests). If the dummy process on the CI/host can't spawn cmd/sh, adjust the dummy to a `dotnet` no-op; keep the URL-parse test independent.

- [ ] **Step 5: Wire endpoints** in `StackEndpoints.cs`. Add a singleton RunService at top of `MapStackEndpoints`:
```csharp
        var run = new RunService();
```
Add routes (after preview):
```csharp
        app.MapPost("/stacks/{id}/run", (string id) =>
            Directory.Exists(Dir(id)) ? Results.Ok(run.Start(id, Path.GetFullPath(Dir(id)))) : Results.NotFound());
        app.MapPost("/stacks/{id}/stop", (string id) => Results.Ok(run.Stop(id)));
        app.MapGet("/stacks/{id}/status", (string id) => Results.Ok(run.Status(id)));
```

- [ ] **Step 6: Full suite + commit + push**

Run: `dotnet test` → all green.
```bash
git add src/AspireUI.Server tests/AspireUI.Server.Tests
git commit -m "feat: RunService and run/stop/status endpoints"
git push
```

---

### Task 5: Frontend — Mantine + router shell + Stacks overview

**Files:**
- Modify: `web/package.json` (deps), `web/src/main.tsx`, `web/src/App.tsx`
- Create: `web/src/theme.ts`, `web/src/pages/StacksOverview.tsx`
- Modify: `web/src/api.ts` (add preview/run/stop/status), `web/src/model.ts` (types)

**Apply superpowers:frontend-design for the look.**

- [ ] **Step 1: Add deps**

Run: `cd web && npm i @mantine/core @mantine/hooks @tabler/icons-react react-router-dom && cd ..`

- [ ] **Step 2: Extend model + api**

`web/src/model.ts` — add to the Node interface `addArgs: string[]`, and add catalog + run types:
```ts
export interface Node { id: string; varName: string; addMethod: string; resourceName: string; withCalls: WithCall[]; x: number; y: number; addArgs: string[] }
export interface CatalogParam { name: string; type: "string" | "int" | "bool" | "enum"; required: boolean; default?: string | null; options?: string[] | null; label: string }
export interface CatalogWith { method: string; label: string; params: CatalogParam[] }
export interface ResourceType { addMethod: string; label: string; icon?: string | null; group?: string | null; addParams: CatalogParam[]; withs: CatalogWith[] }
export type RunState = "NotRunning" | "Starting" | "Running" | "Failed";
export interface RunStatus { state: RunState; dashboardUrl?: string | null; log: string[] }
```
Update the existing `toFlow` usage of nodes to include addArgs is not needed (toFlow reads x/y/labels only).

`web/src/api.ts` — append:
```ts
export const previewStack = (id: string): Promise<string> => fetch(`${base}/stacks/${id}/preview`).then(r => r.text());
export const deleteStack = (id: string): Promise<void> => fetch(`${base}/stacks/${id}`, { method: "DELETE" }).then(() => undefined);
export const runStack = (id: string) => fetch(`${base}/stacks/${id}/run`, { method: "POST" }).then(ok);
export const stopStack = (id: string) => fetch(`${base}/stacks/${id}/stop`, { method: "POST" }).then(ok);
export const statusStack = (id: string) => fetch(`${base}/stacks/${id}/status`).then(ok);
```

- [ ] **Step 3: Theme + providers** — `web/src/theme.ts`:
```ts
import { createTheme } from "@mantine/core";
export const theme = createTheme({ primaryColor: "indigo", defaultRadius: "md" });
```
`web/src/main.tsx`:
```tsx
import React from "react";
import ReactDOM from "react-dom/client";
import { MantineProvider } from "@mantine/core";
import { BrowserRouter } from "react-router-dom";
import "@mantine/core/styles.css";
import { theme } from "./theme";
import App from "./App";

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <MantineProvider theme={theme} defaultColorScheme="dark">
      <BrowserRouter>
        <App />
      </BrowserRouter>
    </MantineProvider>
  </React.StrictMode>
);
```

- [ ] **Step 4: Routes** — `web/src/App.tsx`:
```tsx
import { Routes, Route } from "react-router-dom";
import { StacksOverview } from "./pages/StacksOverview";
import { Editor } from "./pages/Editor";

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<StacksOverview />} />
      <Route path="/stacks/:id" element={<Editor />} />
    </Routes>
  );
}
```
(`Editor` is created in Task 6; for THIS task, create a minimal placeholder `web/src/pages/Editor.tsx` exporting `export function Editor() { return null; }` so the build passes, and Task 6 replaces it.)

- [ ] **Step 5: Stacks overview** — `web/src/pages/StacksOverview.tsx`:
```tsx
import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { AppShell, Group, Title, Button, SimpleGrid, Card, Text, ActionIcon, Modal, TextInput, Badge } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import type { Stack } from "../model";
import * as api from "../api";

export function StacksOverview() {
  const nav = useNavigate();
  const [stacks, setStacks] = useState<Stack[]>([]);
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");

  const load = () => api.listStacks().then(setStacks);
  useEffect(() => { load(); }, []);

  const create = async () => {
    const s = await api.createStack({ name: name || "New Stack", targetFramework: "net10.0", nodes: [], edges: [] });
    setOpen(false); setName("");
    nav(`/stacks/${s.id}`);
  };

  return (
    <AppShell header={{ height: 56 }} padding="lg">
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Title order={3}>AspireUI</Title>
          <Button leftSection={<IconPlus size={16} />} onClick={() => setOpen(true)}>New Stack</Button>
        </Group>
      </AppShell.Header>
      <AppShell.Main>
        <SimpleGrid cols={{ base: 1, sm: 2, md: 3 }}>
          {stacks.map(s => (
            <Card key={s.id} withBorder shadow="sm" padding="lg" style={{ cursor: "pointer" }}
              onClick={() => nav(`/stacks/${s.id}`)}>
              <Group justify="space-between">
                <Text fw={600}>{s.name}</Text>
                <ActionIcon variant="subtle" color="red" onClick={async (e) => { e.stopPropagation(); await api.deleteStack(s.id); load(); }}>
                  <IconTrash size={16} />
                </ActionIcon>
              </Group>
              <Group mt="sm" gap="xs">
                <Badge variant="light">{s.nodes.length} resources</Badge>
                <Badge variant="light" color="gray">{s.targetFramework}</Badge>
              </Group>
            </Card>
          ))}
        </SimpleGrid>
      </AppShell.Main>
      <Modal opened={open} onClose={() => setOpen(false)} title="New Stack">
        <TextInput label="Name" value={name} onChange={e => setName(e.currentTarget.value)} data-autofocus />
        <Button mt="md" onClick={create}>Create</Button>
      </Modal>
    </AppShell>
  );
}
```

- [ ] **Step 6: Build gate + commit + push**

Run: `cd web && npm run build` → clean. `npm test` → existing tests pass.
```bash
git add web
git commit -m "feat: Mantine shell, router, stacks overview"
git push
```

---

### Task 6: Frontend — Editor shell (palette, canvas, panel scaffold)

**Files:**
- Create: `web/src/pages/Editor.tsx` (replace placeholder), `web/src/editor/Palette.tsx`, `web/src/editor/Canvas.tsx`, `web/src/editor/PropertyPanel.tsx`
- Move/remove old `web/src/Canvas.tsx`, `Palette.tsx`, `Inspector.tsx` (superseded).

**Apply superpowers:frontend-design.**

- [ ] **Step 1: Editor page** — `web/src/pages/Editor.tsx` loads the stack by route param, holds `stack`/`selected` state, lays out AppShell with palette (navbar), canvas (main), property panel (aside):
```tsx
import { useEffect, useState } from "react";
import { useParams, useNavigate } from "react-router-dom";
import { AppShell, Group, Title, Button } from "@mantine/core";
import { IconArrowLeft } from "@tabler/icons-react";
import type { Stack } from "../model";
import * as api from "../api";
import { Palette } from "../editor/Palette";
import { Canvas } from "../editor/Canvas";
import { PropertyPanel } from "../editor/PropertyPanel";
import { RunToolbar } from "../editor/RunToolbar";

export function Editor() {
  const { id = "" } = useParams();
  const nav = useNavigate();
  const [stack, setStack] = useState<Stack | null>(null);
  const [sel, setSel] = useState<string | null>(null);

  useEffect(() => { api.getStack(id).then(setStack); }, [id]);
  if (!stack) return null;

  return (
    <AppShell header={{ height: 56 }} navbar={{ width: 240, breakpoint: 0 }} aside={{ width: 380, breakpoint: 0 }} padding={0}>
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Group>
            <Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={() => nav("/")}>Stacks</Button>
            <Title order={4}>{stack.name}</Title>
          </Group>
          <RunToolbar stack={stack} />
        </Group>
      </AppShell.Header>
      <AppShell.Navbar><Palette stack={stack} setStack={setStack} /></AppShell.Navbar>
      <AppShell.Main style={{ height: "calc(100vh - 56px)" }}>
        <Canvas stack={stack} setStack={setStack} onSelect={setSel} />
      </AppShell.Main>
      <AppShell.Aside><PropertyPanel stack={stack} nodeId={sel} setStack={setStack} /></AppShell.Aside>
    </AppShell>
  );
}
```

- [ ] **Step 2: Palette** — `web/src/editor/Palette.tsx`: fetch catalog, group by `group`, search box, click adds a node via `saveStack`. Node creation sets `addArgs: []`, `withCalls: []`, and default required addParams to empty strings so codegen stays valid.
```tsx
import { useEffect, useMemo, useState } from "react";
import { Stack as MStack, TextInput, Text, Button, ScrollArea, Divider } from "@mantine/core";
import type { Stack, ResourceType } from "../model";
import * as api from "../api";

export function Palette({ stack, setStack }: { stack: Stack; setStack: (s: Stack) => void }) {
  const [cat, setCat] = useState<ResourceType[]>([]);
  const [q, setQ] = useState("");
  useEffect(() => { api.getCatalog().then(setCat); }, []);

  const groups = useMemo(() => {
    const f = cat.filter(r => r.label.toLowerCase().includes(q.toLowerCase()));
    const by: Record<string, ResourceType[]> = {};
    for (const r of f) (by[r.group || "Other"] ??= []).push(r);
    return by;
  }, [cat, q]);

  const add = (rt: ResourceType) => {
    const suffix = stack.nodes.filter(n => n.addMethod === rt.addMethod).length || "";
    const varName = rt.addMethod.replace(/^Add/, "").toLowerCase() + suffix;
    const node = {
      id: "n" + crypto.randomUUID().slice(0, 8),
      varName, addMethod: rt.addMethod, resourceName: varName,
      withCalls: [], addArgs: rt.addParams.map(() => '""'),
      x: 60 + stack.nodes.length * 24, y: 60 + stack.nodes.length * 24,
    };
    api.saveStack({ ...stack, nodes: [...stack.nodes, node] }).then(setStack);
  };

  return (
    <MStack gap="xs" p="sm" h="100%">
      <TextInput placeholder="Search…" value={q} onChange={e => setQ(e.currentTarget.value)} />
      <ScrollArea style={{ flex: 1 }}>
        {Object.entries(groups).map(([g, items]) => (
          <div key={g}>
            <Divider my="xs" label={g} labelPosition="left" />
            {items.map(rt => (
              <Button key={rt.addMethod} variant="light" fullWidth justify="start" mb={4} onClick={() => add(rt)}>
                <Text size="sm">{rt.label}</Text>
              </Button>
            ))}
          </div>
        ))}
      </ScrollArea>
    </MStack>
  );
}
```

- [ ] **Step 3: Canvas** — `web/src/editor/Canvas.tsx`: same wiring as the old canvas (drag→patchNode, connect→addEdge, click→onSelect) but with a styled custom node type showing icon + resourceName + addMethod. Keep the `any`-typed change handlers. Register a `nodeTypes={{ resource: ResourceNode }}` custom node (a Mantine Card). Import `"@xyflow/react/dist/style.css"`. Persist position on drag-stop, POST edge on connect.

Provide the file:
```tsx
import { ReactFlow, Background, Controls, Handle, Position } from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { useCallback } from "react";
import { Card, Text, Badge } from "@mantine/core";
import type { Stack } from "../model";
import * as api from "../api";

function ResourceNode({ data }: any) {
  return (
    <Card withBorder shadow="sm" padding="xs" radius="md" style={{ minWidth: 140 }}>
      <Handle type="target" position={Position.Left} />
      <Text fw={600} size="sm">{data.resourceName}</Text>
      <Badge size="xs" variant="light" mt={4}>{data.addMethod}</Badge>
      <Handle type="source" position={Position.Right} />
    </Card>
  );
}
const nodeTypes = { resource: ResourceNode };

export function Canvas({ stack, setStack, onSelect }:
  { stack: Stack; setStack: (s: Stack) => void; onSelect: (id: string) => void }) {
  const nodes = stack.nodes.map(n => ({
    id: n.id, type: "resource", position: { x: n.x, y: n.y },
    data: { resourceName: n.resourceName, addMethod: n.addMethod },
  }));
  const edges = stack.edges.map(e => ({ id: e.id, source: e.fromNodeId, target: e.toNodeId }));

  const onNodesChange = useCallback((changes: any[]) => {
    changes.filter(c => c.type === "position" && c.dragging === false).forEach(c => {
      const node = stack.nodes.find(n => n.id === c.id);
      if (node && c.position) api.patchNode(stack.id, { ...node, x: c.position.x, y: c.position.y }).then(setStack);
    });
  }, [stack, setStack]);
  const onConnect = useCallback((c: any) =>
    api.addEdge(stack.id, { fromNodeId: c.source, toNodeId: c.target, kind: "reference" }).then(setStack),
    [stack, setStack]);

  return (
    <ReactFlow nodes={nodes} edges={edges} nodeTypes={nodeTypes}
      onNodesChange={onNodesChange} onConnect={onConnect}
      onNodeClick={(_, n) => onSelect(n.id)} fitView>
      <Background /><Controls />
    </ReactFlow>
  );
}
```

- [ ] **Step 4: PropertyPanel scaffold** — `web/src/editor/PropertyPanel.tsx` with Mantine `Tabs` (Properties, References) + a CodePreview below. For THIS task the Properties/References bodies can render placeholders (`<Text>…</Text>`); Task 7 fills PropertyGrid + references, Task 8 fills CodePreview. Keep it building.
```tsx
import { Tabs, ScrollArea, Text } from "@mantine/core";
import type { Stack } from "../model";
import { CodePreview } from "./CodePreview";

export function PropertyPanel({ stack, nodeId, setStack }:
  { stack: Stack; nodeId: string | null; setStack: (s: Stack) => void }) {
  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%" }}>
      <Tabs defaultValue="props" style={{ flex: 1, minHeight: 0, display: "flex", flexDirection: "column" }}>
        <Tabs.List>
          <Tabs.Tab value="props">Properties</Tabs.Tab>
          <Tabs.Tab value="refs">References</Tabs.Tab>
        </Tabs.List>
        <ScrollArea style={{ flex: 1 }} p="sm">
          <Tabs.Panel value="props">{nodeId ? <Text size="sm">Properties for {nodeId}</Text> : <Text size="sm" c="dimmed">Select a node</Text>}</Tabs.Panel>
          <Tabs.Panel value="refs"><Text size="sm">References</Text></Tabs.Panel>
        </ScrollArea>
      </Tabs>
      <CodePreview stackId={stack.id} version={JSON.stringify(stack).length} />
    </div>
  );
}
```
Create a minimal `web/src/editor/CodePreview.tsx` placeholder now (Task 8 fills it):
```tsx
export function CodePreview({ stackId, version }: { stackId: string; version: number }) { return null; }
```
Create a minimal `web/src/editor/RunToolbar.tsx` placeholder now (Task 8 fills it):
```tsx
import type { Stack } from "../model";
export function RunToolbar({ stack }: { stack: Stack }) { return null; }
```
Delete the superseded `web/src/Canvas.tsx`, `web/src/Palette.tsx`, `web/src/Inspector.tsx`.

- [ ] **Step 5: Build gate + commit + push**

Run: `cd web && npm run build` → clean; `npm test` → pass.
```bash
git add web
git commit -m "feat: editor shell with palette and custom-node canvas"
git push
```

---

### Task 7: Frontend — schema-driven property grid + references + config transform (Vitest)

**Files:**
- Create: `web/src/editor/PropertyGrid.tsx`
- Modify: `web/src/editor/PropertyPanel.tsx` (use PropertyGrid + references MultiSelect), `web/src/model.ts` (transform), `web/src/model.test.ts` (transform tests)

**Interfaces (pure transform in model.ts):**
```ts
// Read a with-method's repeated rows from a node's withCalls.
export function readWithRows(node: Node, method: string): string[][];      // each row = raw arg literals
// Rebuild node.withCalls for `method` from rows (preserving other methods/raw calls).
export function writeWithRows(node: Node, method: string, rows: string[][]): Node;
// Convenience for scalar addParams <-> addArgs by index.
export function setAddArg(node: Node, index: number, literal: string): Node;
```
Literals are raw C#: a string value `nginx` is stored as `"nginx"` (quoted); an int `8080` as `8080`; bool as `true`/`false`. Provide helpers `toLiteral(value, type)` and `fromLiteral(literal)`.

- [ ] **Step 1: Write failing transform tests** in `model.test.ts` (append):
```ts
import { readWithRows, writeWithRows, setAddArg, toLiteral, fromLiteral } from "./model";

const container: Node = {
  id: "n1", varName: "web", addMethod: "AddContainer", resourceName: "web",
  addArgs: ['"nginx"'], withCalls: [{ method: "WithHttpEndpoint", args: ["8080", "80"] },
                                    { method: "WithEnvironment", args: ['"KEY"', '"val"'] }],
  x: 0, y: 0,
};

describe("config transform", () => {
  it("toLiteral / fromLiteral round-trip", () => {
    expect(toLiteral("nginx", "string")).toBe('"nginx"');
    expect(toLiteral("8080", "int")).toBe("8080");
    expect(fromLiteral('"nginx"')).toBe("nginx");
    expect(fromLiteral("8080")).toBe("8080");
  });
  it("reads with-rows by method", () => {
    expect(readWithRows(container, "WithHttpEndpoint")).toEqual([["8080", "80"]]);
    expect(readWithRows(container, "WithEnvironment")).toEqual([['"KEY"', '"val"']]);
  });
  it("writes with-rows preserving other methods", () => {
    const next = writeWithRows(container, "WithHttpEndpoint", [["9090", "90"], ["9091", "91"]]);
    expect(readWithRows(next, "WithHttpEndpoint")).toEqual([["9090", "90"], ["9091", "91"]]);
    expect(readWithRows(next, "WithEnvironment")).toEqual([['"KEY"', '"val"']]); // untouched
  });
  it("sets an add-arg by index", () => {
    expect(setAddArg(container, 0, '"alpine"').addArgs[0]).toBe('"alpine"');
  });
});
```
(Import `Node` type at the top of the test file if not already: `import type { Node } from "./model";`)

- [ ] **Step 2: Run — expect FAIL**

Run: `cd web && npm test`
Expected: FAIL (functions undefined).

- [ ] **Step 3: Implement transforms** in `model.ts` (append):
```ts
export function toLiteral(value: string, type: CatalogParam["type"]): string {
  if (type === "int") return value === "" ? "0" : String(parseInt(value, 10));
  if (type === "bool") return value === "true" ? "true" : "false";
  return `"${value.replace(/\\/g, "\\\\").replace(/"/g, '\\"')}"`;
}
export function fromLiteral(literal: string): string {
  const s = literal.trim();
  if (s.startsWith('"') && s.endsWith('"')) return s.slice(1, -1).replace(/\\"/g, '"').replace(/\\\\/g, "\\");
  return s;
}
export function readWithRows(node: Node, method: string): string[][] {
  return node.withCalls.filter(w => w.method === method).map(w => w.args);
}
export function writeWithRows(node: Node, method: string, rows: string[][]): Node {
  const others = node.withCalls.filter(w => w.method !== method);
  const rebuilt = rows.map(args => ({ method, args }));
  return { ...node, withCalls: [...others, ...rebuilt] };
}
export function setAddArg(node: Node, index: number, literal: string): Node {
  const addArgs = [...node.addArgs];
  while (addArgs.length <= index) addArgs.push('""');
  addArgs[index] = literal;
  return { ...node, addArgs };
}
```

- [ ] **Step 4: Run — expect PASS**

Run: `cd web && npm test`
Expected: PASS.

- [ ] **Step 5: PropertyGrid component** — `web/src/editor/PropertyGrid.tsx`. Given the selected node and its `ResourceType`, render:
- `resourceName` TextInput (patches node.resourceName + keeps varName synced if desired).
- one control per `addParams[i]` (by type), writing via `setAddArg(node, i, toLiteral(value, type))`.
- for each `withs` entry: a section with add/remove rows; params>0 → inputs per param (env/endpoint/volume); params==0 → a single toggle (present/absent, i.e. rows = `[[]]` or `[]`). Write via `writeWithRows`.
Each change calls `api.patchNode(stack.id, updatedNode).then(setStack)`.

```tsx
import { useEffect, useState } from "react";
import { TextInput, NumberInput, Switch, Stack as MStack, Text, Button, Group, Divider, ActionIcon } from "@mantine/core";
import { IconPlus, IconX } from "@tabler/icons-react";
import type { Stack, Node, ResourceType, CatalogParam } from "../model";
import { setAddArg, toLiteral, fromLiteral, readWithRows, writeWithRows } from "../model";
import * as api from "../api";

export function PropertyGrid({ stack, node, rt, setStack }:
  { stack: Stack; node: Node; rt: ResourceType | undefined; setStack: (s: Stack) => void }) {
  const [draft, setDraft] = useState<Node>(node);
  useEffect(() => setDraft(node), [node.id]);

  const commit = (n: Node) => { setDraft(n); api.patchNode(stack.id, n).then(setStack); };

  const field = (p: CatalogParam, value: string, onChange: (v: string) => void) => {
    if (p.type === "int") return <NumberInput key={p.name} label={p.label} value={value === "" ? "" : Number(value)} onChange={v => onChange(String(v ?? ""))} />;
    if (p.type === "bool") return <Switch key={p.name} label={p.label} checked={value === "true"} onChange={e => onChange(e.currentTarget.checked ? "true" : "false")} />;
    return <TextInput key={p.name} label={p.label} value={value} onChange={e => onChange(e.currentTarget.value)} />;
  };

  return (
    <MStack gap="sm">
      <TextInput label="Name" value={draft.resourceName}
        onChange={e => commit({ ...draft, resourceName: e.currentTarget.value })} />
      {rt?.addParams.map((p, i) => field(p, fromLiteral(draft.addArgs[i] ?? '""'),
        v => commit(setAddArg(draft, i, toLiteral(v, p.type)))))}
      {rt?.withs.map(w => {
        const rows = readWithRows(draft, w.method);
        if (w.params.length === 0) {
          return <Switch key={w.method} label={w.label} checked={rows.length > 0}
            onChange={e => commit(writeWithRows(draft, w.method, e.currentTarget.checked ? [[]] : []))} />;
        }
        return (
          <div key={w.method}>
            <Divider my="xs" label={w.label} labelPosition="left" />
            {rows.map((row, ri) => (
              <Group key={ri} align="end" gap="xs" mb={4}>
                {w.params.map((p, pi) => field(p, fromLiteral(row[pi] ?? '""'), v => {
                  const nr = rows.map(r => [...r]); while (nr[ri].length <= pi) nr[ri].push('""');
                  nr[ri][pi] = toLiteral(v, p.type); commit(writeWithRows(draft, w.method, nr));
                }))}
                <ActionIcon variant="subtle" color="red" onClick={() => commit(writeWithRows(draft, w.method, rows.filter((_, x) => x !== ri)))}><IconX size={14} /></ActionIcon>
              </Group>
            ))}
            <Button size="xs" variant="light" leftSection={<IconPlus size={12} />}
              onClick={() => commit(writeWithRows(draft, w.method, [...rows, w.params.map(() => '""')]))}>Add {w.label}</Button>
          </div>
        );
      })}
    </MStack>
  );
}
```

- [ ] **Step 6: Wire PropertyPanel** — replace the Properties placeholder with `<PropertyGrid>` (look up the node + its ResourceType from a catalog fetched in PropertyPanel), and the References placeholder with a Mantine `MultiSelect` of other node names that adds/removes edges via `api.addEdge` / `api.deleteEdge` (add `deleteEdge` to api.ts: `DELETE /stacks/{id}/edges/{edgeId}`). PropertyPanel fetches `getCatalog()` once (useEffect) to resolve the selected node's ResourceType.

Add to `api.ts`:
```ts
export const deleteEdge = (id: string, edgeId: string): Promise<void> =>
  fetch(`${base}/stacks/${id}/edges/${edgeId}`, { method: "DELETE" }).then(() => undefined);
```

- [ ] **Step 7: Build gate + tests + commit + push**

Run: `cd web && npm run build` (clean) && `npm test` (transform tests pass).
```bash
git add web
git commit -m "feat: schema-driven property grid and reference picker"
git push
```

---

### Task 8: Frontend — code preview + run toolbar (status polling)

**Files:**
- Replace placeholders: `web/src/editor/CodePreview.tsx`, `web/src/editor/RunToolbar.tsx`

**Apply superpowers:frontend-design.**

- [ ] **Step 1: CodePreview** — fetch `previewStack(stackId)` whenever `version` changes; render read-only monospace with a copy button.
```tsx
import { useEffect, useState } from "react";
import { ScrollArea, Code, Group, Text, CopyButton, Button } from "@mantine/core";
import * as api from "../api";

export function CodePreview({ stackId, version }: { stackId: string; version: number }) {
  const [code, setCode] = useState("");
  useEffect(() => { api.previewStack(stackId).then(setCode); }, [stackId, version]);
  return (
    <div style={{ borderTop: "1px solid var(--mantine-color-dark-4)", height: 260, display: "flex", flexDirection: "column" }}>
      <Group justify="space-between" px="sm" py={4}>
        <Text size="xs" fw={600} c="dimmed">Program.cs</Text>
        <CopyButton value={code}>{({ copied, copy }) => <Button size="compact-xs" variant="subtle" onClick={copy}>{copied ? "Copied" : "Copy"}</Button>}</CopyButton>
      </Group>
      <ScrollArea style={{ flex: 1 }} px="sm">
        <Code block style={{ whiteSpace: "pre", fontSize: 12 }}>{code}</Code>
      </ScrollArea>
    </div>
  );
}
```

- [ ] **Step 2: RunToolbar** — Run/Stop buttons + status polling + dashboard link.
```tsx
import { useEffect, useRef, useState } from "react";
import { Button, Group, Badge, Anchor } from "@mantine/core";
import { IconPlayerPlay, IconPlayerStop, IconExternalLink, IconDownload } from "@tabler/icons-react";
import type { Stack, RunStatus } from "../model";
import * as api from "../api";

export function RunToolbar({ stack }: { stack: Stack }) {
  const [st, setSt] = useState<RunStatus>({ state: "NotRunning", log: [] });
  const timer = useRef<number>();

  const poll = () => api.statusStack(stack.id).then(setSt).catch(() => {});
  useEffect(() => { poll(); timer.current = window.setInterval(poll, 2000); return () => clearInterval(timer.current); }, [stack.id]);

  const color = { NotRunning: "gray", Starting: "yellow", Running: "green", Failed: "red" }[st.state];

  return (
    <Group gap="xs">
      <Badge color={color} variant="light">{st.state}</Badge>
      {st.state === "Running" && st.dashboardUrl &&
        <Anchor href={st.dashboardUrl} target="_blank"><Button size="xs" variant="light" leftSection={<IconExternalLink size={14} />}>Dashboard</Button></Anchor>}
      {st.state === "Running" || st.state === "Starting"
        ? <Button size="xs" color="red" leftSection={<IconPlayerStop size={14} />} onClick={() => api.stopStack(stack.id).then(setSt)}>Stop</Button>
        : <Button size="xs" color="green" leftSection={<IconPlayerPlay size={14} />} onClick={() => api.runStack(stack.id).then(setSt)}>Run</Button>}
      <Button size="xs" variant="default" leftSection={<IconDownload size={14} />}
        onClick={() => { window.location.href = `/stacks/${stack.id}/export`; }}>Export</Button>
    </Group>
  );
}
```

- [ ] **Step 3: Overview status badges (optional polish)** — in `StacksOverview`, optionally fetch `statusStack(id)` per card to show a run badge. Keep simple; skip if it complicates. Document choice.

- [ ] **Step 4: Build gate + commit + push**

Run: `cd web && npm run build` (clean) && `npm test`.
```bash
git add web
git commit -m "feat: code preview and run/stop toolbar"
git push
```

---

### Task 9: End-to-end verification + polish

**Files:** as needed for fixes surfaced by the run-through.

- [ ] **Step 1: Backend suite** — `dotnet test` → all green. Record count.
- [ ] **Step 2: Frontend** — `cd web && npm run build` clean, `npm test` green.
- [ ] **Step 3: Live run-through** (`dotnet run -c Release --project src/AspireUI.Server`, port from launchSettings):
  - Overview loads at `/`; create a stack → routes to editor.
  - Palette grouped + search; add a Container node; property grid shows Image + Tag + WithHttpEndpoint/Env/Volume sections; set image `nginx`, add a port `8080/80`, an env var.
  - `curl /stacks/{id}/preview` shows `builder.AddContainer("web", "nginx")` + `.WithHttpEndpoint(...)` etc. Paste output.
  - Add a Redis node, connect Container→Redis, confirm `WithReference` appears in preview.
  - Click Run → status Starting→Running (or Failed with log if the generated project doesn't build on this machine; capture the log tail either way). If Running, confirm a dashboard URL is returned by `curl /stacks/{id}/status`. Stop → NotRunning.
  - Export → zip contains the project.
- [ ] **Step 4:** Fix any issues found (small commits, each pushed). Report the run-through results (screenshots not required; curl/log output is enough).
- [ ] **Step 5: Final commit + push** if any fixes.

```bash
git add -A && git commit -m "chore: end-to-end verification fixes" && git push
```

---

## Self-Review

- **Spec coverage:** overview (Task 5) ✔; property grid schema-driven (Tasks 2,7) ✔; references picker (Task 7) ✔; live preview (Tasks 3,8) ✔; run/stop + dashboard (Tasks 4,8) ✔; Mantine look (Tasks 5-8 + frontend-design) ✔; AddArgs/container image (Tasks 1,2,7) ✔; net10/Aspire13 generated project so run builds (Task 1) ✔.
- **Placeholder scan:** Tasks 5/6 intentionally create minimal placeholders for components that later tasks replace (Editor, CodePreview, RunToolbar) — each is a compiling stub with the real version specified in its own task, not a TODO. All code steps contain real code.
- **Type consistency:** `NodeModel.AddArgs` (C#) ↔ `Node.addArgs` (TS); catalog records `CatalogParam/CatalogWith/ResourceType` mirror the TS interfaces (camelCase over the wire); `RunState`/`RunStatus` shared; transform helpers `readWithRows/writeWithRows/setAddArg/toLiteral/fromLiteral` defined in Task 7 and used by PropertyGrid in the same task.
- **Ordering risk:** Task 6 depends on RunToolbar/CodePreview/PropertyPanel existing as stubs — created in Task 6 itself; Task 5 creates an Editor placeholder replaced in Task 6. No forward references to undefined symbols at build time.
