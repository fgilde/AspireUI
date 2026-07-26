using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

namespace AspireUI.Server.Endpoints;

public static class StackEndpoints
{
    public static void MapStackEndpoints(this WebApplication app)
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
        Directory.CreateDirectory(dataDir);

        var store = new StackStore(Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db"));
        var settings = new SettingsStore(Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db"));
        var gen = new CodeGenService();
        var import = new ImportService();
        var compose = new ComposeImporter();
        var export = new ExportService();
        var catalog = new CatalogService();
        var templates = new TemplateService();
        var userTemplates = new UserTemplateStore(Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db"));
        var snippets = new SnippetStore(Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db"));
        var deployments = new DeploymentStore(Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db"),
            onChanged: d => NotifyService.OnDeploy(settings, d));
        var apiTokens = app.Services.GetRequiredService<ApiTokenStore>();
        var run = app.Services.GetRequiredService<RunService>();
        var graph = app.Services.GetRequiredService<ResourceGraphService>();
        var publish = new PublishService(gen);
        var deploy = new DeployService();

        // A stack is locked for editing while its hosting deployment is deploying/running.
        bool Locked(string stackId) => deployments.GetByStack(stackId) is { State: "running" or "deploying" };
        IResult? LockGuard(string stackId) => Locked(stackId)
            ? Results.Json(new { message = "stack is running in hosting — stop it to edit", deployment = deployments.GetByStack(stackId) }, statusCode: StatusCodes.Status409Conflict)
            : null;
        var lsp = new RoslynLspService();
        var chatClient = app.Services.GetService<IChatClient>()
            ?? new RoutingChatClient(new HttpChatClient(new HttpClient()), new CliChatClient());
        var assist = new AssistService(chatClient, catalog);
        var wsRoot = Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Path.Combine(dataDir, "workspace");
        var proxy = new ProxyService(deploy, Path.Combine(wsRoot, "_proxy"), Environment.GetEnvironmentVariable("HOSTING_BASE_DOMAIN") ?? "localhost");
        var hosting = new HostingService(deployments, publish, deploy, proxy);
        _ = Task.Run(hosting.ReconcileOnStartup);

        var app2 = app.MapGroup("/api").RequireAuthorization();
        var docker = new DockerService(deploy);
        var devProxy = new DevProxyService(deploy);
        var dockerGrp = app2.MapGroup("/docker").RequireAuthorization(p => p.RequireRole("Admin"));
        dockerGrp.MapGet("/images", () => Results.Ok(docker.Images()));
        dockerGrp.MapGet("/volumes", () => Results.Ok(docker.Volumes()));
        dockerGrp.MapGet("/containers", () => Results.Ok(docker.Containers()));
        dockerGrp.MapDelete("/images/{id}", (string id) => { var (ok, log) = docker.RemoveImage(id); return ok ? Results.NoContent() : Results.BadRequest(new { message = log }); });
        dockerGrp.MapDelete("/containers/{id}", (string id) => { var (ok, log) = docker.RemoveContainer(id); return ok ? Results.NoContent() : Results.BadRequest(new { message = log }); });
        dockerGrp.MapDelete("/volumes/{name}", (string name) => { var (ok, log) = docker.RemoveVolume(name); return ok ? Results.NoContent() : Results.BadRequest(new { message = log }); });
        dockerGrp.MapPost("/prune", (PruneRequest b) => { var (ok, log) = docker.Prune(b.Kind ?? ""); return ok ? Results.Ok(new { log }) : Results.BadRequest(new { message = log }); });

        string Dir(string id) => Path.Combine(wsRoot, id);
        static string Uid(HttpContext ctx) => ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "";

        app2.MapGet("/api-tokens", (HttpContext ctx) => Results.Ok(apiTokens.List(Uid(ctx))));
        app2.MapPost("/api-tokens", (CreateTokenRequest b, HttpContext ctx) =>
        {
            var (token, record) = apiTokens.Create(Uid(ctx), b.Name ?? "token");
            return Results.Ok(new { token, record });
        });
        app2.MapDelete("/api-tokens/{id}", (string id, HttpContext ctx) =>
            apiTokens.Delete(id, Uid(ctx)) ? Results.NoContent() : Results.NotFound());

        StackModel New(StackModel s, HttpContext ctx) => s with
        {
            Id = Guid.NewGuid().ToString("n"),
            CreatedAt = DateTime.UtcNow.ToString("O"),
            CreatedBy = ctx.User.Identity?.Name ?? "admin",
        };

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

        app2.MapGet("/settings/ai-cli-tools", () => Results.Ok(CliChatClient.AllowedTools));

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

        app2.MapGet("/snippets", () => snippets.List());
        app2.MapPost("/snippets", (SnippetModel body) =>
        {
            var id = string.IsNullOrWhiteSpace(body.Id) ? "snip" + Guid.NewGuid().ToString("n")[..8] : body.Id;
            snippets.Save(body with { Id = id });
            return Results.Ok(new { id });
        });
        app2.MapDelete("/snippets/{id}", (string id) =>
            snippets.Delete(id) ? Results.NoContent() : Results.NotFound());

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

        app2.MapGet("/store/exclusions", () => Results.Ok(
            (settings.GetValue("StoreExclusions") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        app2.MapPut("/store/exclusions", (StoreExclusionsRequest body) =>
        {
            settings.SetValue("StoreExclusions", string.Join(",", (body.Ids ?? new()).Distinct()));
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole("Admin"));
        app2.MapGet("/templates", () => templates.List()
            .Concat(userTemplates.List().Select(t => new TemplateInfo("user:" + t.Id, t.Name, t.Description)))
            .ToList());
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
            store.Get(id) is { } s
                ? Results.Ok(new { s.Id, s.Name, s.TargetFramework, s.Nodes, s.Edges, s.RawStatements,
                    s.ExtraFiles, s.ExtraPackages, s.Notes, s.Groups, s.CreatedAt, s.CreatedBy,
                    s.HostingUrlPath, s.RunAsIs, s.AppHostProject, s.FromGit, s.HasSource,
                    deployment = deployments.GetByStack(id) })
                : Results.NotFound());

        app2.MapPost("/stacks", (StackModel body, HttpContext ctx) => Persist(New(body, ctx)));

        app2.MapPost("/stacks/{id}/duplicate", (string id, HttpContext ctx) =>
            store.Get(id) is { } s
                ? Persist(New(s, ctx) with { Name = s.Name + " copy" })
                : Results.NotFound());

        app2.MapPost("/stacks/from-template/{templateId}", (string templateId, HttpContext ctx) =>
        {
            var s = templateId.StartsWith("user:")
                ? userTemplates.Get(templateId["user:".Length..])
                : templates.Create(templateId);
            return s is not null ? Persist(New(s, ctx)) : Results.NotFound();
        });

        app2.MapPut("/stacks/{id}", (string id, StackModel body) =>
            LockGuard(id) ?? (store.Get(id) is null ? Results.NotFound() : Persist(body with { Id = id })));

        app2.MapDelete("/stacks/{id}", (string id) =>
        {
            if (LockGuard(id) is { } r) return r;
            run.Stop(id);
            if (deployments.GetByStack(id) is { } dep) hosting.Undeploy(dep.Id);
            store.Delete(id);
            void Rm(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
            Rm(Dir(id));
            Rm(Path.Combine(wsRoot, "_publish", id));
            Rm(Path.Combine(wsRoot, "_backups", id));
            if (settings.GetValue($"git:{id}") is { } cfgRaw)
            {
                try
                {
                    var opts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
                    if (System.Text.Json.JsonSerializer.Deserialize<GitStackRef>(cfgRaw, opts) is { } g) settings.SetValue($"githook:{g.Token}", null);
                }
                catch { }
                settings.SetValue($"git:{id}", null);
            }
            return Results.NoContent();
        });

        app2.MapPatch("/stacks/{id}/nodes/{nodeId}", (string id, string nodeId, NodeModel patch) =>
        {
            if (LockGuard(id) is { } r) return r;
            if (store.Get(id) is not { } s) return Results.NotFound();
            var idx = s.Nodes.FindIndex(n => n.Id == nodeId);
            if (idx < 0) return Results.NotFound();
            s.Nodes[idx] = patch with { Id = nodeId };
            return Persist(s);
        });

        app2.MapPost("/stacks/{id}/edges", (string id, EdgeModel edge) =>
        {
            if (LockGuard(id) is { } r) return r;
            if (store.Get(id) is not { } s) return Results.NotFound();
            s.Edges.Add(edge with { Id = "e" + Guid.NewGuid().ToString("n")[..8] });
            return Persist(s);
        });

        app2.MapDelete("/stacks/{id}/edges/{edgeId}", (string id, string edgeId) =>
        {
            if (LockGuard(id) is { } r) return r;
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
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            // Run-as-is imports run their original AppHost verbatim — show the real entry file, never a regenerated one.
            if (s.RunAsIs && s.AppHostProject is { } ahp)
            {
                var progDir = Path.Combine(Dir(id), Path.GetDirectoryName(ahp.Replace('/', Path.DirectorySeparatorChar)) ?? "");
                var real = ReadAppHostEntry(progDir);
                if (!string.IsNullOrEmpty(real)) return Results.Text(real, "text/plain");
            }
            return Results.Text(gen.GenerateProgram(s), "text/plain");
        });

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
                var updated = import.Import(id, s.Name, newCode, "")
                    with { ExtraFiles = s.ExtraFiles, ExtraPackages = s.ExtraPackages,
                        HasSource = s.HasSource, FromGit = s.FromGit, AppHostProject = s.AppHostProject,
                        RunAsIs = s.RunAsIs, HostingUrlPath = s.HostingUrlPath,
                        CreatedAt = s.CreatedAt, CreatedBy = s.CreatedBy };
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

        var gitJson = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        static string? MergeComposeYaml(string dir, string[]? files, Dictionary<string, string>? env)
        {
            var paths = files is { Length: > 0 }
                ? files.Select(f => Path.Combine(dir, f)).ToList()
                : (GitService.FindCompose(dir) is { } one ? new List<string> { one } : new List<string>());
            paths = paths.Where(File.Exists).ToList();
            if (paths.Count == 0) return null;
            if (env is { Count: > 0 })
                try { File.WriteAllText(Path.Combine(dir, ".env"), string.Join("\n", env.Select(kv => $"{kv.Key}={kv.Value}"))); } catch { }
            return ComposeImporter.ResolveEnv(ComposeImporter.Merge(paths.Select(File.ReadAllText).ToList()), env);
        }

        // AppHost entry point: Program.cs (classic) or AppHost.cs (Aspire 9+), else the first .cs that builds the app.
        static string ReadAppHostEntry(string projDir)
        {
            if (!Directory.Exists(projDir)) return "";
            foreach (var name in new[] { "Program.cs", "AppHost.cs" })
            {
                var p = Path.Combine(projDir, name);
                if (File.Exists(p)) return File.ReadAllText(p);
            }
            foreach (var cs in Directory.EnumerateFiles(projDir, "*.cs"))
            {
                var text = File.ReadAllText(cs);
                if (text.Contains("CreateBuilder") || text.Contains("DistributedApplication")) return text;
            }
            return "";
        }

        // Shared import core: a directory already populated with source files (via git clone or a local upload)
        // becomes a stack. Same logic for both — only how the dir gets filled differs.
        (StackModel? stack, string? error) BuildFromDir(string sid, string dir, string? mode, string name, string[]? files, string[]? services, Dictionary<string, string>? env)
        {
            var m = string.IsNullOrWhiteSpace(mode)
                ? (GitService.FindComposeFiles(dir).Count > 0 ? "compose" : "apphost")
                : mode!.ToLowerInvariant();
            if (m is "apphost" or "runasis")
            {
                var appHostProject = GitService.FindAppHostRel(dir);
                if (appHostProject is null) return (null, "no .NET Aspire AppHost project found (no .csproj referencing Aspire.Hosting.AppHost)");
                var progDir = Path.Combine(dir, Path.GetDirectoryName(appHostProject.Replace('/', Path.DirectorySeparatorChar)) ?? "");
                var programCs = ReadAppHostEntry(progDir);
                // An imported AppHost runs verbatim (RunAsIs): keep the original files, lock the editor. Nodes are a best-effort
                // parse for display only — real projects use patterns codegen can't round-trip, so we never regenerate over them.
                var s = import.Import(sid, name, programCs, "{}")
                    with { RunAsIs = true, AppHostProject = appHostProject, HasSource = true, ExtraFiles = [] };
                return (s, null);
            }
            var yaml = MergeComposeYaml(dir, files, env);
            if (yaml is null) return (null, "no docker-compose file found");
            var (cs, cerr) = compose.Import(sid, name, yaml, services is { Length: > 0 } ? services.ToHashSet() : null);
            return cs is null ? (null, cerr) : (cs with { HasSource = true, ExtraFiles = [] }, null);
        }
        IResult GitPullRedeploy(string id, HttpContext ctx)
        {
            var cfgRaw = settings.GetValue($"git:{id}");
            if (cfgRaw is null) return Results.BadRequest(new { message = "this stack was not deployed from Git" });
            var g = System.Text.Json.JsonSerializer.Deserialize<GitStackRef>(cfgRaw, gitJson)!;
            if (store.Get(id) is not { } existing) return Results.NotFound();
            var dir = Dir(id);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            var (_, cerr) = GitService.CloneInto(g.Url, g.Branch, g.Subdir, dir, g.AuthToken);
            if (cerr is not null) return Results.UnprocessableEntity(new { message = cerr });

            var (rebuilt, err) = BuildFromDir(id, dir, existing.RunAsIs ? "runasis" : "compose", existing.Name, g.Files, g.Services, g.Env);
            if (rebuilt is null) return Results.UnprocessableEntity(new { message = err });
            var updated = rebuilt with { Id = id, CreatedAt = existing.CreatedAt, CreatedBy = existing.CreatedBy, FromGit = true };
            store.Save(updated);
            gen.Materialize(updated, Dir(id));
            var redeployed = false;
            if (deployments.GetByStack(id) is not null)
            {
                var dc = DashCfg();
                hosting.Deploy(updated, PublishRoot(id), PublicHost(ctx), dc.Host, dc.Token, Path.GetFullPath(Dir(id)));
                redeployed = true;
            }
            return Results.Ok(new { stackId = id, redeployed });
        }
        app2.MapPost("/git/inspect", (GitImportRequest b) =>
        {
            var r = GitService.Inspect(b.Url, b.Branch, b.Subdir, b.AuthToken);
            return r.Error is not null ? Results.UnprocessableEntity(new { message = r.Error })
                : Results.Ok(new { r.HasCompose, r.HasAppHost, r.Name, composeFiles = r.ComposeFiles ?? new() });
        });
        app2.MapPost("/git/branches", (GitImportRequest b) =>
        {
            var (branches, error) = GitService.ListBranches(b.Url, b.AuthToken);
            return branches is null ? Results.UnprocessableEntity(new { message = error })
                : Results.Ok(new { branches });
        });
        app2.MapPost("/git/import", (GitImportRequest b, HttpContext ctx) =>
        {
            var mode = string.IsNullOrWhiteSpace(b.Mode) ? "compose" : b.Mode!.ToLowerInvariant();
            var sid = Guid.NewGuid().ToString("n");
            var dir = Dir(sid);
            void RmDir() { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }

            var (name, cerr) = GitService.CloneInto(b.Url, b.Branch, b.Subdir, dir, b.AuthToken);
            if (cerr is not null) { RmDir(); return Results.UnprocessableEntity(new { message = cerr }); }
            var stackName = string.IsNullOrWhiteSpace(b.Name) ? (name ?? "git app") : b.Name!;

            var (stack, err) = BuildFromDir(sid, dir, mode, stackName, b.Files, b.Services, b.Env);
            if (stack is null) { RmDir(); return Results.UnprocessableEntity(new { message = err }); }
            stack = stack with { FromGit = true };

            var withMeta = stack with { CreatedAt = DateTime.UtcNow.ToString("O"), CreatedBy = ctx.User.Identity?.Name ?? "admin" };
            var token = Guid.NewGuid().ToString("n");
            settings.SetValue($"git:{sid}", System.Text.Json.JsonSerializer.Serialize(new GitStackRef(b.Url, b.Branch, b.Subdir, token, b.AuthToken, b.Files, b.Env, b.Services), gitJson));
            settings.SetValue($"githook:{token}", sid);
            store.Save(withMeta);
            gen.Materialize(withMeta, Dir(sid));
            return Results.Ok(withMeta);
        });
        app2.MapGet("/stacks/{id}/git", (string id) =>
        {
            var cfgRaw = settings.GetValue($"git:{id}");
            if (cfgRaw is null) return Results.NotFound();
            var g = System.Text.Json.JsonSerializer.Deserialize<GitStackRef>(cfgRaw, gitJson)!;
            return Results.Ok(new { url = g.Url, branch = g.Branch, subdir = g.Subdir, webhookPath = $"/api/git/hook/{g.Token}" });
        });
        app2.MapPost("/stacks/{id}/git/pull", (string id, HttpContext ctx) => GitPullRedeploy(id, ctx));
        app2.MapPost("/git/hook/{token}", (string token, HttpContext ctx) =>
        {
            var sid = settings.GetValue($"githook:{token}");
            return string.IsNullOrEmpty(sid) ? Results.NotFound() : GitPullRedeploy(sid, ctx);
        }).AllowAnonymous();

        app2.MapPost("/stacks/{id}/import", (string id, ImportRequest req) =>
        {
            var s = import.Import(id, req.Name, req.ProgramCs, req.SidecarJson ?? "");
            return Persist(s);
        });

        app2.MapGet("/import/settings", () => Results.Ok(new
        {
            maxFileMb = int.TryParse(settings.GetValue("MaxImportFileMb"), out var mm) ? mm : 20,
            respectGitignore = (settings.GetValue("RespectGitignore") ?? "true") == "true",
        }));
        app2.MapPut("/import/settings", (ImportSettingsRequest b) =>
        {
            if (b.MaxFileMb is > 0) settings.SetValue("MaxImportFileMb", b.MaxFileMb.ToString());
            if (b.RespectGitignore is { } rg) settings.SetValue("RespectGitignore", rg ? "true" : "false");
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole("Admin"));

        app2.MapPost("/import/local", (LocalImportRequest b, HttpContext ctx) =>
        {
            if (b.Sources is not { Count: > 0 }) return Results.UnprocessableEntity(new { message = "no files uploaded" });
            var sid = Guid.NewGuid().ToString("n");
            var dir = Dir(sid);
            var root = Path.GetFullPath(dir);
            void RmDir() { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
            try
            {
                Directory.CreateDirectory(dir);
                foreach (var f in b.Sources)
                {
                    var full = Path.GetFullPath(Path.Combine(root, f.Path.Replace('\\', '/')));
                    if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                    byte[] bytes; try { bytes = Convert.FromBase64String(f.Content); } catch { continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllBytes(full, bytes);
                }
            }
            catch (Exception ex) { RmDir(); return Results.UnprocessableEntity(new { message = ex.Message }); }

            var stackName = string.IsNullOrWhiteSpace(b.Name) ? "imported app" : b.Name!;
            var (stack, err) = BuildFromDir(sid, dir, b.Mode, stackName, b.Files, b.Services, b.Env);
            if (stack is null) { RmDir(); return Results.UnprocessableEntity(new { message = err }); }
            var withMeta = stack with { CreatedAt = DateTime.UtcNow.ToString("O"), CreatedBy = ctx.User.Identity?.Name ?? "admin" };
            store.Save(withMeta);
            gen.Materialize(withMeta, dir);
            return Results.Ok(withMeta);
        });

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

        string PublicHost(HttpContext ctx)
        {
            var cfg = settings.GetValue("PublicHost");
            return !string.IsNullOrWhiteSpace(cfg) ? cfg! : ctx.Request.Host.Host;
        }
        RunStatus WithHost(RunStatus s, HttpContext ctx) =>
            s.DashboardUrl is null ? s : s with { DashboardUrl = HostUrls.Rewrite(s.DashboardUrl, PublicHost(ctx)) };

        app2.MapPost("/stacks/{id}/run", (string id, HttpContext ctx) =>
        {
            if (LockGuard(id) is { } r) return r;
            if (store.Get(id) is not { } s) return Results.NotFound();
            gen.Materialize(s, Dir(id));
            return Results.Ok(WithHost(run.Start(id, Path.GetFullPath(Dir(id)), s.RunAsIs ? s.AppHostProject : null), ctx));
        });
        app2.MapPost("/stacks/{id}/stop", (string id) => { devProxy.Teardown(id); return Results.Ok(run.Stop(id)); });
        app2.MapGet("/stacks/{id}/status", (string id, HttpContext ctx) => Results.Ok(WithHost(run.Status(id), ctx)));
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

        app2.MapGet("/stacks/{id}/resources", (string id, HttpContext ctx) =>
        {
            var host = PublicHost(ctx);
            var res = graph.GetResources(id).ToList();
            var byRes = new Dictionary<string, int>();
            foreach (var (r, p) in devProxy.LoopbackPorts(res.Select(x => x.Name))) byRes.TryAdd(r, p);
            if (byRes.Count > 0) devProxy.Ensure(id, host, byRes.Values);
            var mapped = res.Select(r => r with
            {
                Urls = r.Urls.Select(u => u with
                {
                    Url = byRes.TryGetValue(r.Name, out var rp) ? HostUrls.WithHostPort(u.Url, host, rp) : HostUrls.Rewrite(u.Url, host),
                }).ToList(),
            }).ToList();
            return Results.Ok(mapped);
        });
        app2.MapGet("/stacks/{id}/stats", (string id) => Results.Ok(DockerStatsSnapshot()));
        app2.MapGet("/hosting/stats", () => Results.Ok(DockerStatsSnapshot()));
        app2.MapGet("/hosting/summary", () =>
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(wsRoot) ?? "/");
                return Results.Ok(new
                {
                    diskFreeGb = Math.Round(drive.AvailableFreeSpace / 1024d / 1024 / 1024, 1),
                    diskTotalGb = Math.Round(drive.TotalSize / 1024d / 1024 / 1024, 1),
                });
            }
            catch { return Results.Ok(new { diskFreeGb = 0.0, diskTotalGb = 0.0 }); }
        });
        app2.MapPost("/stacks/{id}/resources/{name}/command", async (string id, string name, ResourceCommandBody body, HttpContext ctx) =>
        {
            var (ok, message) = await graph.ExecuteCommandAsync(id, name, body.ResourceType ?? "", body.Command, ctx.RequestAborted);
            return ok ? Results.Ok(new { ok, message }) : Results.Json(new { ok, message }, statusCode: StatusCodes.Status502BadGateway);
        });
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
            catch (OperationCanceledException) { }
        });

        string PublishRoot(string id) => Path.Combine(wsRoot, "_publish", id);
        string PublishOut(string id) => Path.Combine(PublishRoot(id), "out");
        string LegacyPublishDir(string id) => Path.Combine(wsRoot, id, "publish");

        app2.MapPost("/stacks/{id}/publish", (string id, string? target) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            var t = target is not null && PublishService.IsTarget(target) ? target : "compose";
            foreach (var d in new[] { PublishRoot(id), LegacyPublishDir(id) })
                try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
            return Results.Ok(publish.Publish(s, PublishRoot(id), t, (s.FromGit || s.HasSource) ? Path.GetFullPath(Dir(id)) : null));
        });

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
        {
            if (LockGuard(id) is { } lg) return lg;
            if (store.Get(id) is not { } cur) return Results.NotFound();
            // Import only reconstructs nodes/edges/raws from the code; carry over everything the code model
            // can't represent (extra files/packages) AND the import provenance (source dir, git, apphost) so
            // a code save doesn't strip it — otherwise deploy loses the copied source (Dockerfile, etc.).
            return Persist(import.Import(id, r.Name, r.Code, "")
                with { ExtraFiles = cur.ExtraFiles, ExtraPackages = cur.ExtraPackages,
                    HasSource = cur.HasSource, FromGit = cur.FromGit, AppHostProject = cur.AppHostProject,
                    RunAsIs = cur.RunAsIs, HostingUrlPath = cur.HostingUrlPath,
                    CreatedAt = cur.CreatedAt, CreatedBy = cur.CreatedBy });
        });

        app2.MapPost("/stacks/{id}/deploy/down", (string id) =>
            Directory.Exists(PublishOut(id))
                ? Results.Ok(deploy.Down(PublishOut(id)))
                : Results.Conflict(new { message = "nothing deployed" }));

        // --- Hosting (persistent compose deploy, tracked, separate from dev Run) ---
        // Admin-controlled: host the Aspire dashboard with each deployment? + a browser token so AspireUI
        // can hand out a one-click login link.
        (bool Host, string? Token) DashCfg() => ((settings.GetValue("HostDashboard") ?? "false") == "true", settings.GetValue("DashboardToken"));
        app2.MapPost("/stacks/{id}/hosting/deploy", (string id, HttpContext ctx) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            gen.Materialize(s, Dir(id));
            var dc = DashCfg();
            return Results.Ok(hosting.Deploy(s, PublishRoot(id), PublicHost(ctx), dc.Host, dc.Token, (s.FromGit || s.HasSource) ? Path.GetFullPath(Dir(id)) : null));
        });
        app2.MapPost("/stacks/{id}/hosting/stop", (string id) =>
        {
            if (deployments.GetByStack(id) is not { } d) return Results.NotFound();
            hosting.Stop(d.Id);
            return Results.Ok(deployments.Get(d.Id));
        });
        app2.MapPost("/stacks/{id}/hosting/start", (string id) =>
        {
            if (deployments.GetByStack(id) is not { } d) return Results.NotFound();
            hosting.Start(d.Id);
            return Results.Ok(deployments.Get(d.Id));
        });
        app2.MapPost("/stacks/{id}/hosting/restart", (string id) =>
        {
            if (deployments.GetByStack(id) is not { } d) return Results.NotFound();
            deploy.RestartProject(d.ComposeDir, d.Project);
            return Results.Ok(hosting.Refresh(d.Id) ?? deployments.Get(d.Id));
        });
        app2.MapPost("/stacks/{id}/hosting/undeploy", (string id, bool? wipe) =>
        {
            if (deployments.GetByStack(id) is not { } d) return Results.NotFound();
            hosting.Undeploy(d.Id, wipe == true);
            return Results.NoContent();
        });
        app2.MapPost("/stacks/{id}/hosting/update", (string id) =>
            deployments.GetByStack(id) is { } d ? Results.Ok(hosting.Update(d.Id)) : Results.NotFound());
        app2.MapPost("/stacks/{id}/hosting/check-updates", (string id) =>
        {
            if (deployments.GetByStack(id) is not { } d) return Results.NotFound();
            var images = deploy.ConfigImages(d.ComposeDir, d.Project).Log
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !x.Contains(' ') && (x.Contains('/') || x.Contains(':'))).Distinct().ToList();
            var results = images.Select(img =>
            {
                var pull = deploy.Docker(d.ComposeDir, $"pull {img}");
                return new { image = img, updateAvailable = pull.Log.Contains("Downloaded newer image", StringComparison.OrdinalIgnoreCase) };
            }).ToList();
            return Results.Ok(new { images = results, anyUpdate = results.Any(r => r.updateAvailable) });
        });
        string BackupsRoot() => Path.Combine(wsRoot, "_backups");
        app2.MapPost("/stacks/{id}/hosting/backup", (string id) =>
            deployments.GetByStack(id) is { } d
                ? Results.Ok(new { dir = hosting.Backup(d.Id, BackupsRoot()) })
                : Results.NotFound());
        app2.MapGet("/stacks/{id}/hosting/backups", (string id) =>
            deployments.GetByStack(id) is { } d ? Results.Ok(hosting.ListBackups(d.Id, BackupsRoot())) : Results.NotFound());
        app2.MapPost("/stacks/{id}/hosting/backups/{stamp}/restore", (string id, string stamp) =>
            deployments.GetByStack(id) is not { } d ? Results.NotFound()
                : hosting.Restore(d.Id, BackupsRoot(), stamp)
                    ? Results.Ok(hosting.Refresh(d.Id) ?? deployments.Get(d.Id))
                    : Results.BadRequest(new { message = "restore failed" }));
        app2.MapDelete("/stacks/{id}/hosting/backups/{stamp}", (string id, string stamp) =>
            deployments.GetByStack(id) is { } d && hosting.DeleteBackup(d.Id, BackupsRoot(), stamp)
                ? Results.NoContent() : Results.NotFound());
        app2.MapGet("/stacks/{id}/hosting/backups/{stamp}/download", (string id, string stamp) =>
        {
            if (deployments.GetByStack(id) is not { } d || hosting.BackupDir(d.Id, BackupsRoot(), stamp) is not { } dir)
                return Results.NotFound();
            var ms = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                foreach (var f in Directory.GetFiles(dir)) zip.CreateEntryFromFile(f, Path.GetFileName(f));
            ms.Position = 0;
            return Results.File(ms, "application/zip", $"{d.Name}-{stamp}.zip");
        });
        app2.MapGet("/hosting/{id}/services", (string id) => Results.Ok(hosting.Services(id)));
        app2.MapGet("/hosting/{id}/volumes", (string id) => Results.Ok(hosting.VolumeSizes(id)))
            .RequireAuthorization(p => p.RequireRole("Admin"));
        app2.MapGet("/hosting/{id}/volumes/{vol}/ls", (string id, string vol, string? path) =>
            Results.Ok(hosting.BrowseVolume(id, vol, path ?? "")))
            .RequireAuthorization(p => p.RequireRole("Admin"));
        app2.MapGet("/hosting/{id}/volumes/{vol}/file", (string id, string vol, string path) =>
        {
            var (data, error) = hosting.ReadVolumeFile(id, vol, path);
            if (data is null) return Results.BadRequest(new { message = error ?? "could not read file" });
            var name = path.Replace('\\', '/').Split('/').LastOrDefault() ?? "file";
            return Results.File(data, "application/octet-stream", name);
        }).RequireAuthorization(p => p.RequireRole("Admin"));
        app2.MapPost("/hosting/{id}/exec", (string id, ExecRequest b) =>
        {
            if (deployments.Get(id) is not { } d) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(b.Container) || !b.Container.StartsWith(d.Project, StringComparison.Ordinal))
                return Results.BadRequest(new { message = "container is not part of this app" });
            if (string.IsNullOrWhiteSpace(b.Cmd)) return Results.BadRequest(new { message = "empty command" });
            var r = deploy.Exec(b.Container, b.Cmd);
            return Results.Ok(new { ok = r.Ok, output = r.Log });
        }).RequireAuthorization(p => p.RequireRole("Admin"));
        app2.MapGet("/stacks/{id}/hosting/config", (string id) =>
            store.Get(id) is { } s ? Results.Ok(HostingService.NodeConfigs(s)) : Results.NotFound());
        app2.MapPost("/stacks/{id}/hosting/reconfigure", (string id, ReconfigureRequest body, HttpContext ctx) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            if (deployments.GetByStack(id) is not { } d) return Results.NotFound();
            hosting.Stop(d.Id);
            if (body.Ports is { Count: > 0 })
            {
                var merged = (d.Ports ?? new()).ToDictionary(p => p.Container);
                foreach (var o in body.Ports) merged[o.Container] = o;
                deployments.Upsert(d with { Ports = merged.Values.ToList() });
            }
            var updated = HostingService.ApplyEnvUpdates(s, body.Env ?? new());
            store.Save(updated);
            gen.Materialize(updated, Dir(id));
            var dc = DashCfg();
            return Results.Ok(hosting.Deploy(updated, PublishRoot(id), PublicHost(ctx), dc.Host, dc.Token, (updated.FromGit || updated.HasSource) ? Path.GetFullPath(Dir(id)) : null));
        });
        app2.MapGet("/hosting/dashboard-settings", (HttpContext ctx) => Results.Ok(new
        {
            hostDashboard = (settings.GetValue("HostDashboard") ?? "false") == "true",
            dashboardToken = settings.GetValue("DashboardToken") ?? "",
            publicHost = PublicHost(ctx),
            publicHostSetting = settings.GetValue("PublicHost") ?? "",
            requestHost = ctx.Request.Host.Host,
        }));
        app2.MapGet("/hosting/detect-ip", () => Results.Ok(HostUrls.CandidateIPs())).RequireAuthorization(p => p.RequireRole("Admin"));
        app2.MapPut("/hosting/dashboard-settings", (DashboardSettingsRequest b) =>
        {
            settings.SetValue("HostDashboard", b.HostDashboard ? "true" : "false");
            settings.SetValue("DashboardToken", b.DashboardToken ?? "");
            settings.SetValue("PublicHost", b.PublicHost?.Trim() ?? "");
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole("Admin"));

        NpmConfig NpmCfg() => new(
            (settings.GetValue("NpmEnabled") ?? "false") == "true",
            settings.GetValue("NpmBaseUrl") ?? "", settings.GetValue("NpmEmail") ?? "",
            settings.GetValue("NpmPassword") ?? "", settings.GetValue("NpmForwardHost") ?? "");
        app2.MapGet("/hosting/npm-settings", () => { var c = NpmCfg(); return Results.Ok(new
        {
            enabled = c.Enabled, baseUrl = c.BaseUrl, email = c.Email,
            hasPassword = !string.IsNullOrEmpty(c.Password),
        }); }).RequireAuthorization(p => p.RequireRole("Admin"));
        app2.MapPut("/hosting/npm-settings", (NpmSettingsRequest b) =>
        {
            settings.SetValue("NpmEnabled", b.Enabled ? "true" : "false");
            settings.SetValue("NpmBaseUrl", b.BaseUrl ?? "");
            settings.SetValue("NpmEmail", b.Email ?? "");
            if (b.Password is not null) settings.SetValue("NpmPassword", b.Password);
            settings.SetValue("NpmForwardHost", b.ForwardHost ?? "");
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole("Admin"));
        app2.MapPost("/hosting/npm/test", async (NpmSettingsRequest b) =>
        {
            var c = new NpmConfig(true, b.BaseUrl ?? "", b.Email ?? "", b.Password ?? (settings.GetValue("NpmPassword") ?? ""), b.ForwardHost ?? "");
            var (ok, error) = await NpmService.TestAsync(c);
            return Results.Ok(new { ok, error });
        }).RequireAuthorization(p => p.RequireRole("Admin"));
        app2.MapGet("/hosting/backup-settings", () => Results.Ok(new
        {
            intervalHours = int.TryParse(settings.GetValue("BackupIntervalHours"), out var h) ? h : 0,
            retain = int.TryParse(settings.GetValue("BackupRetain"), out var r) ? r : 7,
            lastRun = settings.GetValue("BackupLastRun"),
        })).RequireAuthorization(p => p.RequireRole("Admin"));
        app2.MapPut("/hosting/backup-settings", (BackupSettingsRequest b) =>
        {
            settings.SetValue("BackupIntervalHours", Math.Max(0, b.IntervalHours).ToString());
            settings.SetValue("BackupRetain", (b.Retain <= 0 ? 7 : b.Retain).ToString());
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole("Admin"));
        app2.MapGet("/hosting/notify-settings", () => Results.Ok(new
        {
            webhookUrl = settings.GetValue("NotifyWebhookUrl") ?? "",
            telegramToken = settings.GetValue("NotifyTelegramToken") ?? "",
            telegramChat = settings.GetValue("NotifyTelegramChat") ?? "",
        })).RequireAuthorization(p => p.RequireRole("Admin"));
        app2.MapPut("/hosting/notify-settings", (NotifySettingsRequest b) =>
        {
            settings.SetValue("NotifyWebhookUrl", b.WebhookUrl?.Trim() ?? "");
            settings.SetValue("NotifyTelegramToken", b.TelegramToken?.Trim() ?? "");
            settings.SetValue("NotifyTelegramChat", b.TelegramChat?.Trim() ?? "");
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole("Admin"));
        app2.MapPost("/hosting/notify/test", async (NotifySettingsRequest b) =>
        {
            const string title = "🔔 AspireUI test notification";
            const string body = "\nIf you can read this, notifications work.";
            var url = string.IsNullOrWhiteSpace(b.WebhookUrl) ? settings.GetValue("NotifyWebhookUrl") : b.WebhookUrl;
            var tok = string.IsNullOrWhiteSpace(b.TelegramToken) ? settings.GetValue("NotifyTelegramToken") : b.TelegramToken;
            var chat = string.IsNullOrWhiteSpace(b.TelegramChat) ? settings.GetValue("NotifyTelegramChat") : b.TelegramChat;
            var any = false; string? err = null;
            if (!string.IsNullOrWhiteSpace(url)) { any = true; var (ok, e) = await NotifyService.SendAsync(url!, title, body); err ??= ok ? null : $"webhook: {e}"; }
            if (!string.IsNullOrWhiteSpace(tok) && !string.IsNullOrWhiteSpace(chat)) { any = true; var (ok, e) = await NotifyService.SendTelegramAsync(tok!, chat!, title + body); err ??= ok ? null : $"telegram: {e}"; }
            return Results.Ok(new { ok = any && err is null, error = any ? err : "no channel configured" });
        }).RequireAuthorization(p => p.RequireRole("Admin"));
        app2.MapGet("/stacks/{id}/hosting/domain", async (string id, HttpContext ctx) =>
        {
            if (deployments.GetByStack(id) is not { } d) return Results.NotFound();
            var c = NpmCfg();
            if (!c.Enabled || string.IsNullOrWhiteSpace(c.BaseUrl)) return Results.Ok(new { configured = false });
            var port = (d.Ports ?? new()).FirstOrDefault(p => p.Public)?.Host ?? 0;
            var fwdHost = PublicHost(ctx);
            NpmProxyHost? existing = null; string? error = null;
            try { existing = (await NpmService.ListAsync(c)).FirstOrDefault(h => port > 0 && h.ForwardPort == port); }
            catch (Exception e) { error = e.Message; }
            return Results.Ok(new
            {
                configured = true, error,
                proposal = new { forwardHost = fwdHost, forwardPort = port, scheme = "http", websockets = true },
                existing,
            });
        });
        app2.MapPut("/stacks/{id}/hosting/domain", async (string id, DomainRequest b) =>
        {
            if (deployments.GetByStack(id) is null) return Results.NotFound();
            var c = NpmCfg();
            if (!c.Enabled) return Results.BadRequest(new { message = "Nginx Proxy Manager isn't configured (Settings → Hosting)." });
            try
            {
                var list = b.DomainNames ?? new();
                var certId = b.CertificateId;
                if (b.Ssl && certId <= 0)
                {
                    if (string.IsNullOrWhiteSpace(c.Email)) return Results.BadRequest(new { message = "Set the NPM account email (Settings → Hosting) — Let's Encrypt needs it." });
                    certId = await NpmService.RequestCertAsync(c, list, c.Email);
                }
                return Results.Ok(await NpmService.UpsertAsync(c, b.Id, list, b.Scheme ?? "http", b.ForwardHost, b.ForwardPort, b.Websockets, certId, b.Ssl && certId > 0));
            }
            catch (Exception e) { return Results.BadRequest(new { message = e.Message }); }
        });
        app2.MapDelete("/stacks/{id}/hosting/domain/{proxyId:int}", async (string id, int proxyId) =>
        {
            var c = NpmCfg();
            if (!c.Enabled) return Results.BadRequest(new { message = "Nginx Proxy Manager isn't configured." });
            try { await NpmService.DeleteAsync(c, proxyId); return Results.NoContent(); }
            catch (Exception e) { return Results.BadRequest(new { message = e.Message }); }
        });
        app2.MapPost("/stacks/{id}/hosting/domain/{proxyId:int}/enabled", async (string id, int proxyId, EnabledRequest b) =>
        {
            var c = NpmCfg();
            if (!c.Enabled) return Results.BadRequest(new { message = "Nginx Proxy Manager isn't configured." });
            try { await NpmService.SetEnabledAsync(c, proxyId, b.Enabled); return Results.NoContent(); }
            catch (Exception e) { return Results.BadRequest(new { message = e.Message }); }
        });

        app2.MapGet("/hosting", (HttpContext ctx) =>
        {
            var host = PublicHost(ctx);
            return Results.Ok(deployments.List().Select(d => hosting.Refresh(d.Id) ?? d)
                .Select(d => d with { Urls = d.Urls.Select(u => HostUrls.ForceHost(u, host)).ToList() }));
        });
        app2.MapGet("/hosting/{id}/logs", async (string id, HttpContext ctx) =>
        {
            if (deployments.Get(id) is not { } d) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.Headers.Append("Content-Type", "text/event-stream");
            var logs = deploy.Logs(d.ComposeDir, d.Project);
            foreach (var line in logs.Log.Split('\n'))
                await ctx.Response.WriteAsync($"data: {line}\n\n");
            await ctx.Response.Body.FlushAsync();
        });
    }

    private static List<object> DockerStatsSnapshot()
    {
        try
        {
            var psi = new ProcessStartInfo("docker", "stats --no-stream --format \"{{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return new();
            var outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } return new(); }
            return outp.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
            {
                var c = line.Split('\t');
                if (c.Length < 3) return null;
                var cpu = double.TryParse(c[1].TrimEnd('%', ' '), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
                var used = c[2].Split('/')[0].Trim();
                double memMb;
                var num = double.TryParse(new string(used.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray()), System.Globalization.CultureInfo.InvariantCulture, out var mv) ? mv : 0;
                if (used.Contains("GiB", StringComparison.OrdinalIgnoreCase)) memMb = num * 1024;
                else if (used.Contains("KiB", StringComparison.OrdinalIgnoreCase)) memMb = num / 1024;
                else memMb = num;
                return (object)new { name = c[0].Trim(), cpu, memMb = Math.Round(memMb, 1) };
            }).Where(x => x is not null).ToList()!;
        }
        catch { return new(); }
    }

    private static readonly HttpClient Web = CreateWebClient();
    private static HttpClient CreateWebClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AspireUI-AutoAdd/1.0");
        return c;
    }

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
                    catch { }
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
    public record ReconfigureRequest(Dictionary<string, List<string[]>> Env, List<AspireUI.Server.Models.PortMapping>? Ports = null);
    public record StoreExclusionsRequest(List<string>? Ids);
    public record DashboardSettingsRequest(bool HostDashboard, string? DashboardToken, string? PublicHost = null);
    public record ImportSettingsRequest(int? MaxFileMb = null, bool? RespectGitignore = null);
    public record CreateTokenRequest(string? Name);
    public record PruneRequest(string? Kind);
    public record NpmSettingsRequest(bool Enabled, string? BaseUrl, string? Email, string? Password, string? ForwardHost);
    public record DomainRequest(int? Id, List<string>? DomainNames, string? Scheme, string ForwardHost, int ForwardPort, bool Websockets, bool Ssl = false, int CertificateId = 0);
    public record NotifySettingsRequest(string? WebhookUrl, string? TelegramToken, string? TelegramChat);
    public record ExecRequest(string Container, string Cmd);
    public record BackupSettingsRequest(int IntervalHours, int Retain);
    public record GitImportRequest(string Url, string? Branch, string? Subdir, string? Name, string? Mode = null, string? AuthToken = null, string[]? Files = null, Dictionary<string, string>? Env = null, string[]? Services = null);
    public record GitStackRef(string Url, string? Branch, string? Subdir, string Token, string? AuthToken = null, string[]? Files = null, Dictionary<string, string>? Env = null, string[]? Services = null);
    public record EnabledRequest(bool Enabled);
    public record SourceFile(string Path, string Content);
    public record LocalImportRequest(string? Name, string? Mode, List<SourceFile> Sources, string[]? Files = null, string[]? Services = null, Dictionary<string, string>? Env = null);
}
