using System.Diagnostics;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

namespace AspireUI.Server.Endpoints;

public static class StackEndpoints
{
    public static void MapStackEndpoints(this WebApplication app)
    {
        // Data lives outside the project tree by default so generated stack .cs files are never
        // swept into the server's own compilation and survive rebuilds.
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
        Directory.CreateDirectory(dataDir);

        var store = new StackStore(Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db"));
        var settings = new SettingsStore(Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db"));
        var gen = new CodeGenService();
        var import = new ImportService();
        var bundle = new BundleImporter();
        var export = new ExportService();
        var catalog = new CatalogService();
        var templates = new TemplateService();
        var run = app.Services.GetRequiredService<RunService>();
        var publish = new PublishService(gen);
        var deploy = new DeployService();
        var lsp = new RoslynLspService();
        // Real client by default (shared HttpClient); tests register a fake IChatClient in the
        // DI container before Build(), which this picks up instead.
        var chatClient = app.Services.GetService<IChatClient>() ?? new HttpChatClient(new HttpClient());
        var assist = new AssistService(chatClient, catalog);
        var wsRoot = Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Path.Combine(dataDir, "workspace");

        // All app endpoints below require an authenticated session (cookie auth wired in
        // Program.cs). Anonymous endpoints (/auth/*, /env/health, SPA static files) are mapped
        // separately and never go through this group.
        var app2 = app.MapGroup("").RequireAuthorization();

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

        app2.MapGet("/settings", () =>
        {
            var s = settings.Get();
            var masked = string.IsNullOrEmpty(s.AiApiKey) ? null : "***";
            return Results.Ok(s with { AiApiKey = masked });
        });

        app2.MapPut("/settings", (AppSettings body) =>
        {
            var current = settings.Get();
            var apiKey = body.AiApiKey == "***" ? current.AiApiKey
                : string.IsNullOrEmpty(body.AiApiKey) ? null
                : body.AiApiKey;
            settings.Save(body with { AiApiKey = apiKey });
            return Results.Ok();
        });

        app2.MapGet("/catalog", () => catalog.GetCatalog());
        app2.MapGet("/templates", () => templates.List());
        app2.MapGet("/stacks", () => store.List());
        app2.MapGet("/stacks/{id}", (string id) =>
            store.Get(id) is { } s ? Results.Ok(s) : Results.NotFound());

        app2.MapPost("/stacks", (StackModel body) =>
        {
            var s = body with { Id = Guid.NewGuid().ToString("n") };
            return Persist(s);
        });

        app2.MapPost("/stacks/{id}/duplicate", (string id) =>
            store.Get(id) is { } s
                ? Persist(s with { Id = Guid.NewGuid().ToString("n"), Name = s.Name + " copy" })
                : Results.NotFound());

        app2.MapPost("/stacks/from-template/{templateId}", (string templateId) =>
            templates.Create(templateId) is { } s ? Persist(s) : Results.NotFound());

        app2.MapPut("/stacks/{id}", (string id, StackModel body) =>
            store.Get(id) is null ? Results.NotFound() : Persist(body with { Id = id }));

        app2.MapDelete("/stacks/{id}", (string id) =>
        {
            run.Stop(id);
            store.Delete(id);
            if (Directory.Exists(Dir(id))) Directory.Delete(Dir(id), true);
            return Results.NoContent();
        });

        app2.MapPatch("/stacks/{id}/nodes/{nodeId}", (string id, string nodeId, NodeModel patch) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            var idx = s.Nodes.FindIndex(n => n.Id == nodeId);
            if (idx < 0) return Results.NotFound();
            s.Nodes[idx] = patch with { Id = nodeId };
            return Persist(s);
        });

        app2.MapPost("/stacks/{id}/edges", (string id, EdgeModel edge) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            s.Edges.Add(edge with { Id = "e" + Guid.NewGuid().ToString("n")[..8] });
            return Persist(s);
        });

        app2.MapDelete("/stacks/{id}/edges/{edgeId}", (string id, string edgeId) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            s.Edges.RemoveAll(e => e.Id == edgeId);
            return Persist(s);
        });

        app2.MapGet("/stacks/{id}/export", (string id) =>
        {
            if (!Directory.Exists(Dir(id))) return Results.NotFound();
            return Results.File(export.Zip(Dir(id)), "application/zip", $"{id}.zip");
        });

        app2.MapGet("/stacks/{id}/preview", (string id) =>
            store.Get(id) is { } s ? Results.Text(gen.GenerateProgram(s), "text/plain") : Results.NotFound());

        app2.MapGet("/stacks/{id}/packages", (string id) =>
            store.Get(id) is { } s ? Results.Ok(gen.GetPackages(s)) : Results.NotFound());

        app2.MapPost("/stacks/{id}/assist", async (string id, AssistRequest body) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();

            var appSettings = settings.Get();
            if (string.IsNullOrEmpty(appSettings.AiBaseUrl))
                return Results.BadRequest("AI not configured — set it in Settings");

            AssistResult result;
            try
            {
                result = await assist.AssistAsync(s, body.Prompt, appSettings);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }

            if (!result.Ok) return Results.UnprocessableEntity(new { reply = result.Reply });

            var forced = result.Stack! with { Id = id };
            var persisted = Persist(forced);
            if (persisted is IStatusCodeHttpResult { StatusCode: StatusCodes.Status422UnprocessableEntity })
            {
                var errors = (persisted as IValueHttpResult)?.Value;
                return Results.UnprocessableEntity(new { reply = result.Reply, errors });
            }
            return Results.Ok(new { reply = result.Reply, stack = forced });
        });

        app2.MapPost("/stacks/{id}/import", (string id, ImportRequest req) =>
        {
            var s = import.Import(id, req.Name, req.ProgramCs, req.SidecarJson ?? "");
            return Persist(s);
        });

        // Bundle import: a whole set of source files (folder/zip contents) -> editable stack,
        // carrying extra packages (csproj) and custom code (extra .cs files) the node-graph
        // model can't represent. Text-only /stacks/{id}/import above stays for back-compat.
        app2.MapPost("/stacks/import-bundle", (ImportBundleRequest body) =>
        {
            var id = Guid.NewGuid().ToString("n");
            var (stack, error, status) = bundle.Import(id, body.Name, body.Files, body.ProgramPath);
            if (stack is null)
                return status == StatusCodes.Status413PayloadTooLarge
                    ? Results.Text(error ?? "payload too large", "text/plain", statusCode: StatusCodes.Status413PayloadTooLarge)
                    : Results.UnprocessableEntity(error);
            return Persist(stack);
        });

        // Open the materialized stack in a locally-installed IDE. Only meaningful when the server runs
        // on the user's own machine (the local-first default). Best-effort: tries known executables,
        // returns ok:false (not a 500) if none launch.
        app2.MapPost("/stacks/{id}/open", (string id, OpenIdeRequest r) =>
        {
            if (!Directory.Exists(Dir(id))) return Results.NotFound();
            var dir = Path.GetFullPath(Dir(id));
            var csproj = Directory.GetFiles(dir, "*.csproj").FirstOrDefault() ?? dir;
            var (target, candidates) = r.Ide switch
            {
                "vscode" => (dir, new[] { "code.cmd", "code", Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe") }),
                "rider"  => (csproj, new[] { "rider64.exe", "rider.cmd", "rider" }),
                "vs"     => (csproj, new[] { "devenv.exe", "devenv" }),
                _        => ("", Array.Empty<string>()),
            };
            if (candidates.Length == 0) return Results.BadRequest(new { message = "unknown ide" });
            foreach (var exe in candidates)
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = exe, Arguments = $"\"{target}\"", UseShellExecute = true });
                    return Results.Ok(new { ok = true });
                }
                catch { /* try next candidate */ }
            }
            return Results.Ok(new { ok = false, error = $"Could not launch {r.Ide}. Make sure it's installed and on PATH, and that AspireUI runs on your machine." });
        });

        app2.MapPost("/stacks/{id}/run", (string id) =>
            Directory.Exists(Dir(id)) ? Results.Ok(run.Start(id, Path.GetFullPath(Dir(id)))) : Results.NotFound());
        app2.MapPost("/stacks/{id}/stop", (string id) => Results.Ok(run.Stop(id)));
        app2.MapGet("/stacks/{id}/status", (string id) => Results.Ok(run.Status(id)));

        // Publish output lives OUTSIDE the run project dir (wsRoot/{id}); otherwise the run
        // project's SDK `**/*.cs` glob sweeps publish/src/Program.cs in and the build fails with
        // CS8802 (two top-level-statement files) + CS0579 (duplicate AssemblyInfo).
        string PublishRoot(string id) => Path.Combine(wsRoot, "_publish", id);
        string PublishOut(string id) => Path.Combine(PublishRoot(id), "out");
        string LegacyPublishDir(string id) => Path.Combine(wsRoot, id, "publish");

        // Generate Docker Compose artifacts via `aspire publish` (blocking; ~seconds once restore is warm).
        app2.MapPost("/stacks/{id}/publish", (string id, string? target) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            var t = target is not null && PublishService.IsTarget(target) ? target : "compose";
            // Best-effort clean; the MSBuild/compiler server can hold handles on the prior build's
            // DLLs for a while, so don't 500 if the delete fails — Materialize + aspire overwrite anyway.
            // Also purge the old nested publish dir so stacks published before the relocation can run again.
            foreach (var d in new[] { PublishRoot(id), LegacyPublishDir(id) })
                try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
            return Results.Ok(publish.Publish(s, PublishRoot(id), t));
        });

        // Deploy locally: `docker compose up -d` in the last publish output. 409 if never published.
        app2.MapPost("/stacks/{id}/deploy", (string id) =>
            File.Exists(Path.Combine(PublishOut(id), "docker-compose.yaml"))
                ? Results.Ok(deploy.Up(PublishOut(id)))
                : Results.Conflict(new { message = "publish first" }));

        // Monaco code editor: Roslyn-backed IntelliSense over the posted code (compile-only, no
        // execution). The LSP endpoints analyze the body's `code` and don't need the stack to exist;
        // /code/save persists via the existing markerless import parser.
        app2.MapPost("/stacks/{id}/code/complete", async (string id, CodeRequest r) =>
            Results.Ok(await lsp.CompleteAsync(r.Code, r.Offset)));
        app2.MapPost("/stacks/{id}/code/hover", async (string id, CodeRequest r) =>
            Results.Ok(new { contents = await lsp.HoverAsync(r.Code, r.Offset) }));
        app2.MapPost("/stacks/{id}/code/signature", async (string id, CodeRequest r) =>
            Results.Ok(await lsp.SignatureAsync(r.Code, r.Offset)));
        app2.MapPost("/stacks/{id}/code/diagnostics", (string id, CodeRequest r) =>
            Results.Ok(lsp.Diagnostics(r.Code)));
        // Whole-stack semantic validation: Roslyn diagnostics over the generated Program.cs (real
        // compile errors/warnings, not just syntax), for a canvas-level health badge.
        app2.MapGet("/stacks/{id}/validate", (string id) =>
            store.Get(id) is { } s ? Results.Ok(lsp.Diagnostics(gen.GenerateProgram(s))) : Results.NotFound());

        app2.MapPost("/stacks/{id}/code/save", (string id, CodeSaveRequest r) =>
            store.Get(id) is not { } cur ? Results.NotFound()
                // Import only reconstructs nodes/edges/raws from the code; carry over the parts the code
                // model can't represent (bundle-imported extra files + package refs) so a save doesn't wipe them.
                : Persist(import.Import(id, r.Name, r.Code, "")
                    with { ExtraFiles = cur.ExtraFiles, ExtraPackages = cur.ExtraPackages }));

        app2.MapPost("/stacks/{id}/deploy/down", (string id) =>
            Directory.Exists(PublishOut(id))
                ? Results.Ok(deploy.Down(PublishOut(id)))
                : Results.Conflict(new { message = "nothing deployed" }));
    }

    public record OpenIdeRequest(string Ide);
    public record CodeRequest(string Code, int Offset);
    public record CodeSaveRequest(string Name, string Code);
    public record AssistRequest(string Prompt);
    public record ImportRequest(string Name, string ProgramCs, string? SidecarJson);
    public record ImportBundleRequest(string Name, List<BundleFile> Files, string? ProgramPath);
}
