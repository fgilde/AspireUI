# Hosting foundation + edit-lock (Appliance Slice 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deploy any stack persistently into a "Hosting" area (tracked compose deployment, lifecycle, host:port URLs) and lock the stack for editing while it runs there.

**Architecture:** A new `DeploymentStore` (SQLite) tracks one deployment per stack. A `HostingService` chains the existing `PublishService.Publish` (→ docker-compose.yaml, post-processed with `restart: unless-stopped`) and a project-scoped `DeployService` (`docker compose -p <project> up -d/stop/start/down/ps`). Mutating stack endpoints consult the store and 409 while a stack's deployment is running. A `/hosting` SPA page manages deployments; the editor shows a read-only banner + "Stop & edit" when locked.

**Tech Stack:** ASP.NET Core minimal APIs (net10), Microsoft.Data.Sqlite, React + Mantine + React Router, xUnit + WebApplicationFactory.

## Global Constraints

- All API endpoints live under `/api` (group `app2 = app.MapGroup("/api")`); frontend `base = "/api"`.
- Hosting (compose-deploy) and dev Run (`RunService`/aspire) are **separate paths** — never entangle.
- Single Docker host, Linux-first. One stack ↔ at most one deployment.
- Stores follow the existing pattern (file SQLite at `DB_PATH`, `:memory:` shared-cache keep-alive for tests) — mirror `SnippetStore`.
- Commit after every green step. No `--no-verify`, no co-author footer, push is separate (owner pushes / existing workflow).

---

### Task 1: Deployment model + DeploymentStore

**Files:**
- Create: `src/AspireUI.Server/Models/Deployment.cs`
- Create: `src/AspireUI.Server/Services/DeploymentStore.cs`
- Test: `tests/AspireUI.Server.Tests/DeploymentStoreTests.cs`

**Interfaces:**
- Produces: `record Deployment(string Id, string StackId, string Name, string ComposeDir, string Project, string State, List<string> Urls, string CreatedAt, string UpdatedAt, string? LastError)`; `DeploymentStore(string dbPath)` with `Upsert(Deployment)`, `Deployment? Get(string id)`, `Deployment? GetByStack(string stackId)`, `IReadOnlyList<Deployment> List()`, `bool Delete(string id)`, `void SetState(string id, string state, string? error = null)`. States: `deploying|running|stopped|failed`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AspireUI.Server.Tests/DeploymentStoreTests.cs
using AspireUI.Server.Models;
using AspireUI.Server.Services;

public class DeploymentStoreTests
{
    private static DeploymentStore NewStore() => new(":memory:");

    [Fact]
    public void Upsert_then_GetByStack_roundtrips()
    {
        var s = NewStore();
        var d = new Deployment("d1", "stack1", "Demo", "/c/dir", "aspireui-stack1", "running",
            new() { "http://localhost:8096" }, "2026-07-23T00:00:00Z", "2026-07-23T00:00:00Z", null);
        s.Upsert(d);
        var got = s.GetByStack("stack1");
        Assert.NotNull(got);
        Assert.Equal("running", got!.State);
        Assert.Equal("aspireui-stack1", got.Project);
        Assert.Single(got.Urls);
    }

    [Fact]
    public void GetByStack_is_unique_last_write_wins()
    {
        var s = NewStore();
        s.Upsert(new Deployment("d1", "stack1", "A", "/c", "p", "deploying", new(), "t", "t", null));
        s.Upsert(new Deployment("d1", "stack1", "A", "/c", "p", "running", new(), "t", "t2", null));
        Assert.Equal("running", s.GetByStack("stack1")!.State);
        Assert.Single(s.List());
    }

    [Fact]
    public void SetState_updates_state_and_error()
    {
        var s = NewStore();
        s.Upsert(new Deployment("d1", "stack1", "A", "/c", "p", "deploying", new(), "t", "t", null));
        s.SetState("d1", "failed", "boom");
        Assert.Equal("failed", s.Get("d1")!.State);
        Assert.Equal("boom", s.Get("d1")!.LastError);
    }

    [Fact]
    public void Delete_removes()
    {
        var s = NewStore();
        s.Upsert(new Deployment("d1", "stack1", "A", "/c", "p", "running", new(), "t", "t", null));
        Assert.True(s.Delete("d1"));
        Assert.Null(s.GetByStack("stack1"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AspireUI.Server.Tests --filter FullyQualifiedName~DeploymentStoreTests`
Expected: FAIL to compile — `Deployment` / `DeploymentStore` do not exist.

- [ ] **Step 3: Write the model**

```csharp
// src/AspireUI.Server/Models/Deployment.cs
namespace AspireUI.Server.Models;

// A stack deployed persistently into hosting (a long-lived docker-compose project), tracked so it
// survives AppHost restarts and can be managed. State: deploying|running|stopped|failed.
public record Deployment(string Id, string StackId, string Name, string ComposeDir, string Project,
    string State, List<string> Urls, string CreatedAt, string UpdatedAt, string? LastError);
```

- [ ] **Step 4: Write the store**

```csharp
// src/AspireUI.Server/Services/DeploymentStore.cs
using System.Text.Json;
using AspireUI.Server.Models;
using Microsoft.Data.Sqlite;

namespace AspireUI.Server.Services;

// Tracks hosting deployments (one per stack) in the shared SQLite DB. Mirrors SnippetStore.
public class DeploymentStore
{
    private readonly string _connString;
    private readonly SqliteConnection? _keepAlive;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DeploymentStore(string dbPath = "aspireui.db")
    {
        _connString = dbPath == ":memory:" ? "Data Source=DeploymentStore;Mode=Memory;Cache=Shared" : $"Data Source={dbPath}";
        if (dbPath == ":memory:") { _keepAlive = new SqliteConnection(_connString); _keepAlive.Open(); }
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS deployments (id TEXT PRIMARY KEY, stack_id TEXT UNIQUE, " +
                              "name TEXT, compose_dir TEXT, project TEXT, state TEXT, urls TEXT, " +
                              "created_at TEXT, updated_at TEXT, last_error TEXT)";
            cmd.ExecuteNonQuery();
        });
    }

    private void UsingConnection(Action<SqliteConnection> action)
    {
        if (_keepAlive is { } shared) { action(shared); return; }
        using var conn = new SqliteConnection(_connString); conn.Open(); action(conn);
    }

    private static Deployment Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        JsonSerializer.Deserialize<List<string>>(r.IsDBNull(6) ? "[]" : r.GetString(6), Json) ?? new(),
        r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9));

    private const string Cols = "id, stack_id, name, compose_dir, project, state, urls, created_at, updated_at, last_error";

    public void Upsert(Deployment d) => UsingConnection(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO deployments (" + Cols + ") VALUES " +
                          "($i,$s,$n,$c,$p,$st,$u,$ca,$ua,$e)";
        cmd.Parameters.AddWithValue("$i", d.Id);
        cmd.Parameters.AddWithValue("$s", d.StackId);
        cmd.Parameters.AddWithValue("$n", d.Name);
        cmd.Parameters.AddWithValue("$c", d.ComposeDir);
        cmd.Parameters.AddWithValue("$p", d.Project);
        cmd.Parameters.AddWithValue("$st", d.State);
        cmd.Parameters.AddWithValue("$u", JsonSerializer.Serialize(d.Urls, Json));
        cmd.Parameters.AddWithValue("$ca", d.CreatedAt);
        cmd.Parameters.AddWithValue("$ua", d.UpdatedAt);
        cmd.Parameters.AddWithValue("$e", (object?)d.LastError ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    });

    public Deployment? Get(string id) => QueryOne("WHERE id=$k", id);
    public Deployment? GetByStack(string stackId) => QueryOne("WHERE stack_id=$k", stackId);

    private Deployment? QueryOne(string where, string key)
    {
        Deployment? result = null;
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {Cols} FROM deployments {where}";
            cmd.Parameters.AddWithValue("$k", key);
            using var r = cmd.ExecuteReader();
            if (r.Read()) result = Read(r);
        });
        return result;
    }

    public IReadOnlyList<Deployment> List()
    {
        var result = new List<Deployment>();
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {Cols} FROM deployments ORDER BY created_at DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(Read(r));
        });
        return result;
    }

    public void SetState(string id, string state, string? error = null) => UsingConnection(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE deployments SET state=$st, last_error=$e, updated_at=$ua WHERE id=$i";
        cmd.Parameters.AddWithValue("$st", state);
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ua", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    });

    public bool Delete(string id)
    {
        var n = 0;
        UsingConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM deployments WHERE id=$i";
            cmd.Parameters.AddWithValue("$i", id);
            n = cmd.ExecuteNonQuery();
        });
        return n > 0;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/AspireUI.Server.Tests --filter FullyQualifiedName~DeploymentStoreTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/AspireUI.Server/Models/Deployment.cs src/AspireUI.Server/Services/DeploymentStore.cs tests/AspireUI.Server.Tests/DeploymentStoreTests.cs
git commit -m "feat(hosting): Deployment model + DeploymentStore (one tracked compose deployment per stack)"
```

---

### Task 2: Project-scoped compose commands in DeployService

**Files:**
- Modify: `src/AspireUI.Server/Services/DeployService.cs`
- Test: `tests/AspireUI.Server.Tests/DeployServiceProjectTests.cs`

**Interfaces:**
- Consumes: `DeployService(Func<string,string,ProcessStartInfo>? commandFactory)` (existing test seam).
- Produces on `DeployService`: `DeployResult UpProject(string dir, string project)`, `StopProject(string dir, string project)`, `StartProject(string dir, string project)`, `DownProject(string dir, string project)`, `DeployResult Ps(string dir, string project)`. Each shells `docker compose -p <project> <verb> …`.

- [ ] **Step 1: Write the failing test** (asserts the docker args, via the injectable factory — no real docker)

```csharp
// tests/AspireUI.Server.Tests/DeployServiceProjectTests.cs
using System.Diagnostics;
using AspireUI.Server.Services;

public class DeployServiceProjectTests
{
    // Capture the args the service would run; exit 0 so Run() reports ok.
    private static (DeployService svc, List<string> calls) Fake()
    {
        var calls = new List<string>();
        var svc = new DeployService((workdir, args) =>
        {
            calls.Add(args);
            // A trivially-succeeding process on any platform.
            return new ProcessStartInfo { FileName = "cmd", Arguments = "/c exit 0" };
        });
        return (svc, calls);
    }

    [Fact]
    public void UpProject_passes_project_and_up_detached()
    {
        var (svc, calls) = Fake();
        svc.UpProject("/dir", "aspireui-abc");
        Assert.Contains("compose -p aspireui-abc up -d", calls);
    }

    [Fact]
    public void Stop_Start_Down_Ps_use_project()
    {
        var (svc, calls) = Fake();
        svc.StopProject("/d", "p"); svc.StartProject("/d", "p"); svc.DownProject("/d", "p"); svc.Ps("/d", "p");
        Assert.Contains("compose -p p stop", calls);
        Assert.Contains("compose -p p start", calls);
        Assert.Contains("compose -p p down", calls);
        Assert.Contains("compose -p p ps --format json", calls);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AspireUI.Server.Tests --filter FullyQualifiedName~DeployServiceProjectTests`
Expected: FAIL to compile — `UpProject` etc. undefined.

- [ ] **Step 3: Add the methods** (insert after the existing `Up`/`Down` in `DeployService`)

```csharp
    public DeployResult UpProject(string dir, string project) => Run(dir, $"compose -p {project} up -d");
    public DeployResult StopProject(string dir, string project) => Run(dir, $"compose -p {project} stop");
    public DeployResult StartProject(string dir, string project) => Run(dir, $"compose -p {project} start");
    public DeployResult DownProject(string dir, string project) => Run(dir, $"compose -p {project} down");
    public DeployResult Ps(string dir, string project) => Run(dir, $"compose -p {project} ps --format json");
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AspireUI.Server.Tests --filter FullyQualifiedName~DeployServiceProjectTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/AspireUI.Server/Services/DeployService.cs tests/AspireUI.Server.Tests/DeployServiceProjectTests.cs
git commit -m "feat(hosting): project-scoped docker compose commands (up/stop/start/down/ps -p <project>)"
```

---

### Task 3: HostingService — compose post-process, URL parse, lifecycle orchestration

**Files:**
- Create: `src/AspireUI.Server/Services/HostingService.cs`
- Test: `tests/AspireUI.Server.Tests/HostingServiceTests.cs`

**Interfaces:**
- Consumes: `PublishService.Publish(StackModel, string publishRoot, string target="compose")` → writes `docker-compose.yaml` under `<publishRoot>/out`; `DeployService` project methods (Task 2); `DeploymentStore` (Task 1).
- Produces: `HostingService(DeploymentStore store, PublishService publish, DeployService deploy)` with `Deployment Deploy(StackModel stack, string publishRoot)`, `void Stop(string id)`, `void Start(string id)`, `void Undeploy(string id)`, `Deployment? Refresh(string id)`, and static helpers `string AddRestartPolicy(string composeYaml)`, `List<string> ParseUrls(string composeYaml, string host)`. Project name: `Project(stackId) => "aspireui-" + stackId[..Math.Min(8, stackId.Length)]`.

- [ ] **Step 1: Write the failing test** (pure helpers only — the docker-driven methods are exercised manually)

```csharp
// tests/AspireUI.Server.Tests/HostingServiceTests.cs
using AspireUI.Server.Services;

public class HostingServiceTests
{
    private const string Compose = """
        services:
          web:
            image: nginx
            ports:
              - "8096:80"
          api:
            image: acme/api
            ports:
              - "5000:5000"
        """;

    [Fact]
    public void AddRestartPolicy_adds_unless_stopped_to_each_service()
    {
        var outp = HostingService.AddRestartPolicy(Compose);
        // one restart line per service
        var count = outp.Split('\n').Count(l => l.Trim() == "restart: unless-stopped");
        Assert.Equal(2, count);
    }

    [Fact]
    public void AddRestartPolicy_is_idempotent()
    {
        var once = HostingService.AddRestartPolicy(Compose);
        var twice = HostingService.AddRestartPolicy(once);
        Assert.Equal(once.Split('\n').Count(l => l.Trim() == "restart: unless-stopped"),
                     twice.Split('\n').Count(l => l.Trim() == "restart: unless-stopped"));
    }

    [Fact]
    public void ParseUrls_maps_host_ports()
    {
        var urls = HostingService.ParseUrls(Compose, "localhost");
        Assert.Contains("http://localhost:8096", urls);
        Assert.Contains("http://localhost:5000", urls);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AspireUI.Server.Tests --filter FullyQualifiedName~HostingServiceTests`
Expected: FAIL to compile — `HostingService` undefined.

- [ ] **Step 3: Write the service**

```csharp
// src/AspireUI.Server/Services/HostingService.cs
using System.Text;
using System.Text.RegularExpressions;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

// Turns a stack into a tracked, persistent compose deployment (install & forget), separate from the
// ephemeral dev Run path. Deploy = publish → post-process compose (restart policy) → up -d.
public class HostingService(DeploymentStore store, PublishService publish, DeployService deploy)
{
    public static string Project(string stackId) => "aspireui-" + stackId[..Math.Min(8, stackId.Length)];

    private static string ComposePath(string publishRoot) => Path.Combine(publishRoot, "out", "docker-compose.yaml");

    // Add `restart: unless-stopped` under each service (2-space service indent → 4-space property).
    // Idempotent: skips a service that already has a restart line.
    public static string AddRestartPolicy(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        var outp = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            outp.Add(lines[i]);
            // A service header looks like `  web:` (exactly 2 leading spaces, ends with ':').
            var m = Regex.Match(lines[i], @"^  (\S[^:]*):\s*$");
            if (!m.Success) continue;
            // Find the service block's existing properties; skip if a restart line is already present.
            var hasRestart = false;
            for (var j = i + 1; j < lines.Count; j++)
            {
                if (Regex.IsMatch(lines[j], @"^  \S")) break;             // next service / dedent
                if (lines[j].Trim() == "restart: unless-stopped") { hasRestart = true; break; }
            }
            if (!hasRestart) outp.Add("    restart: unless-stopped");
        }
        return string.Join("\n", outp);
    }

    // Emit http://host:HOSTPORT for each published `- "HOST:CONTAINER"` mapping.
    public static List<string> ParseUrls(string yaml, string host)
    {
        var urls = new List<string>();
        foreach (Match m in Regex.Matches(yaml, @"-\s*""?(\d+):\d+""?"))
            urls.Add($"http://{host}:{m.Groups[1].Value}");
        return urls.Distinct().ToList();
    }

    public Deployment Deploy(StackModel stack, string publishRoot, string host = "localhost")
    {
        var project = Project(stack.Id);
        var now = DateTime.UtcNow.ToString("O");
        var existing = store.GetByStack(stack.Id);
        var id = existing?.Id ?? "dep" + Guid.NewGuid().ToString("n")[..8];
        var outDir = Path.Combine(publishRoot, "out");
        store.Upsert(new Deployment(id, stack.Id, stack.Name, outDir, project, "deploying",
            existing?.Urls ?? new(), existing?.CreatedAt ?? now, now, null));
        try
        {
            var pub = publish.Publish(stack, publishRoot, "compose");
            if (!pub.Ok) { store.SetState(id, "failed", pub.Log); return store.Get(id)!; }
            var path = ComposePath(publishRoot);
            var yaml = File.ReadAllText(path);
            File.WriteAllText(path, AddRestartPolicy(yaml));
            var urls = ParseUrls(yaml, host);
            var up = deploy.UpProject(outDir, project);
            store.Upsert(store.Get(id)! with { Urls = urls, State = up.Ok ? "running" : "failed",
                LastError = up.Ok ? null : up.Log, UpdatedAt = DateTime.UtcNow.ToString("O") });
        }
        catch (Exception ex) { store.SetState(id, "failed", ex.Message); }
        return store.Get(id)!;
    }

    public void Stop(string id) { if (store.Get(id) is { } d) { deploy.StopProject(d.ComposeDir, d.Project); store.SetState(id, "stopped"); } }
    public void Start(string id) { if (store.Get(id) is { } d) { deploy.StartProject(d.ComposeDir, d.Project); store.SetState(id, "running"); } }
    public void Undeploy(string id) { if (store.Get(id) is { } d) { deploy.DownProject(d.ComposeDir, d.Project); store.Delete(id); } }

    // Best-effort reconcile from `docker compose ps` (exit!=0 or empty → mark stopped).
    public Deployment? Refresh(string id)
    {
        if (store.Get(id) is not { } d) return null;
        if (d.State is "deploying" or "failed") return d;
        var ps = deploy.Ps(d.ComposeDir, d.Project);
        var running = ps.Ok && ps.Log.Contains("\"State\":\"running\"", StringComparison.OrdinalIgnoreCase);
        var next = running ? "running" : "stopped";
        if (next != d.State) store.SetState(id, next);
        return store.Get(id);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AspireUI.Server.Tests --filter FullyQualifiedName~HostingServiceTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/AspireUI.Server/Services/HostingService.cs tests/AspireUI.Server.Tests/HostingServiceTests.cs
git commit -m "feat(hosting): HostingService — publish+deploy orchestration, restart-policy injection, host:port URL parse"
```

---

### Task 4: Endpoints — hosting lifecycle, deployment on GET, edit-lock guard

**Files:**
- Modify: `src/AspireUI.Server/Endpoints/StackEndpoints.cs`
- Test: `tests/AspireUI.Server.Tests/HostingLockTests.cs`

**Interfaces:**
- Consumes: `DeploymentStore`, `HostingService` (Tasks 1/3), existing `store` (StackStore), `PublishRoot(id)`, `Dir(id)`, `gen`.
- Produces endpoints (under `/api`): `POST /stacks/{id}/hosting/deploy`, `POST /stacks/{id}/hosting/stop`, `POST /stacks/{id}/hosting/start`, `POST /stacks/{id}/hosting/undeploy`, `GET /hosting`, `GET /hosting/{id}/logs` (SSE). `GET /stacks/{id}` response includes a `deployment` field. Mutating endpoints 409 while locked.

- [ ] **Step 1: Write the failing test** (seed a running deployment into the SAME db the app uses → mutations 409; stop → 200)

```csharp
// tests/AspireUI.Server.Tests/HostingLockTests.cs
using System.Net;
using System.Net.Http.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

public class HostingLockTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _f;
    private readonly HttpClient _c;
    public HostingLockTests(TestWebAppFactory f) { _f = f; _c = f.CreateClient(); }

    private static StackModel EmptyStack(string name) => new(
        "", name, "net10.0", new(), new(), new(), new(), new());

    [Fact]
    public async Task Mutations_409_while_running_then_ok_after_stop()
    {
        // Create a stack via the API.
        var created = await (await _c.PostAsJsonAsync("/api/stacks", EmptyStack("Locked")))
            .Content.ReadFromJsonAsync<StackModel>();
        var id = created!.Id;

        // Seed a RUNNING deployment for it directly in the shared DB the server reads.
        var store = new DeploymentStore(_f.DbPath);
        store.Upsert(new Deployment("dep1", id, "Locked", "/c", HostingService.Project(id), "running",
            new(), "t", "t", null));

        // A save (PUT) must now be rejected.
        var put = await _c.PutAsJsonAsync($"/api/stacks/{id}", created with { Name = "changed" });
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);

        // GET carries the deployment so the UI can lock.
        var got = await _c.GetFromJsonAsync<StackWithDeployment>($"/api/stacks/{id}");
        Assert.Equal("running", got!.Deployment?.State);

        // Stop unlocks.
        store.SetState("dep1", "stopped");
        var put2 = await _c.PutAsJsonAsync($"/api/stacks/{id}", created with { Name = "changed" });
        Assert.Equal(HttpStatusCode.OK, put2.StatusCode);
    }

    // Minimal shape for reading the augmented GET (only the field we assert).
    private record StackWithDeployment(string Id, DeploymentDto? Deployment);
    private record DeploymentDto(string State);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AspireUI.Server.Tests --filter FullyQualifiedName~HostingLockTests`
Expected: FAIL — PUT currently returns 200 (no lock), and GET has no `deployment` field.

- [ ] **Step 3: Wire stores/service + guard + endpoints.** In `StackEndpoints.MapStackEndpoints`, after the existing `var snippets = new SnippetStore(...)` line add:

```csharp
        var deployments = new DeploymentStore(Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db"));
        var hosting = new HostingService(deployments, publish, deploy);

        // A stack is locked for editing while its hosting deployment is deploying/running.
        bool Locked(string stackId) => deployments.GetByStack(stackId) is { State: "running" or "deploying" };
        IResult? LockGuard(string stackId) => Locked(stackId)
            ? Results.Json(new { message = "stack is running in hosting — stop it to edit", deployment = deployments.GetByStack(stackId) }, statusCode: StatusCodes.Status409Conflict)
            : null;
```

- [ ] **Step 4: Add the lock guard to each mutating endpoint.** For every handler below, add the guard as the first line. Exact edits:

`PUT /stacks/{id}` (currently `store.Get(id) is null ? NotFound : Persist(body with {Id=id})`) becomes:

```csharp
        app2.MapPut("/stacks/{id}", (string id, StackModel body) =>
            LockGuard(id) ?? (store.Get(id) is null ? Results.NotFound() : Persist(body with { Id = id })));
```

`PATCH /stacks/{id}/nodes/{nodeId}` — add `if (LockGuard(id) is { } r) return r;` as the first line of the handler body.

`POST /stacks/{id}/edges` — add `if (LockGuard(id) is { } r) return r;` first.

`DELETE /stacks/{id}/edges/{edgeId}` — add `if (LockGuard(id) is { } r) return r;` first.

`POST /stacks/{id}/run` — add `if (LockGuard(id) is { } r) return r;` first.

`POST /stacks/{id}/code/save` — add `if (LockGuard(id) is { } r) return r;` first.

`DELETE /stacks/{id}` — add `if (LockGuard(id) is { } r) return r;` first (can't delete a live deployment's stack; undeploy first).

- [ ] **Step 5: Augment `GET /stacks/{id}`** to include the deployment. Find the get-one handler (returns the stack) and wrap its value:

```csharp
        app2.MapGet("/stacks/{id}", (string id) =>
            store.Get(id) is { } s
                ? Results.Ok(new { s.Id, s.Name, s.TargetFramework, s.Nodes, s.Edges, s.RawStatements,
                    s.ExtraFiles, s.ExtraPackages, s.Notes, s.Groups, s.CreatedAt, s.CreatedBy,
                    deployment = deployments.GetByStack(id) })
                : Results.NotFound());
```

(If the existing get-one returns `store.Get(id)` directly, replace it with the above so the SPA still receives all stack fields plus `deployment`. Keep field names identical to `StackModel`'s JSON so the frontend `Stack` type is unchanged apart from the new optional `deployment`.)

- [ ] **Step 6: Add hosting endpoints** (near the other `app2` endpoints):

```csharp
        app2.MapPost("/stacks/{id}/hosting/deploy", (string id, HttpContext ctx) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            gen.Materialize(s, Dir(id));
            var host = ctx.Request.Host.Host;
            return Results.Ok(hosting.Deploy(s, PublishRoot(id), host));
        });
        app2.MapPost("/stacks/{id}/hosting/stop", (string id) =>
            deployments.GetByStack(id) is { } d ? (IResult)Results.Ok(Wrap(hosting, () => hosting.Stop(d.Id), d.Id, deployments)) : Results.NotFound());
        app2.MapPost("/stacks/{id}/hosting/start", (string id) =>
            deployments.GetByStack(id) is { } d ? (IResult)Results.Ok(Wrap(hosting, () => hosting.Start(d.Id), d.Id, deployments)) : Results.NotFound());
        app2.MapPost("/stacks/{id}/hosting/undeploy", (string id) =>
        {
            if (deployments.GetByStack(id) is not { } d) return Results.NotFound();
            hosting.Undeploy(d.Id);
            return Results.NoContent();
        });
        app2.MapGet("/hosting", () => Results.Ok(deployments.List().Select(d => hosting.Refresh(d.Id) ?? d)));
        app2.MapGet("/hosting/{id}/logs", async (string id, HttpContext ctx) =>
        {
            if (deployments.Get(id) is not { } d) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.Headers.Append("Content-Type", "text/event-stream");
            var res = deploy.Ps(d.ComposeDir, d.Project); // simple: emit compose logs snapshot then close
            var logs = deploy.Run(d.ComposeDir, $"compose -p {d.Project} logs --tail 200");
            foreach (var line in (logs.Ok ? logs.Log : res.Log).Split('\n'))
                await ctx.Response.WriteAsync($"data: {line}\n\n");
            await ctx.Response.Body.FlushAsync();
        });
```

Add this small helper method to the class (returns the refreshed deployment after a lifecycle action):

```csharp
    private static Deployment Wrap(HostingService h, Action act, string id, DeploymentStore store)
    { act(); return store.Get(id)!; }
```

**Note:** `DeployService.Run` is currently `private`. Change its signature to `public DeployResult Run(string workdir, string args)` so the logs endpoint can call `compose logs` (or add a public `Logs(dir, project, tail)` method mirroring `Ps` and use that instead — preferred, keeps `Run` private). Prefer adding:

```csharp
    public DeployResult Logs(string dir, string project, int tail = 200) => Run(dir, $"compose -p {project} logs --tail {tail}");
```
and call `deploy.Logs(d.ComposeDir, d.Project)` in the endpoint (drop the `deploy.Run(...)` call). Keep `Run` private.

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test tests/AspireUI.Server.Tests --filter FullyQualifiedName~HostingLockTests`
Expected: PASS.

- [ ] **Step 8: Run the full server test suite (guard against regressions in the augmented GET / lock)**

Run: `dotnet test tests/AspireUI.Server.Tests`
Expected: PASS (all).

- [ ] **Step 9: Commit**

```bash
git add src/AspireUI.Server/Endpoints/StackEndpoints.cs src/AspireUI.Server/Services/DeployService.cs tests/AspireUI.Server.Tests/HostingLockTests.cs
git commit -m "feat(hosting): deploy/stop/start/undeploy + /hosting list + logs endpoints; GET stack carries deployment; edit-lock (409) on mutating endpoints while running"
```

---

### Task 5: Frontend model + api

**Files:**
- Modify: `web/src/model.ts`
- Modify: `web/src/api.ts`

**Interfaces:**
- Produces: `interface Deployment { id: string; stackId: string; name: string; state: "deploying"|"running"|"stopped"|"failed"; urls: string[]; ... }`; `Stack.deployment?: Deployment | null`; api fns `deployStack(id)`, `stopHosting(id)`, `startHosting(id)`, `undeployHosting(id)`, `listHosting()`.

- [ ] **Step 1: Add the type + field** in `web/src/model.ts` (near `Stack`):

```typescript
export interface Deployment {
  id: string; stackId: string; name: string;
  state: "deploying" | "running" | "stopped" | "failed";
  urls: string[]; createdAt: string; updatedAt: string; lastError?: string | null;
}
```
and add to the `Stack` interface: `deployment?: Deployment | null;`

- [ ] **Step 2: Add api functions** in `web/src/api.ts`:

```typescript
export const listHosting = (): Promise<import("./model").Deployment[]> => fetch(`${base}/hosting`).then(ok);
export const deployStack = (id: string): Promise<import("./model").Deployment> =>
  fetch(`${base}/stacks/${id}/hosting/deploy`, { method: "POST" }).then(ok);
export const stopHosting = (id: string): Promise<import("./model").Deployment> =>
  fetch(`${base}/stacks/${id}/hosting/stop`, { method: "POST" }).then(ok);
export const startHosting = (id: string): Promise<import("./model").Deployment> =>
  fetch(`${base}/stacks/${id}/hosting/start`, { method: "POST" }).then(ok);
export const undeployHosting = (id: string): Promise<void> =>
  fetch(`${base}/stacks/${id}/hosting/undeploy`, { method: "POST" }).then(() => undefined);
```

- [ ] **Step 3: Build the frontend to typecheck**

Run: `cd web && npm run build`
Expected: `✓ built`.

- [ ] **Step 4: Commit**

```bash
git add web/src/model.ts web/src/api.ts
git commit -m "feat(hosting): frontend Deployment type + Stack.deployment + hosting api"
```

---

### Task 6: Hosting page + route + account-menu entry

**Files:**
- Create: `web/src/pages/Hosting.tsx`
- Modify: `web/src/App.tsx` (route)
- Modify: `web/src/auth/UserMenu.tsx` (menu entry)

**Interfaces:**
- Consumes: `api.listHosting/stopHosting/startHosting/undeployHosting`, `useTitle`, `ResourceLogDrawer` (optional; Slice-1 uses a simple logs modal — see below).

- [ ] **Step 1: Create the page**

```tsx
// web/src/pages/Hosting.tsx
import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { AppShell, Group, Title, Button, Container, Table, Badge, Anchor, ActionIcon, Menu, Text } from "@mantine/core";
import { IconArrowLeft, IconDots, IconPlayerPlay, IconPlayerStop, IconTrash, IconExternalLink, IconPencil } from "@tabler/icons-react";
import type { Deployment } from "../model";
import * as api from "../api";
import { useTitle } from "../useTitle";
import { confirmDelete, toastOk, toastErr } from "../ui";

const color = (s: Deployment["state"]) => s === "running" ? "green" : s === "failed" ? "red" : s === "deploying" ? "yellow" : "gray";

export function Hosting() {
  const nav = useNavigate();
  useTitle("Hosting");
  const [items, setItems] = useState<Deployment[]>([]);
  const load = () => api.listHosting().then(setItems).catch(() => {});
  useEffect(() => { load(); const t = setInterval(load, 4000); return () => clearInterval(t); }, []);

  const stop = (d: Deployment) => api.stopHosting(d.stackId).then(load).catch(toastErr);
  const start = (d: Deployment) => api.startHosting(d.stackId).then(load).catch(toastErr);
  const undeploy = (d: Deployment) => confirmDelete(`"${d.name}"`, "This runs docker compose down (kept volumes).")
    .then(okd => { if (okd) api.undeployHosting(d.stackId).then(load).then(() => toastOk("Undeployed")).catch(toastErr); });

  return (
    <AppShell header={{ height: 56 }} padding="lg">
      <AppShell.Header>
        <Group h="100%" px="md">
          <Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={() => nav("/")}>Stacks</Button>
          <Title order={4}>Hosting</Title>
        </Group>
      </AppShell.Header>
      <AppShell.Main>
        <Container size="lg">
          {items.length === 0
            ? <Text c="dimmed" size="sm">No stacks deployed to hosting yet. Open a stack and choose <b>Deploy to hosting</b>.</Text>
            : (
            <Table verticalSpacing="sm">
              <Table.Thead><Table.Tr>
                <Table.Th>App</Table.Th><Table.Th>Status</Table.Th><Table.Th>URLs</Table.Th><Table.Th /></Table.Tr></Table.Thead>
              <Table.Tbody>
                {items.map(d => (
                  <Table.Tr key={d.id}>
                    <Table.Td>{d.name}</Table.Td>
                    <Table.Td><Badge color={color(d.state)} variant="light">{d.state}</Badge></Table.Td>
                    <Table.Td>{d.urls.map(u => <Anchor key={u} href={u} target="_blank" mr="sm" size="sm">{u}<IconExternalLink size={12} /></Anchor>)}</Table.Td>
                    <Table.Td>
                      <Menu position="bottom-end" withArrow>
                        <Menu.Target><ActionIcon variant="subtle"><IconDots size={16} /></ActionIcon></Menu.Target>
                        <Menu.Dropdown>
                          {d.state === "running"
                            ? <Menu.Item leftSection={<IconPlayerStop size={14} />} onClick={() => stop(d)}>Stop</Menu.Item>
                            : <Menu.Item leftSection={<IconPlayerPlay size={14} />} onClick={() => start(d)}>Start</Menu.Item>}
                          <Menu.Item leftSection={<IconPencil size={14} />} onClick={() => nav(`/editor/${d.stackId}`)}>Open in editor</Menu.Item>
                          <Menu.Divider />
                          <Menu.Item color="red" leftSection={<IconTrash size={14} />} onClick={() => undeploy(d)}>Undeploy</Menu.Item>
                        </Menu.Dropdown>
                      </Menu>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>)}
        </Container>
      </AppShell.Main>
    </AppShell>
  );
}
```

- [ ] **Step 2: Add the route** in `web/src/App.tsx` (import + `<Route>`):

```tsx
import { Hosting } from "./pages/Hosting";
// ...inside <Routes>:
        <Route path="/hosting" element={<Hosting />} />
```

- [ ] **Step 3: Add the account-menu entry** in `web/src/auth/UserMenu.tsx` (after the Settings item), with an import of `IconServer` from `@tabler/icons-react`:

```tsx
          <Menu.Item leftSection={<IconServer size={14} />} onClick={() => nav("/hosting")}>Hosting</Menu.Item>
```

- [ ] **Step 4: Build**

Run: `cd web && npm run build`
Expected: `✓ built`.

- [ ] **Step 5: Commit**

```bash
git add web/src/pages/Hosting.tsx web/src/App.tsx web/src/auth/UserMenu.tsx
git commit -m "feat(hosting): Hosting page (deployed stacks — status/URLs/start/stop/undeploy/open) + route + account-menu entry"
```

---

### Task 7: Editor — Deploy action + edit-lock banner

**Files:**
- Modify: `web/src/pages/Editor.tsx`

**Interfaces:**
- Consumes: `stack.deployment` (Task 5), `api.deployStack/stopHosting`.

- [ ] **Step 1: Add a `locked` derivation + Deploy/Stop handlers** in the `Editor` component body (after `const [stack, setStackState] = ...`):

```tsx
  const locked = stack?.deployment?.state === "running" || stack?.deployment?.state === "deploying";
  const deploy = () => stack && api.deployStack(stack.id)
    .then(() => api.getStack(stack.id)).then(setStackState)
    .then(() => toastOk("Deployed to hosting")).catch(toastErr);
  const stopHosting = () => stack && api.stopHosting(stack.id)
    .then(() => api.getStack(stack.id)).then(setStackState).catch(toastErr);
```

(Import `toastOk, toastErr` from `../ui` if not already imported.)

- [ ] **Step 2: Add a Deploy button + lock banner** in the header actions group. Add next to the existing run/undo controls:

```tsx
              {!locked && <Button variant="default" size="sm" onClick={deploy}>Deploy to hosting</Button>}
```

And directly under `<AppShell.Header>` content, render a banner when locked (place above the `DockLayout` in the main area):

```tsx
      {locked && (
        <div style={{ background: "var(--mantine-color-orange-light)", padding: "6px 12px", display: "flex", alignItems: "center", gap: 12 }}>
          <span>Running in hosting — read-only. Stop it to edit.</span>
          <Button size="compact-xs" color="orange" onClick={() => confirmDelete("this deployment", "Stop it so you can edit the stack? It stays deployed (stopped).").then(okd => okd && stopHosting())}>Stop &amp; edit</Button>
        </div>
      )}
```

(Import `confirmDelete` from `../ui`. `confirmDelete` returns a boolean promise; reuse it as a generic confirm.)

- [ ] **Step 3: Make the canvas read-only when locked.** Pass `locked` into the dock/editor context so mutating actions no-op. Minimal Slice-1 approach: gate the `setStack` used by panels — wrap the context `setStack` so it's a no-op while locked (server also 409s as a backstop):

```tsx
  const guardedSetStack = useCallback((next: Stack) => { if (locked) { toastErr("Stop the hosting deployment to edit."); return; } setStack(next); }, [locked, setStack]);
```
and pass `guardedSetStack` as the context `setStack` value instead of `setStack`.

- [ ] **Step 4: Build**

Run: `cd web && npm run build`
Expected: `✓ built`.

- [ ] **Step 5: Manual verification (documented, not CI)**

1. `dotnet run -c Release --project src/AspireUI.Server`, log in.
2. Open a stack with one container (e.g. an nginx `AddContainer`), click **Deploy to hosting**.
3. Confirm: `/hosting` lists it as `running` with an `http://<host>:<port>` URL that serves the app; the container has `restart: unless-stopped` (survives an AppHost restart).
4. Back in the editor: banner shows, edits are blocked; **Stop & edit** flips it to `stopped` and unblocks.
5. `/hosting` → **Undeploy** removes it (`docker compose down`).

- [ ] **Step 6: Commit**

```bash
git add web/src/pages/Editor.tsx
git commit -m "feat(hosting): editor 'Deploy to hosting' action + read-only edit-lock banner with Stop & edit"
```

---

## Self-Review

**Spec coverage:** DeploymentStore (T1) ✓; project compose commands (T2) ✓; publish+restart+URL+lifecycle HostingService (T3) ✓; endpoints deploy/stop/start/undeploy/list/logs + GET-carries-deployment + 409 lock (T4) ✓; frontend types/api (T5) ✓; Hosting page + route + menu (T6) ✓; editor Deploy + lock banner + read-only (T7) ✓. Non-goals (ingress/RBAC/simple-shell/updates) correctly excluded.

**Placeholder scan:** none — every code step is concrete. The logs endpoint is a deliberate Slice-1 simplification (snapshot `logs --tail 200` over SSE, not a live follow) — noted, not a placeholder.

**Type consistency:** `Deployment` fields identical across store/model/endpoints (`state`, `urls`, `stackId`). `HostingService.Project(stackId)` used in both service and the lock test. `deployStack/stopHosting/...` names consistent T5↔T6↔T7. Lifecycle endpoints keyed by **stackId** (`/stacks/{id}/hosting/*`) while `/hosting` list + logs key by **deployment id** — intentional and consistent with the handlers.

**Known Slice-1 simplifications (acceptable):** per-stack (not per-service) lifecycle; logs snapshot vs live follow; URL is raw host:port. All deferred detail belongs to later slices per the spec.
