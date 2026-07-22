using System.Diagnostics;
using System.Text.Json;
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
        var compose = new ComposeImporter();
        var export = new ExportService();
        var catalog = new CatalogService();
        var templates = new TemplateService();
        var userTemplates = new UserTemplateStore(Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db"));
        var snippets = new SnippetStore(Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db"));
        var run = app.Services.GetRequiredService<RunService>();
        var graph = app.Services.GetRequiredService<ResourceGraphService>();
        var publish = new PublishService(gen);
        var deploy = new DeployService();
        var lsp = new RoslynLspService();
        // Real client by default (shared HttpClient); tests register a fake IChatClient in the
        // DI container before Build(), which this picks up instead.
        var chatClient = app.Services.GetService<IChatClient>()
            ?? new RoutingChatClient(new HttpChatClient(new HttpClient()), new CliChatClient());
        var assist = new AssistService(chatClient, catalog);
        var wsRoot = Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Path.Combine(dataDir, "workspace");

        // All app endpoints below require an authenticated session (cookie auth wired in
        // Program.cs). Anonymous endpoints (/auth/*, /env/health, SPA static files) are mapped
        // separately and never go through this group.
        var app2 = app.MapGroup("").RequireAuthorization();

        string Dir(string id) => Path.Combine(wsRoot, id);

        // Stamp creation metadata on a brand-new stack (id + who + when).
        StackModel New(StackModel s, HttpContext ctx) => s with
        {
            Id = Guid.NewGuid().ToString("n"),
            CreatedAt = DateTime.UtcNow.ToString("O"),
            CreatedBy = ctx.User.Identity?.Name ?? "admin",
        };

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

        // Test the assistant's AI backend with a tiny live round-trip (uses the settings as entered;
        // "***" apiKey means keep the stored one). Never throws — returns {ok,error} for the UI.
        app2.MapPost("/settings/test-ai", async (AppSettings body) =>
        {
            var current = settings.Get();
            var apiKey = body.AiApiKey == "***" ? current.AiApiKey : body.AiApiKey;
            var s = body with { AiApiKey = apiKey };
            var isCli = string.Equals(s.AiKind, "cli", StringComparison.OrdinalIgnoreCase);
            if (isCli && string.IsNullOrWhiteSpace(s.AiCliTool))
                return Results.Ok(new { ok = false, error = "No CLI tool selected." });
            if (!isCli && string.IsNullOrWhiteSpace(s.AiBaseUrl))
                return Results.Ok(new { ok = false, error = "Base URL is not set." });
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var reply = await chatClient.CompleteAsync(
                    "You are a connectivity probe. Reply with the JSON object {\"ok\":true}.", "ping", s);
                sw.Stop();
                return Results.Ok(new { ok = true, model = s.AiModel, ms = sw.ElapsedMilliseconds, reply });
            }
            catch (Exception ex) { return Results.Ok(new { ok = false, error = ex.Message }); }
        });

        // Whitelisted local agent CLIs the assistant can drive (for the Settings dropdown).
        app2.MapGet("/settings/ai-cli-tools", () => Results.Ok(CliChatClient.AllowedTools));

        // Try to discover available models for the entered backend (HTTP /v1/models, or `ollama list`
        // / `llm models` for CLI). Never throws — {models, error} for the UI.
        app2.MapPost("/settings/ai-models", async (AppSettings body) =>
        {
            var current = settings.Get();
            var apiKey = body.AiApiKey == "***" ? current.AiApiKey : body.AiApiKey;
            var s = body with { AiApiKey = apiKey };
            try
            {
                var isCli = string.Equals(s.AiKind, "cli", StringComparison.OrdinalIgnoreCase);
                var models = isCli
                    ? await new CliChatClient().ListModelsAsync(s)
                    : string.IsNullOrWhiteSpace(s.AiBaseUrl)
                        ? throw new InvalidOperationException("Base URL is not set.")
                        : await new HttpChatClient(new HttpClient()).ListModelsAsync(s);
                return Results.Ok(new { models, error = (string?)null });
            }
            catch (Exception ex) { return Results.Ok(new { models = new List<string>(), error = ex.Message }); }
        });

        // Custom palette snippets (reusable sub-graphs the user saved from a stack). Per-instance.
        app2.MapGet("/snippets", () => snippets.List());
        app2.MapPost("/snippets", (SnippetModel body) =>
        {
            var id = string.IsNullOrWhiteSpace(body.Id) ? "snip" + Guid.NewGuid().ToString("n")[..8] : body.Id;
            snippets.Save(body with { Id = id });
            return Results.Ok(new { id });
        });
        app2.MapDelete("/snippets/{id}", (string id) =>
            snippets.Delete(id) ? Results.NoContent() : Results.NotFound());

        // AI auto-add: fetch the URL (README/page), let the assistant write Aspire builder C# (open-minded:
        // AddGithubRepository / AddContainer / companions / wiring), then parse it into nodes+edges via the
        // normal import path for review. Never throws.
        app2.MapPost("/catalog/auto-preset", async (AutoPresetRequest body) =>
        {
            var s = settings.Get();
            if (!AiConfigured(s)) return Results.Ok(new { ok = false, reason = "AI backend not configured (see Settings)." });
            if (string.IsNullOrWhiteSpace(body.Url)) return Results.Ok(new { ok = false, reason = "No URL." });
            try
            {
                var context = await FetchUrlContext(body.Url);
                var (okr, reason, code) = await assist.AutoAddCodeAsync(body.Url, context, s);
                if (!okr || code is null) return Results.Ok(new { ok = false, reason });
                var program = $"var builder = DistributedApplication.CreateBuilder(args);\n{code}\nbuilder.Build().Run();";
                var frag = import.Import("autoadd", "autoadd", program, "");
                if (frag.Nodes.Count == 0)
                    return Results.Ok(new { ok = false, reason = "The generated code didn't parse into any resources.", code });
                return Results.Ok(new { ok = true, code, nodes = frag.Nodes, edges = frag.Edges });
            }
            catch (Exception ex) { return Results.Ok(new { ok = false, reason = ex.Message }); }
        });

        app2.MapGet("/catalog", () => catalog.GetCatalog());
        app2.MapGet("/catalog/presets", () => catalog.GetPresets());
        // Built-in demo templates + the user's own saved templates (prefixed "user:" so ids never clash).
        app2.MapGet("/templates", () => templates.List()
            .Concat(userTemplates.List().Select(t => new TemplateInfo("user:" + t.Id, t.Name, t.Description)))
            .ToList());
        // Save a stack as a reusable user template.
        app2.MapPost("/templates", (SaveTemplateRequest body) =>
        {
            if (store.Get(body.StackId) is not { } s) return Results.NotFound();
            var id = Guid.NewGuid().ToString("n");
            userTemplates.Save(id, string.IsNullOrWhiteSpace(body.Name) ? s.Name : body.Name, body.Description ?? "", s);
            return Results.Ok(new TemplateInfo("user:" + id, body.Name ?? s.Name, body.Description ?? ""));
        });
        app2.MapDelete("/templates/user/{id}", (string id) =>
            userTemplates.Delete(id) ? Results.NoContent() : Results.NotFound());
        app2.MapGet("/stacks", () => store.List());
        app2.MapGet("/stacks/{id}", (string id) =>
            store.Get(id) is { } s ? Results.Ok(s) : Results.NotFound());

        app2.MapPost("/stacks", (StackModel body, HttpContext ctx) => Persist(New(body, ctx)));

        app2.MapPost("/stacks/{id}/duplicate", (string id, HttpContext ctx) =>
            store.Get(id) is { } s
                ? Persist(New(s, ctx) with { Name = s.Name + " copy" })
                : Results.NotFound());

        app2.MapPost("/stacks/from-template/{templateId}", (string templateId, HttpContext ctx) =>
        {
            // "user:<id>" → a saved user template; otherwise a built-in demo template.
            var s = templateId.StartsWith("user:")
                ? userTemplates.Get(templateId["user:".Length..])
                : templates.Create(templateId);
            return s is not null ? Persist(New(s, ctx)) : Results.NotFound();
        });

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

        app2.MapPost("/stacks/{id}/explain", async (string id) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            var appSettings = settings.Get();
            if (!AiConfigured(appSettings))
                return Results.BadRequest("AI not configured — set it in Settings");
            try
            {
                var reply = await assist.ExplainAsync(s, appSettings);
                return Results.Ok(new { reply });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app2.MapPost("/stacks/{id}/assist", async (string id, AssistRequest body) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();

            var appSettings = settings.Get();
            if (!AiConfigured(appSettings))
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

        // Assistant "code mode": rewrite the generated Program.cs to satisfy the request, then parse it
        // back into the graph. Robust for backends that don't produce our node-graph JSON reliably.
        app2.MapPost("/stacks/{id}/assist-code", async (string id, AssistRequest body) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            var appSettings = settings.Get();
            if (!AiConfigured(appSettings)) return Results.BadRequest("AI not configured — set it in Settings");
            try
            {
                var (okr, reason, newCode) = await assist.RewriteCodeAsync(gen.GenerateProgram(s), body.Prompt, appSettings);
                if (!okr || newCode is null) return Results.UnprocessableEntity(new { reply = reason ?? "Could not apply." });
                var updated = import.Import(id, s.Name, newCode, "") with { ExtraFiles = s.ExtraFiles, ExtraPackages = s.ExtraPackages };
                var persisted = Persist(updated);
                if (persisted is IStatusCodeHttpResult { StatusCode: StatusCodes.Status422UnprocessableEntity })
                    return Results.UnprocessableEntity(new { reply = "Applied, but the code didn't compile — reverted.", errors = (persisted as IValueHttpResult)?.Value });
                return Results.Ok(new { reply = "Applied your change via code.", stack = updated });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Docker Compose import: services -> AddContainer nodes, ports/env/depends_on mapped.
        app2.MapPost("/stacks/import-compose", (ComposeRequest body, HttpContext ctx) =>
        {
            var (stack, error) = compose.Import(Guid.NewGuid().ToString("n"), body.Name, body.Yaml);
            return stack is null ? Results.UnprocessableEntity(error) : Persist(New(stack, ctx));
        });

        app2.MapPost("/stacks/{id}/import", (string id, ImportRequest req) =>
        {
            var s = import.Import(id, req.Name, req.ProgramCs, req.SidecarJson ?? "");
            return Persist(s);
        });

        // Bundle import: a whole set of source files (folder/zip contents) -> editable stack,
        // carrying extra packages (csproj) and custom code (extra .cs files) the node-graph
        // model can't represent. Text-only /stacks/{id}/import above stays for back-compat.
        app2.MapPost("/stacks/import-bundle", (ImportBundleRequest body, HttpContext ctx) =>
        {
            var id = Guid.NewGuid().ToString("n");
            var (stack, error, status) = bundle.Import(id, body.Name, body.Files, body.ProgramPath);
            if (stack is null)
                return status == StatusCodes.Status413PayloadTooLarge
                    ? Results.Text(error ?? "payload too large", "text/plain", statusCode: StatusCodes.Status413PayloadTooLarge)
                    : Results.UnprocessableEntity(error);
            return Persist(New(stack, ctx));
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
        {
            // Re-materialize fresh so the run reflects the current model and a clean single-csproj dir
            // (stale csproj from an earlier stack name would otherwise make `dotnet run` ambiguous).
            if (store.Get(id) is not { } s) return Results.NotFound();
            gen.Materialize(s, Dir(id));
            return Results.Ok(run.Start(id, Path.GetFullPath(Dir(id))));
        });
        app2.MapPost("/stacks/{id}/stop", (string id) => Results.Ok(run.Stop(id)));
        app2.MapGet("/stacks/{id}/status", (string id) => Results.Ok(run.Status(id)));
        // Read-only host filesystem browse — powers the path picker for project/script/config-path
        // params (e.g. a Deno/C# app's working directory). Authenticated (app2) only; local-first tool
        // that already shells dotnet/IDEs, so listing folders is in scope. No path → drive roots.
        app2.MapGet("/fs", (string? path) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    var roots = System.IO.DriveInfo.GetDrives().Where(d => d.IsReady)
                        .Select(d => new { name = d.RootDirectory.FullName, path = d.RootDirectory.FullName, isDir = true });
                    return Results.Ok(new { path = (string?)null, parent = (string?)null, entries = roots.ToList() });
                }
                var full = Path.GetFullPath(path);
                if (!Directory.Exists(full)) return Results.NotFound();
                var dirs = Directory.EnumerateDirectories(full).Select(d => new { name = Path.GetFileName(d), path = d, isDir = true });
                var files = Directory.EnumerateFiles(full).Select(f => new { name = Path.GetFileName(f), path = f, isDir = false });
                return Results.Ok(new
                {
                    path = full,
                    parent = Directory.GetParent(full)?.FullName,
                    entries = dirs.Concat(files).ToList(),
                });
            }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        // Live per-resource view of a running stack (state/urls/parent), from the Aspire resource service.
        app2.MapGet("/stacks/{id}/resources", (string id) => Results.Ok(graph.GetResources(id)));
        // Best-effort container CPU/memory via `docker stats` (single snapshot). Returns all running
        // containers; the client matches them to resources by name. Empty on any error / no docker.
        app2.MapGet("/stacks/{id}/stats", (string id) =>
        {
            try
            {
                var psi = new ProcessStartInfo("docker", "stats --no-stream --format \"{{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}\"")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi);
                if (p is null) return Results.Ok(Array.Empty<object>());
                var outp = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } return Results.Ok(Array.Empty<object>()); }
                var rows = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
                {
                    var c = line.Split('\t');
                    if (c.Length < 3) return null;
                    var cpu = double.TryParse(c[1].TrimEnd('%', ' '), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
                    // MemUsage looks like "12.3MiB / 1.9GiB" — take the used side, normalize to MB.
                    var used = c[2].Split('/')[0].Trim();
                    double memMb = 0;
                    var num = double.TryParse(new string(used.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray()), System.Globalization.CultureInfo.InvariantCulture, out var mv) ? mv : 0;
                    if (used.Contains("GiB", StringComparison.OrdinalIgnoreCase)) memMb = num * 1024;
                    else if (used.Contains("KiB", StringComparison.OrdinalIgnoreCase)) memMb = num / 1024;
                    else memMb = num; // MiB / MB
                    return (object)new { name = c[0].Trim(), cpu, memMb = Math.Round(memMb, 1) };
                }).Where(x => x is not null).ToList();
                return Results.Ok(rows);
            }
            catch { return Results.Ok(Array.Empty<object>()); }
        });
        // Run a resource command (Start/Stop/Restart/…) advertised by a live resource.
        app2.MapPost("/stacks/{id}/resources/{name}/command", async (string id, string name, ResourceCommandBody body, HttpContext ctx) =>
        {
            var (ok, message) = await graph.ExecuteCommandAsync(id, name, body.ResourceType ?? "", body.Command, ctx.RequestAborted);
            return ok ? Results.Ok(new { ok, message }) : Results.Json(new { ok, message }, statusCode: StatusCodes.Status502BadGateway);
        });
        // Live console-log stream for one resource (SSE). {name} is the full resource name (with suffix).
        app2.MapGet("/stacks/{id}/resources/{name}/logs", async (string id, string name, HttpContext ctx) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Append("X-Accel-Buffering", "no");
            try
            {
                await foreach (var line in graph.StreamLogsAsync(id, name, ctx.RequestAborted))
                {
                    var payload = JsonSerializer.Serialize(new { text = line.Text, stderr = line.IsStdErr, n = line.LineNumber });
                    await ctx.Response.WriteAsync($"data: {payload}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { /* client closed the EventSource */ }
        });

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

    private static readonly HttpClient Web = CreateWebClient();
    private static HttpClient CreateWebClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AspireUI-AutoAdd/1.0");
        return c;
    }

    // Gather text the AI can reason about for a project URL. For a GitHub repo, pull the README +
    // Dockerfile + docker-compose + a manifest (best-effort, main then master). Otherwise fetch the page
    // and crudely strip HTML. Capped so it fits the prompt.
    private static async Task<string> FetchUrlContext(string url)
    {
        var sb = new System.Text.StringBuilder();
        var m = System.Text.RegularExpressions.Regex.Match(url, @"github\.com/([^/\s]+)/([^/\s#?]+)");
        if (m.Success)
        {
            var owner = m.Groups[1].Value; var repo = m.Groups[2].Value.TrimEnd('/');
            if (repo.EndsWith(".git")) repo = repo[..^4];
            async Task Try(string label, string path)
            {
                foreach (var branch in new[] { "main", "master" })
                {
                    try
                    {
                        var raw = await Web.GetStringAsync($"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}");
                        if (!string.IsNullOrWhiteSpace(raw)) { sb.AppendLine($"--- {label} ---"); sb.AppendLine(raw.Length > 6000 ? raw[..6000] : raw); return; }
                    }
                    catch { /* try next branch / file */ }
                }
            }
            await Try("README.md", "README.md");
            await Try("Dockerfile", "Dockerfile");
            await Try("docker-compose.yml", "docker-compose.yml");
            await Try("package.json", "package.json");
            await Try("csproj/appsettings", "appsettings.json");
        }
        if (sb.Length == 0)
        {
            try
            {
                var html = await Web.GetStringAsync(url);
                var text = System.Text.RegularExpressions.Regex.Replace(html, "<script.*?</script>|<style.*?</style>", " ",
                    System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
                sb.AppendLine(text.Length > 8000 ? text[..8000] : text);
            }
            catch (Exception ex) { sb.AppendLine($"(Could not fetch page: {ex.Message})"); }
        }
        return sb.ToString();
    }

    // The assistant is configured when there's an HTTP base URL, or a CLI backend with a tool selected.
    private static bool AiConfigured(AppSettings s) =>
        !string.IsNullOrWhiteSpace(s.AiBaseUrl)
        || (string.Equals(s.AiKind, "cli", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s.AiCliTool));

    public record OpenIdeRequest(string Ide);
    public record ResourceCommandBody(string Command, string? ResourceType);
    public record SaveTemplateRequest(string StackId, string? Name, string? Description);
    public record ComposeRequest(string Name, string Yaml);
    public record CodeRequest(string Code, int Offset);
    public record CodeSaveRequest(string Name, string Code);
    public record AssistRequest(string Prompt);
    public record AutoPresetRequest(string Url);
    public record ImportRequest(string Name, string ProgramCs, string? SidecarJson);
    public record ImportBundleRequest(string Name, List<BundleFile> Files, string? ProgramPath);
}
