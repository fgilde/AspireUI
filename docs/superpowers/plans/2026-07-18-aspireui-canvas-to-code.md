# AspireUI Canvas-to-Code Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a web tool where a .NET Aspire `AppHost` project is edited visually as a graph (nodes = resources, edges = `WithReference`), imported from existing C#, and exported as a ZIP.

**Architecture:** ASP.NET Core minimal-API backend owns all logic — Roslyn for C# generate/parse, reflection for the resource catalog, SQLite for the model. A Vite/React SPA using `@xyflow/react` is the canvas; ASP.NET serves the built SPA as one deployable. The C# project on disk is the source of truth; the tool owns a marker-delimited block inside `Program.cs`, and canvas positions live in an `aspireui.json` sidecar.

**Tech Stack:** .NET 9, ASP.NET Core minimal APIs, Roslyn (`Microsoft.CodeAnalysis.CSharp`), `Microsoft.Data.Sqlite`, xUnit; React 18 + TypeScript + Vite + `@xyflow/react`, Vitest.

## Global Constraints

- Tool projects (`AspireUI.Server`, `AspireUI.Server.Tests`) target `net10.0` (matches installed SDK 10.0.300).
- Generated stacks (the on-disk `.csproj` produced by CodeGen) target `net9.0` and reference `Aspire.Hosting.AppHost`. These are text artifacts, not built in this slice.
- The tool owns ONLY the text between `// >>> aspireui:begin` and `// <<< aspireui:end` in a generated `Program.cs`. Never rewrite code outside the markers.
- Marker block canonical form: all `var x = builder.AddX("name");` declarations first (node order), then all `x.WithY(...);` and `x.WithReference(y);` statements. One statement per line. This ordering guarantees the block compiles regardless of reference direction.
- Canvas positions (x/y) are NOT in the C#. They live only in `aspireui.json` and in the SQLite model. Round-trip equality is defined on the code-relevant model (nodes without x/y, edges) only.
- No auth, no `dotnet run`, no deploy in this slice.
- Commit messages: Conventional Commits.

---

## File Structure

```
AspireUI.sln
src/AspireUI.Server/
  AspireUI.Server.csproj
  Program.cs                     minimal-API host + static SPA
  Models/StackModel.cs           records: StackModel, NodeModel, EdgeModel, WithCall
  Services/StackStore.cs         SQLite JSON-blob CRUD
  Services/CodeGenService.cs     model -> Program.cs/.csproj, materialize to disk, compile-check
  Services/ImportService.cs      Program.cs + sidecar -> model
  Services/CatalogService.cs     reflection over Aspire assemblies + JSON overlays
  Services/ExportService.cs      project dir -> ZIP bytes
  Endpoints/StackEndpoints.cs    maps REST routes to services
  catalog/aspire-hosting.json    overlay for core package
tests/AspireUI.Server.Tests/
  AspireUI.Server.Tests.csproj
  StackStoreTests.cs
  CodeGenTests.cs
  ImportTests.cs
  CatalogTests.cs
  ApiTests.cs
web/
  package.json  vite.config.ts  tsconfig.json  index.html
  src/model.ts        StackModel <-> React Flow mapping (pure functions)
  src/model.test.ts
  src/api.ts          fetch wrappers
  src/Canvas.tsx      React Flow canvas
  src/Inspector.tsx   right panel: name/env/WithX editing
  src/Palette.tsx     catalog drag source
  src/App.tsx
```

---

### Task 1: Solution scaffold + model + failing store test

**Files:**
- Create: `AspireUI.sln`, `src/AspireUI.Server/AspireUI.Server.csproj`, `src/AspireUI.Server/Models/StackModel.cs`
- Create: `tests/AspireUI.Server.Tests/AspireUI.Server.Tests.csproj`, `tests/AspireUI.Server.Tests/StackStoreTests.cs`

**Interfaces:**
- Produces: model records used by every later task.

```csharp
// Models/StackModel.cs
namespace AspireUI.Server.Models;

public record StackModel(
    string Id,
    string Name,
    string TargetFramework,
    List<NodeModel> Nodes,
    List<EdgeModel> Edges);

public record NodeModel(
    string Id,
    string VarName,       // C# identifier, e.g. "db"
    string AddMethod,     // e.g. "AddPostgres"
    string ResourceName,  // string arg, e.g. "db"
    List<WithCall> WithCalls,
    double X,
    double Y);

public record EdgeModel(string Id, string FromNodeId, string ToNodeId, string Kind); // Kind = "reference"

public record WithCall(string Method, List<string> Args); // Args = raw C# literals, e.g. "\"vol\""
```

- [ ] **Step 1: Create the projects**

Run:
```bash
cd src/AspireUI.Server && dotnet new web -f net10.0 -n AspireUI.Server -o . && cd ../..
dotnet new xunit -f net10.0 -n AspireUI.Server.Tests -o tests/AspireUI.Server.Tests
dotnet new sln -n AspireUI && dotnet sln add src/AspireUI.Server tests/AspireUI.Server.Tests
dotnet add tests/AspireUI.Server.Tests reference src/AspireUI.Server
dotnet add src/AspireUI.Server package Microsoft.Data.Sqlite
```

- [ ] **Step 2: Add the model file** — write `Models/StackModel.cs` exactly as above.

- [ ] **Step 3: Write the failing store test**

```csharp
// StackStoreTests.cs
using AspireUI.Server.Models;
using AspireUI.Server.Services;

public class StackStoreTests
{
    [Fact]
    public void SaveGet_RoundTrips()
    {
        var store = new StackStore(":memory:");
        var s = new StackModel("s1", "demo", "net9.0",
            [new NodeModel("n1", "db", "AddPostgres", "db", [], 10, 20)],
            []);
        store.Save(s);
        var got = store.Get("s1");
        Assert.Equal("demo", got!.Name);
        Assert.Single(got.Nodes);
        Assert.Equal("db", got.Nodes[0].VarName);
    }

    [Fact]
    public void Delete_Removes()
    {
        var store = new StackStore(":memory:");
        store.Save(new StackModel("s1", "d", "net9.0", [], []));
        store.Delete("s1");
        Assert.Null(store.Get("s1"));
    }
}
```

- [ ] **Step 4: Run it — expect FAIL** (`StackStore` not defined)

Run: `dotnet test tests/AspireUI.Server.Tests`
Expected: FAIL, compile error `StackStore` not found.

- [ ] **Step 5: Commit**

```bash
git add AspireUI.sln src tests
git commit -m "chore: scaffold server + model, failing store test"
```

---

### Task 2: StackStore (SQLite JSON-blob CRUD)

**Files:**
- Create: `src/AspireUI.Server/Services/StackStore.cs`
- Test: `tests/AspireUI.Server.Tests/StackStoreTests.cs` (from Task 1)

**Interfaces:**
- Consumes: `StackModel` (Task 1).
- Produces: `StackStore(string dbPath)`, `void Save(StackModel)`, `StackModel? Get(string id)`, `IReadOnlyList<StackModel> List()`, `void Delete(string id)`.

- [ ] **Step 1: Implement StackStore**

```csharp
// Services/StackStore.cs
using System.Text.Json;
using AspireUI.Server.Models;
using Microsoft.Data.Sqlite;

namespace AspireUI.Server.Services;

public class StackStore
{
    private readonly string _connString;

    public StackStore(string dbPath = "aspireui.db")
    {
        _connString = dbPath == ":memory:"
            ? "Data Source=StackStore;Mode=Memory;Cache=Shared"
            : $"Data Source={dbPath}";
        // Keep one open connection for :memory: shared cache to persist across calls.
        _keepAlive = new SqliteConnection(_connString);
        _keepAlive.Open();
        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS stacks (id TEXT PRIMARY KEY, name TEXT, json TEXT)";
        cmd.ExecuteNonQuery();
    }

    private readonly SqliteConnection _keepAlive;

    public void Save(StackModel s)
    {
        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = "INSERT INTO stacks (id,name,json) VALUES ($i,$n,$j) " +
                          "ON CONFLICT(id) DO UPDATE SET name=$n, json=$j";
        cmd.Parameters.AddWithValue("$i", s.Id);
        cmd.Parameters.AddWithValue("$n", s.Name);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(s));
        cmd.ExecuteNonQuery();
    }

    public StackModel? Get(string id)
    {
        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = "SELECT json FROM stacks WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<StackModel>(json);
    }

    public IReadOnlyList<StackModel> List()
    {
        var result = new List<StackModel>();
        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = "SELECT json FROM stacks ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(JsonSerializer.Deserialize<StackModel>(r.GetString(0))!);
        return result;
    }

    public void Delete(string id)
    {
        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = "DELETE FROM stacks WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    }
}
```

- [ ] **Step 2: Run tests — expect PASS**

Run: `dotnet test tests/AspireUI.Server.Tests --filter StackStoreTests`
Expected: PASS (2 tests).

- [ ] **Step 3: Commit**

```bash
git add src/AspireUI.Server/Services/StackStore.cs
git commit -m "feat: SQLite-backed StackStore"
```

---

### Task 3: CodeGenService (model → Program.cs, compile-check)

**Files:**
- Create: `src/AspireUI.Server/Services/CodeGenService.cs`
- Test: `tests/AspireUI.Server.Tests/CodeGenTests.cs`

**Interfaces:**
- Consumes: `StackModel`, `NodeModel`, `EdgeModel`, `WithCall`.
- Produces:
  - `string GenerateProgram(StackModel s)`
  - `string GenerateCsproj(StackModel s)`
  - `void Materialize(StackModel s, string dir)` — writes `Program.cs`, `<Name>.csproj`, `aspireui.json`.
  - `IReadOnlyList<string> CompileErrors(string programCs)` — empty list = compiles.

- [ ] **Step 1: Add Roslyn package**

Run: `dotnet add src/AspireUI.Server package Microsoft.CodeAnalysis.CSharp`

- [ ] **Step 2: Write failing test**

```csharp
// CodeGenTests.cs
using System.Text.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

public class CodeGenTests
{
    private static StackModel Fixture() => new("s1", "Demo", "net9.0",
        [
            new NodeModel("n1", "db", "AddPostgres", "db", [new WithCall("WithDataVolume", [])], 0, 0),
            new NodeModel("n2", "cache", "AddRedis", "cache", [], 0, 0)
        ],
        [new EdgeModel("e1", "n1", "n2", "reference")]);

    [Fact]
    public void Generate_EmitsMarkerBlockInCanonicalOrder()
    {
        var code = new CodeGenService().GenerateProgram(Fixture());
        Assert.Contains("// >>> aspireui:begin", code);
        Assert.Contains("var db = builder.AddPostgres(\"db\");", code);
        Assert.Contains("var cache = builder.AddRedis(\"cache\");", code);
        Assert.Contains("db.WithDataVolume();", code);
        Assert.Contains("db.WithReference(cache);", code);
        Assert.Contains("// <<< aspireui:end", code);
        // declarations precede modifications
        Assert.True(code.IndexOf("var cache =") < code.IndexOf("db.WithDataVolume();"));
    }

    [Fact]
    public void Materialize_WritesFilesAndSidecar()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-test-" + Guid.NewGuid());
        new CodeGenService().Materialize(Fixture(), dir);
        Assert.True(File.Exists(Path.Combine(dir, "Program.cs")));
        Assert.True(File.Exists(Path.Combine(dir, "Demo.csproj")));
        var sidecar = JsonSerializer.Deserialize<Dictionary<string, double[]>>(
            File.ReadAllText(Path.Combine(dir, "aspireui.json")));
        Assert.True(sidecar!.ContainsKey("n1"));
        Directory.Delete(dir, true);
    }
}
```

- [ ] **Step 3: Run — expect FAIL** (`CodeGenService` not defined)

Run: `dotnet test tests/AspireUI.Server.Tests --filter CodeGenTests`
Expected: FAIL, compile error.

- [ ] **Step 4: Implement CodeGenService**

```csharp
// Services/CodeGenService.cs
using System.Text;
using System.Text.Json;
using AspireUI.Server.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AspireUI.Server.Services;

public class CodeGenService
{
    public const string Begin = "// >>> aspireui:begin (nicht von Hand editieren)";
    public const string End = "// <<< aspireui:end";

    public string GenerateProgram(StackModel s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("var builder = DistributedApplication.CreateBuilder(args);");
        sb.AppendLine();
        sb.AppendLine(Begin);
        foreach (var n in s.Nodes)
            sb.AppendLine($"var {n.VarName} = builder.{n.AddMethod}(\"{n.ResourceName}\");");
        foreach (var n in s.Nodes)
            foreach (var w in n.WithCalls)
                sb.AppendLine($"{n.VarName}.{w.Method}({string.Join(", ", w.Args)});");
        foreach (var e in s.Edges.Where(e => e.Kind == "reference"))
            sb.AppendLine($"{Var(s, e.FromNodeId)}.WithReference({Var(s, e.ToNodeId)});");
        sb.AppendLine(End);
        sb.AppendLine();
        sb.AppendLine("builder.Build().Run();");
        return sb.ToString();
    }

    private static string Var(StackModel s, string nodeId) =>
        s.Nodes.First(n => n.Id == nodeId).VarName;

    public string GenerateCsproj(StackModel s) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <Sdk Name="Aspire.AppHost.Sdk" Version="9.0.0" />
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>{s.TargetFramework}</TargetFramework>
            <IsAspireHost>true</IsAspireHost>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Aspire.Hosting.AppHost" Version="9.0.0" />
          </ItemGroup>
        </Project>
        """;

    public void Materialize(StackModel s, string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), GenerateProgram(s));
        File.WriteAllText(Path.Combine(dir, $"{s.Name}.csproj"), GenerateCsproj(s));
        var positions = s.Nodes.ToDictionary(n => n.Id, n => new[] { n.X, n.Y });
        File.WriteAllText(Path.Combine(dir, "aspireui.json"), JsonSerializer.Serialize(positions));
    }

    public IReadOnlyList<string> CompileErrors(string programCs)
    {
        var tree = CSharpSyntaxTree.ParseText(programCs);
        var comp = CSharpCompilation.Create("check")
            .AddSyntaxTrees(tree)
            .WithOptions(new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        // Only surface syntax errors here; full semantic check needs Aspire refs (later slice).
        return tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
    }
}
```

`ponytail:` `CompileErrors` does syntax-only checking — no Aspire metadata references loaded. Full semantic compile belongs to the run slice; upgrade when we resolve real package assemblies.

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test tests/AspireUI.Server.Tests --filter CodeGenTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/AspireUI.Server/Services/CodeGenService.cs tests/AspireUI.Server.Tests/CodeGenTests.cs src/AspireUI.Server/AspireUI.Server.csproj
git commit -m "feat: CodeGenService generates canonical marker block"
```

---

### Task 4: ImportService + round-trip invariant

**Files:**
- Create: `src/AspireUI.Server/Services/ImportService.cs`
- Test: `tests/AspireUI.Server.Tests/ImportTests.cs`

**Interfaces:**
- Consumes: `CodeGenService.Begin`/`End`, model records.
- Produces: `StackModel Import(string id, string name, string programCs, string sidecarJson)`. Statements inside markers parse to nodes/edges/withcalls; anything else is ignored in this slice (foreign-node UI is a later task, tracked below).

- [ ] **Step 1: Write the round-trip test (the load-bearing one)**

```csharp
// ImportTests.cs
using System.Text.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

public class ImportTests
{
    private static StackModel Fixture() => new("s1", "Demo", "net9.0",
        [
            new NodeModel("n1", "db", "AddPostgres", "db", [new WithCall("WithDataVolume", [])], 5, 6),
            new NodeModel("n2", "cache", "AddRedis", "cache", [], 7, 8)
        ],
        [new EdgeModel("e1", "n1", "n2", "reference")]);

    [Fact]
    public void ImportOfGenerate_EqualsOriginal_IgnoringPositions()
    {
        var m = Fixture();
        var code = new CodeGenService().GenerateProgram(m);
        var sidecar = JsonSerializer.Serialize(
            m.Nodes.ToDictionary(n => n.Id, n => new[] { n.X, n.Y }));

        var back = new ImportService().Import("s1", "Demo", code, sidecar);

        // Compare code-relevant shape: (varName, addMethod, resourceName, withCalls) per node, ignoring ids/xy.
        string Key(NodeModel n) => $"{n.VarName}|{n.AddMethod}|{n.ResourceName}|" +
            string.Join(",", n.WithCalls.Select(w => w.Method + "(" + string.Join(";", w.Args) + ")"));
        Assert.Equal(m.Nodes.Select(Key).OrderBy(x => x),
                     back.Nodes.Select(Key).OrderBy(x => x));

        // Edges compared by (fromVar -> toVar).
        string EdgeKey(StackModel s, EdgeModel e) =>
            s.Nodes.First(n => n.Id == e.FromNodeId).VarName + "->" +
            s.Nodes.First(n => n.Id == e.ToNodeId).VarName;
        Assert.Equal(m.Edges.Select(e => EdgeKey(m, e)),
                     back.Edges.Select(e => EdgeKey(back, e)));

        // Positions restored from sidecar.
        Assert.Equal(5, back.Nodes.First(n => n.VarName == "db").X);
    }
}
```

- [ ] **Step 2: Run — expect FAIL** (`ImportService` not defined)

Run: `dotnet test tests/AspireUI.Server.Tests --filter ImportTests`
Expected: FAIL.

- [ ] **Step 3: Implement ImportService**

```csharp
// Services/ImportService.cs
using System.Text.Json;
using AspireUI.Server.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AspireUI.Server.Services;

public class ImportService
{
    public StackModel Import(string id, string name, string programCs, string sidecarJson)
    {
        var positions = string.IsNullOrWhiteSpace(sidecarJson)
            ? new Dictionary<string, double[]>()
            : JsonSerializer.Deserialize<Dictionary<string, double[]>>(sidecarJson)!;

        var root = CSharpSyntaxTree.ParseText(programCs).GetCompilationUnitRoot();
        var (from, to) = MarkerSpan(programCs);

        var statements = root.DescendantNodes()
            .OfType<StatementSyntax>()
            .Where(s => s.SpanStart >= from && s.Span.End <= to)
            .ToList();

        var nodes = new List<NodeModel>();
        var edges = new List<EdgeModel>();
        var varToNodeId = new Dictionary<string, string>();
        int nId = 0, eId = 0;

        // Pass 1: declarations "var X = builder.AddM("name");"
        foreach (var st in statements.OfType<LocalDeclarationStatementSyntax>())
        {
            var decl = st.Declaration.Variables[0];
            var varName = decl.Identifier.Text;
            if (decl.Initializer?.Value is not InvocationExpressionSyntax inv) continue;
            if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;      // builder.AddM
            var addMethod = ma.Name.Identifier.Text;
            var resourceName = (inv.ArgumentList.Arguments.FirstOrDefault()?.Expression
                as LiteralExpressionSyntax)?.Token.ValueText ?? varName;
            var nodeId = "n" + (++nId);
            varToNodeId[varName] = nodeId;
            nodes.Add(new NodeModel(nodeId, varName, addMethod, resourceName, [], 0, 0));
        }

        // Pass 2: modifications "X.WithY(...);" and "X.WithReference(Y);"
        foreach (var st in statements.OfType<ExpressionStatementSyntax>())
        {
            if (st.Expression is not InvocationExpressionSyntax inv) continue;
            if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
            if (ma.Expression is not IdentifierNameSyntax target) continue;
            if (!varToNodeId.TryGetValue(target.Identifier.Text, out var srcNodeId)) continue;
            var method = ma.Name.Identifier.Text;

            if (method == "WithReference"
                && inv.ArgumentList.Arguments.FirstOrDefault()?.Expression is IdentifierNameSyntax refId
                && varToNodeId.TryGetValue(refId.Identifier.Text, out var toNodeId))
            {
                edges.Add(new EdgeModel("e" + (++eId), srcNodeId, toNodeId, "reference"));
            }
            else
            {
                var args = inv.ArgumentList.Arguments.Select(a => a.Expression.ToString()).ToList();
                var idx = nodes.FindIndex(x => x.Id == srcNodeId);
                nodes[idx] = nodes[idx] with { WithCalls = [.. nodes[idx].WithCalls, new WithCall(method, args)] };
            }
        }

        // Restore positions from sidecar (keyed by node id).
        for (int i = 0; i < nodes.Count; i++)
            if (positions.TryGetValue(nodes[i].Id, out var xy) && xy.Length == 2)
                nodes[i] = nodes[i] with { X = xy[0], Y = xy[1] };

        return new StackModel(id, name, "net9.0", nodes, edges);
    }

    private static (int from, int to) MarkerSpan(string src)
    {
        var b = src.IndexOf(CodeGenService.Begin, StringComparison.Ordinal);
        var e = src.IndexOf(CodeGenService.End, StringComparison.Ordinal);
        return b < 0 || e < 0 ? (0, src.Length) : (b, e);
    }
}
```

Note the sidecar keys node ids (`n1`, `n2`). Import re-derives ids in declaration order (`n1`, `n2`, …), matching what generate/materialize wrote, so positions line up on round-trip. `ponytail:` id re-derivation is positional — stable for tool-generated files; a later slice can persist a stable id comment if hand-edited files reorder nodes.

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test tests/AspireUI.Server.Tests --filter ImportTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AspireUI.Server/Services/ImportService.cs tests/AspireUI.Server.Tests/ImportTests.cs
git commit -m "feat: ImportService with round-trip invariant"
```

---

### Task 5: CatalogService (reflection + overlay)

**Files:**
- Create: `src/AspireUI.Server/Services/CatalogService.cs`, `src/AspireUI.Server/catalog/aspire-hosting.json`
- Test: `tests/AspireUI.Server.Tests/CatalogTests.cs`

**Interfaces:**
- Produces: `record ResourceType(string AddMethod, string Label, string? Icon, string? Group, List<CatalogWith> Withs)`, `record CatalogWith(string Method, List<string> Params)`, and `CatalogService(params Assembly[] assemblies)` with `IReadOnlyList<ResourceType> GetCatalog()`.

- [ ] **Step 1: Reference a package that provides AddX for the test**

Run:
```bash
dotnet add tests/AspireUI.Server.Tests package Aspire.Hosting.Redis
```

- [ ] **Step 2: Write failing test**

```csharp
// CatalogTests.cs
using System.Reflection;
using AspireUI.Server.Services;

public class CatalogTests
{
    [Fact]
    public void Reflection_FindsAddRedis()
    {
        // Force-load the assembly that defines AddRedis.
        var asm = typeof(Aspire.Hosting.RedisBuilderExtensions).Assembly;
        var catalog = new CatalogService(asm).GetCatalog();
        Assert.Contains(catalog, r => r.AddMethod == "AddRedis");
    }
}
```

- [ ] **Step 3: Run — expect FAIL**

Run: `dotnet test tests/AspireUI.Server.Tests --filter CatalogTests`
Expected: FAIL.

- [ ] **Step 4: Add overlay file** `src/AspireUI.Server/catalog/aspire-hosting.json`

```json
{
  "AddRedis":    { "label": "Redis",    "icon": "redis",    "group": "Cache",    "withs": ["WithDataVolume", "WithRedisCommander"] },
  "AddPostgres": { "label": "Postgres", "icon": "postgres", "group": "Database", "withs": ["WithDataVolume", "WithPgAdmin"] },
  "AddContainer":{ "label": "Container","icon": "docker",   "group": "Generic",  "withs": ["WithVolume", "WithEnvironment", "WithHttpEndpoint"] }
}
```

Add to `.csproj` so it copies to output:
```xml
<ItemGroup>
  <Content Include="catalog\**\*.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 5: Implement CatalogService**

```csharp
// Services/CatalogService.cs
using System.Reflection;
using System.Text.Json;

namespace AspireUI.Server.Services;

public record CatalogWith(string Method, List<string> Params);
public record ResourceType(string AddMethod, string Label, string? Icon, string? Group, List<CatalogWith> Withs);

public class CatalogService
{
    private readonly Assembly[] _assemblies;
    private readonly Dictionary<string, JsonElement> _overlay;

    public CatalogService(params Assembly[] assemblies)
    {
        _assemblies = assemblies.Length > 0 ? assemblies : LoadDefault();
        _overlay = LoadOverlays();
    }

    public IReadOnlyList<ResourceType> GetCatalog()
    {
        var result = new List<ResourceType>();
        foreach (var asm in _assemblies)
        foreach (var type in SafeTypes(asm))
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (!m.Name.StartsWith("Add")) continue;
            if (!ReturnsResourceBuilder(m.ReturnType)) continue;
            var p = m.GetParameters();
            if (p.Length == 0 || !IsAppBuilder(p[0].ParameterType)) continue; // extension on IDistributedApplicationBuilder

            var over = _overlay.TryGetValue(m.Name, out var o) ? o : (JsonElement?)null;
            var withs = (over?.TryGetProperty("withs", out var w) == true)
                ? w.EnumerateArray().Select(x => new CatalogWith(x.GetString()!, [])).ToList()
                : new List<CatalogWith>();
            result.Add(new ResourceType(
                m.Name,
                over?.GetProperty("label").GetString() ?? m.Name[3..],
                over?.TryGetProperty("icon", out var i) == true ? i.GetString() : null,
                over?.TryGetProperty("group", out var g) == true ? g.GetString() : "Other",
                withs));
        }
        // Dedup by AddMethod (same extension can appear via multiple overloads).
        return result.GroupBy(r => r.AddMethod).Select(gr => gr.First()).OrderBy(r => r.AddMethod).ToList();
    }

    private static bool ReturnsResourceBuilder(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition().Name.StartsWith("IResourceBuilder");

    private static bool IsAppBuilder(Type t) => t.Name == "IDistributedApplicationBuilder";

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null)!; }
    }

    private static Assembly[] LoadDefault() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Aspire.Hosting") == true
                     || a.GetName().Name?.Contains("Aspire") == true)
            .ToArray();

    private Dictionary<string, JsonElement> LoadOverlays()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "catalog");
        var map = new Dictionary<string, JsonElement>();
        if (!Directory.Exists(dir)) return map;
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            var doc = JsonDocument.Parse(File.ReadAllText(f));
            foreach (var prop in doc.RootElement.EnumerateObject())
                map[prop.Name] = prop.Value.Clone();
        }
        return map;
    }
}
```

- [ ] **Step 6: Run — expect PASS**

Run: `dotnet test tests/AspireUI.Server.Tests --filter CatalogTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/AspireUI.Server/Services/CatalogService.cs src/AspireUI.Server/catalog tests/AspireUI.Server.Tests
git commit -m "feat: CatalogService reflection + overlay merge"
```

---

### Task 6: ExportService (ZIP)

**Files:**
- Create: `src/AspireUI.Server/Services/ExportService.cs`
- Test: `tests/AspireUI.Server.Tests/CodeGenTests.cs` (add one test) or new `ExportTests.cs`

**Interfaces:**
- Produces: `byte[] Zip(string dir)`.

- [ ] **Step 1: Write failing test** (`tests/AspireUI.Server.Tests/ExportTests.cs`)

```csharp
using System.IO.Compression;
using AspireUI.Server.Services;

public class ExportTests
{
    [Fact]
    public void Zip_ContainsProjectFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-zip-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "// x");
        File.WriteAllText(Path.Combine(dir, "Demo.csproj"), "<Project/>");

        var bytes = new ExportService().Zip(dir);

        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Contains(zip.Entries, e => e.FullName == "Program.cs");
        Assert.Contains(zip.Entries, e => e.FullName == "Demo.csproj");
        Directory.Delete(dir, true);
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test tests/AspireUI.Server.Tests --filter ExportTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// Services/ExportService.cs
using System.IO.Compression;

namespace AspireUI.Server.Services;

public class ExportService
{
    public byte[] Zip(string dir)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                zip.CreateEntryFromFile(file, rel);
            }
        }
        return ms.ToArray();
    }
}
```

- [ ] **Step 4: Run — expect PASS**; then **Commit**

```bash
git add src/AspireUI.Server/Services/ExportService.cs tests/AspireUI.Server.Tests/ExportTests.cs
git commit -m "feat: ExportService zips project dir"
```

---

### Task 7: REST endpoints + integration test

**Files:**
- Create: `src/AspireUI.Server/Endpoints/StackEndpoints.cs`
- Modify: `src/AspireUI.Server/Program.cs`
- Test: `tests/AspireUI.Server.Tests/ApiTests.cs`

**Interfaces:**
- Consumes: all services above.
- Produces: routes `GET /catalog`, `GET/POST/DELETE /stacks[/{id}]`, `PATCH /stacks/{id}/nodes/{nodeId}`, `POST /stacks/{id}/edges`, `DELETE /stacks/{id}/edges/{edgeId}`, `GET /stacks/{id}/export`, `POST /stacks/{id}/import`.
- The workspace root is configured via `WORKSPACE_DIR` env var (default `./workspace`). Each save materializes to `workspace/{id}/`.

- [ ] **Step 1: Add test package + write failing integration test**

Run: `dotnet add tests/AspireUI.Server.Tests package Microsoft.AspNetCore.Mvc.Testing`

Add `<ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>` to the test csproj if the factory needs it.

```csharp
// ApiTests.cs
using System.Net.Http.Json;
using AspireUI.Server.Models;
using Microsoft.AspNetCore.Mvc.Testing;

public class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _c;
    public ApiTests(WebApplicationFactory<Program> f) => _c = f.CreateClient();

    [Fact]
    public async Task CreateThenGet_Works()
    {
        var create = await _c.PostAsJsonAsync("/stacks",
            new StackModel("", "MyStack", "net9.0", [], []));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<StackModel>();
        Assert.False(string.IsNullOrEmpty(created!.Id));

        var got = await _c.GetFromJsonAsync<StackModel>($"/stacks/{created.Id}");
        Assert.Equal("MyStack", got!.Name);
    }

    [Fact]
    public async Task Catalog_ReturnsList()
    {
        var cat = await _c.GetFromJsonAsync<List<ResourceTypeDto>>("/catalog");
        Assert.NotNull(cat);
    }
    public record ResourceTypeDto(string AddMethod, string Label);
}
```

- [ ] **Step 2: Run — expect FAIL** (`Program` not accessible / routes missing)

Run: `dotnet test tests/AspireUI.Server.Tests --filter ApiTests`
Expected: FAIL.

- [ ] **Step 3: Write StackEndpoints**

```csharp
// Endpoints/StackEndpoints.cs
using AspireUI.Server.Models;
using AspireUI.Server.Services;

namespace AspireUI.Server.Endpoints;

public static class StackEndpoints
{
    public static void MapStackEndpoints(this WebApplication app)
    {
        var store = new StackStore(Environment.GetEnvironmentVariable("DB_PATH") ?? "aspireui.db");
        var gen = new CodeGenService();
        var import = new ImportService();
        var export = new ExportService();
        var catalog = new CatalogService();
        var wsRoot = Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? "workspace";

        string Dir(string id) => Path.Combine(wsRoot, id);

        // Materialize + compile-check; returns error list (empty = ok).
        IResult Persist(StackModel s)
        {
            var errors = gen.CompileErrors(gen.GenerateProgram(s));
            if (errors.Count > 0) return Results.UnprocessableEntity(errors);
            store.Save(s);
            gen.Materialize(s, Dir(s.Id));
            return Results.Ok(s);
        }

        app.MapGet("/catalog", () => catalog.GetCatalog());
        app.MapGet("/stacks", () => store.List());
        app.MapGet("/stacks/{id}", (string id) =>
            store.Get(id) is { } s ? Results.Ok(s) : Results.NotFound());

        app.MapPost("/stacks", (StackModel body) =>
        {
            var s = body with { Id = Guid.NewGuid().ToString("n") };
            return Persist(s);
        });

        app.MapPut("/stacks/{id}", (string id, StackModel body) =>
            store.Get(id) is null ? Results.NotFound() : Persist(body with { Id = id }));

        app.MapDelete("/stacks/{id}", (string id) =>
        {
            store.Delete(id);
            if (Directory.Exists(Dir(id))) Directory.Delete(Dir(id), true);
            return Results.NoContent();
        });

        app.MapPatch("/stacks/{id}/nodes/{nodeId}", (string id, string nodeId, NodeModel patch) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            var idx = s.Nodes.FindIndex(n => n.Id == nodeId);
            if (idx < 0) return Results.NotFound();
            s.Nodes[idx] = patch with { Id = nodeId };
            return Persist(s);
        });

        app.MapPost("/stacks/{id}/edges", (string id, EdgeModel edge) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            s.Edges.Add(edge with { Id = "e" + Guid.NewGuid().ToString("n")[..8] });
            return Persist(s);
        });

        app.MapDelete("/stacks/{id}/edges/{edgeId}", (string id, string edgeId) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            s.Edges.RemoveAll(e => e.Id == edgeId);
            return Persist(s);
        });

        app.MapGet("/stacks/{id}/export", (string id) =>
        {
            if (!Directory.Exists(Dir(id))) return Results.NotFound();
            return Results.File(export.Zip(Dir(id)), "application/zip", $"{id}.zip");
        });

        app.MapPost("/stacks/{id}/import", (string id, ImportRequest req) =>
        {
            var s = import.Import(id, req.Name, req.ProgramCs, req.SidecarJson ?? "");
            return Persist(s);
        });
    }

    public record ImportRequest(string Name, string ProgramCs, string? SidecarJson);
}
```

- [ ] **Step 4: Wire Program.cs** — replace generated `Program.cs` body:

```csharp
using AspireUI.Server.Endpoints;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();       // serves built SPA from wwwroot (Task 8 copies it here)
app.MapStackEndpoints();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program { } // expose for WebApplicationFactory
```

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test tests/AspireUI.Server.Tests --filter ApiTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Full backend test run + commit**

Run: `dotnet test`
Expected: all tests PASS.
```bash
git add src/AspireUI.Server tests/AspireUI.Server.Tests
git commit -m "feat: REST endpoints wiring services"
```

---

### Task 8: React SPA scaffold + model mapping (Vitest)

**Files:**
- Create: `web/` Vite app, `web/src/model.ts`, `web/src/model.test.ts`, `web/src/api.ts`

**Interfaces:**
- Produces: TS types mirroring the C# model; `toFlow(stack)` → `{nodes, edges}` for React Flow; `applyNodePosition(stack, id, x, y)`.

- [ ] **Step 1: Scaffold**

Run:
```bash
cd web && npm create vite@latest . -- --template react-ts && npm i && npm i @xyflow/react && npm i -D vitest && cd ..
```

- [ ] **Step 2: Write types + failing model test**

`web/src/model.ts`:
```ts
export interface WithCall { method: string; args: string[] }
export interface Node { id: string; varName: string; addMethod: string; resourceName: string; withCalls: WithCall[]; x: number; y: number }
export interface Edge { id: string; fromNodeId: string; toNodeId: string; kind: string }
export interface Stack { id: string; name: string; targetFramework: string; nodes: Node[]; edges: Edge[] }

export function toFlow(s: Stack) {
  return {
    nodes: s.nodes.map(n => ({
      id: n.id,
      position: { x: n.x, y: n.y },
      data: { label: `${n.resourceName} (${n.addMethod})`, node: n },
      type: "default",
    })),
    edges: s.edges.map(e => ({ id: e.id, source: e.fromNodeId, target: e.toNodeId })),
  };
}

export function applyNodePosition(s: Stack, id: string, x: number, y: number): Stack {
  return { ...s, nodes: s.nodes.map(n => n.id === id ? { ...n, x, y } : n) };
}
```

`web/src/model.test.ts`:
```ts
import { describe, it, expect } from "vitest";
import { toFlow, applyNodePosition, Stack } from "./model";

const stack: Stack = {
  id: "s1", name: "d", targetFramework: "net9.0",
  nodes: [{ id: "n1", varName: "db", addMethod: "AddPostgres", resourceName: "db", withCalls: [], x: 1, y: 2 }],
  edges: [{ id: "e1", fromNodeId: "n1", toNodeId: "n1", kind: "reference" }],
};

describe("model", () => {
  it("maps nodes to flow positions", () => {
    const f = toFlow(stack);
    expect(f.nodes[0].position).toEqual({ x: 1, y: 2 });
    expect(f.edges[0].source).toBe("n1");
  });
  it("updates a node position immutably", () => {
    const next = applyNodePosition(stack, "n1", 9, 9);
    expect(next.nodes[0].x).toBe(9);
    expect(stack.nodes[0].x).toBe(1);
  });
});
```

Add to `web/package.json` scripts: `"test": "vitest run"`.

- [ ] **Step 3: Run — expect PASS**

Run: `cd web && npm test && cd ..`
Expected: PASS (2 tests).

- [ ] **Step 4: Add api.ts**

```ts
// web/src/api.ts
import { Stack, Node, Edge } from "./model";
const base = "";
export const getCatalog = () => fetch(`${base}/catalog`).then(r => r.json());
export const listStacks = () => fetch(`${base}/stacks`).then(r => r.json());
export const getStack = (id: string): Promise<Stack> => fetch(`${base}/stacks/${id}`).then(r => r.json());
export const createStack = (s: Partial<Stack>): Promise<Stack> =>
  fetch(`${base}/stacks`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(s) }).then(r => r.json());
export const saveStack = (s: Stack): Promise<Stack> =>
  fetch(`${base}/stacks/${s.id}`, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(s) }).then(r => r.json());
export const patchNode = (id: string, node: Node): Promise<Stack> =>
  fetch(`${base}/stacks/${id}/nodes/${node.id}`, { method: "PATCH", headers: { "content-type": "application/json" }, body: JSON.stringify(node) }).then(r => r.json());
export const addEdge = (id: string, edge: Partial<Edge>): Promise<Stack> =>
  fetch(`${base}/stacks/${id}/edges`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(edge) }).then(r => r.json());
```

- [ ] **Step 5: Commit**

```bash
git add web
git commit -m "feat: React scaffold + model mapping"
```

---

### Task 9: Canvas + Palette + Inspector

**Files:**
- Create: `web/src/Canvas.tsx`, `web/src/Palette.tsx`, `web/src/Inspector.tsx`, `web/src/App.tsx`

**Interfaces:**
- Consumes: `model.ts`, `api.ts`, `@xyflow/react`.
- Produces: an app where dragging a catalog item adds a node, dragging a node saves its position, connecting two nodes POSTs an edge, and selecting a node shows the Inspector which PATCHes resourceName + WithCalls.

- [ ] **Step 1: Canvas.tsx**

```tsx
import { ReactFlow, Background, Controls, applyNodeChanges, addEdge as rfAddEdge } from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { useCallback } from "react";
import { Stack } from "./model";
import * as api from "./api";

export function Canvas({ stack, setStack, onSelect }:
  { stack: Stack; setStack: (s: Stack) => void; onSelect: (nodeId: string) => void }) {

  const flowNodes = stack.nodes.map(n => ({
    id: n.id, position: { x: n.x, y: n.y },
    data: { label: `${n.resourceName}\n${n.addMethod}` },
  }));
  const flowEdges = stack.edges.map(e => ({ id: e.id, source: e.fromNodeId, target: e.toNodeId }));

  const onNodesChange = useCallback((changes: any[]) => {
    // Persist position on drag-stop.
    changes.filter(c => c.type === "position" && c.dragging === false).forEach(c => {
      const node = stack.nodes.find(n => n.id === c.id);
      if (node && c.position) api.patchNode(stack.id, { ...node, x: c.position.x, y: c.position.y }).then(setStack);
    });
  }, [stack, setStack]);

  const onConnect = useCallback((c: any) => {
    api.addEdge(stack.id, { fromNodeId: c.source, toNodeId: c.target, kind: "reference" }).then(setStack);
  }, [stack, setStack]);

  return (
    <div style={{ flex: 1, height: "100vh" }}>
      <ReactFlow nodes={flowNodes} edges={flowEdges}
        onNodesChange={onNodesChange} onConnect={onConnect}
        onNodeClick={(_, n) => onSelect(n.id)} fitView>
        <Background /><Controls />
      </ReactFlow>
    </div>
  );
}
```

- [ ] **Step 2: Palette.tsx** — lists the catalog; clicking a resource appends a node to the stack and persists via `saveStack` (the `PUT /stacks/{id}` route from Task 7, `saveStack` from Task 8).

```tsx
// Palette.tsx
import { useEffect, useState } from "react";
import { Stack } from "./model";
import * as api from "./api";

export function Palette({ stack, setStack }: { stack: Stack; setStack: (s: Stack) => void }) {
  const [cat, setCat] = useState<any[]>([]);
  useEffect(() => { api.getCatalog().then(setCat); }, []);

  const add = (rt: any) => {
    const n = cat.filter(c => c.addMethod === rt.addMethod).length;
    const base = rt.addMethod.replace(/^Add/, "").toLowerCase();
    const varName = base + (stack.nodes.filter(x => x.addMethod === rt.addMethod).length || "");
    const node = {
      id: "n" + crypto.randomUUID().slice(0, 8),
      varName, addMethod: rt.addMethod, resourceName: varName,
      withCalls: [], x: 40 + stack.nodes.length * 30, y: 40 + stack.nodes.length * 30,
    };
    api.saveStack({ ...stack, nodes: [...stack.nodes, node] }).then(setStack);
  };

  return (
    <div style={{ width: 200, borderRight: "1px solid #333", padding: 8 }}>
      <h3>Resources</h3>
      {cat.map(rt => (
        <button key={rt.addMethod} onClick={() => add(rt)} style={{ display: "block", width: "100%", marginBottom: 4 }}>
          {rt.label}
        </button>
      ))}
    </div>
  );
}
```

- [ ] **Step 3: Inspector.tsx**

```tsx
import { useState, useEffect } from "react";
import { Stack, Node } from "./model";
import * as api from "./api";

export function Inspector({ stack, nodeId, setStack }:
  { stack: Stack; nodeId: string | null; setStack: (s: Stack) => void }) {
  const node = stack.nodes.find(n => n.id === nodeId);
  const [draft, setDraft] = useState<Node | null>(node ?? null);
  useEffect(() => setDraft(node ?? null), [nodeId]);

  if (!draft) return <div style={{ width: 300, padding: 8 }}>Select a node</div>;

  const save = () => api.patchNode(stack.id, draft).then(setStack);

  return (
    <div style={{ width: 300, borderLeft: "1px solid #333", padding: 8 }}>
      <h3>{draft.addMethod}</h3>
      <label>Name<input value={draft.resourceName}
        onChange={e => setDraft({ ...draft, resourceName: e.target.value })} /></label>
      <h4>WithCalls</h4>
      {draft.withCalls.map((w, i) => (
        <div key={i}>{w.method}({w.args.join(", ")})
          <button onClick={() => setDraft({ ...draft, withCalls: draft.withCalls.filter((_, j) => j !== i) })}>x</button>
        </div>
      ))}
      <button onClick={() => {
        const m = prompt("Method (e.g. WithDataVolume)"); if (!m) return;
        setDraft({ ...draft, withCalls: [...draft.withCalls, { method: m, args: [] }] });
      }}>+ WithCall</button>
      <hr /><button onClick={save}>Save</button>
    </div>
  );
}
```

- [ ] **Step 4: App.tsx**

```tsx
import { useEffect, useState } from "react";
import { Stack } from "./model";
import * as api from "./api";
import { Canvas } from "./Canvas";
import { Palette } from "./Palette";
import { Inspector } from "./Inspector";

export default function App() {
  const [stack, setStack] = useState<Stack | null>(null);
  const [sel, setSel] = useState<string | null>(null);

  useEffect(() => {
    api.listStacks().then(async (list: Stack[]) => {
      setStack(list[0] ?? await api.createStack({ name: "New Stack", targetFramework: "net9.0", nodes: [], edges: [] }));
    });
  }, []);

  if (!stack) return <div>Loading…</div>;
  return (
    <div style={{ display: "flex", height: "100vh" }}>
      <Palette stack={stack} setStack={setStack} />
      <Canvas stack={stack} setStack={setStack} onSelect={setSel} />
      <Inspector stack={stack} nodeId={sel} setStack={setStack} />
    </div>
  );
}
```

- [ ] **Step 5: Manual smoke test**

Run backend: `dotnet run --project src/AspireUI.Server` and frontend dev: `cd web && npm run dev`.
Configure Vite proxy in `web/vite.config.ts` so `/catalog`, `/stacks` proxy to `http://localhost:5000`:
```ts
server: { proxy: { "/catalog": "http://localhost:5000", "/stacks": "http://localhost:5000" } }
```
Expected: palette lists resources; clicking adds a node; dragging repositions and persists (reload keeps position); connecting two nodes draws an edge and `Program.cs` in `workspace/<id>/` gains a `WithReference` line.

- [ ] **Step 6: Commit**

```bash
git add web
git commit -m "feat: canvas, palette, inspector"
```

---

### Task 10: Build integration — SPA served by ASP.NET + export button

**Files:**
- Modify: `src/AspireUI.Server/AspireUI.Server.csproj`, `web/src/App.tsx`

**Interfaces:**
- Produces: `dotnet publish` yields a single deployable serving the built SPA; an Export button downloads the ZIP.

- [ ] **Step 1: Build SPA into wwwroot on publish** — add to server csproj:

```xml
<Target Name="BuildSpa" BeforeTargets="Build">
  <Exec Command="npm install" WorkingDirectory="../../web" />
  <Exec Command="npm run build" WorkingDirectory="../../web" />
  <ItemGroup>
    <SpaFiles Include="../../web/dist/**/*" />
  </ItemGroup>
  <Copy SourceFiles="@(SpaFiles)" DestinationFolder="wwwroot/%(RecursiveDir)" />
</Target>
```

- [ ] **Step 2: Add Export button in App.tsx** (near top-level):

```tsx
<button onClick={() => window.location.href = `/stacks/${stack.id}/export`}>Export ZIP</button>
```

- [ ] **Step 3: Verify end-to-end**

Run: `dotnet run --project src/AspireUI.Server`, open `http://localhost:5000`.
Expected: SPA loads from ASP.NET (no separate dev server), add nodes, click Export ZIP → downloads `<id>.zip` containing `Program.cs` + `.csproj` + `aspireui.json`. Unzip, run `dotnet build` in it — the AppHost project compiles.

- [ ] **Step 4: Commit**

```bash
git add src/AspireUI.Server/AspireUI.Server.csproj web/src/App.tsx
git commit -m "feat: serve SPA from ASP.NET + export button"
```

---

## Deferred to later slices (tracked, not built here)

- **Foreign nodes:** import currently ignores statements outside the marker block. Spec calls for pinning them as read-only nodes. Add when the import UI exists.
- **AddProject<T> generic:** the `builder.AddProject<Projects.X>("name")` form (generic type arg) isn't modeled; only `AddX("name")` resources are. Add a `TypeArg` field to `NodeModel` when project resources are needed.
- **Semantic compile-check:** `CompileErrors` is syntax-only; real Aspire-referenced compilation belongs to the run slice.
- Auth, wizard, `dotnet run`, deploy, reverse-proxy, Proxmox install script — separate specs.

## Self-Review

- **Spec coverage:** model→code (Tasks 3), code→model round-trip (Task 4), catalog hybrid (Task 5), SQLite store (Task 2), REST (Task 7), ZIP export (Tasks 6/10), canvas/inspector/palette (Tasks 8/9), sidecar positions (Tasks 3/4), one-deployable (Task 10). ✔
- **Placeholder scan:** every code step has full code; Task 9 flags a required Task 7 addition (PUT route) explicitly rather than leaving a TODO. ✔
- **Type consistency:** `StackModel/NodeModel/EdgeModel/WithCall` names/fields identical across C# and TS (camelCase over the wire via System.Text.Json default). `saveStack` PUT route added to Task 7 as noted. ✔
