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
        var dirs = new DirImporter(import, compose);
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
        var dbFile = Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db");
        var secrets = new SecretStore(dbFile, dataDir);
        var targetStore = new TargetStore(dbFile);
        var targets = new TargetService(targetStore, secrets, wsRoot);
        var provision = new ProvisionService(targetStore, targets, secrets);
        var orchestrator = new OrchestratorService(deployments, publish, targets, secrets);
        var domains = new DomainService(targetStore, secrets, settings);
        domains.MigrateGlobalNpm();
        var hosting = new HostingService(deployments, publish, deploy, proxy, targets, orchestrator);
        _ = Task.Run(hosting.ReconcileOnStartup);
        // A seed can ask for its stacks to be deployed. The seeder runs before there is any hosting to
        // deploy with, so it leaves the ids here and this picks them up once.
        _ = Task.Run(() =>
        {
            if (settings.GetValue(Seeder.PendingDeployKey) is not { Length: > 0 } raw) return;
            settings.SetValue(Seeder.PendingDeployKey, "");
            var ids = System.Text.Json.JsonSerializer.Deserialize<List<string>>(raw) ?? [];
            var host = settings.GetValue("PublicHost") is { Length: > 0 } h ? h : "localhost";
            foreach (var id in ids)
            {
                if (store.Get(id) is not { } s || deployments.GetByStack(id) is not null) continue;
                try
                {
                    gen.Materialize(s, Dir(id));
                    var dc = DashCfg();
                    hosting.Deploy(s, PublishRoot(id), host, dc.Host, dc.Token,
                        (s.FromGit || s.HasSource) ? Path.GetFullPath(Dir(id)) : null);
                }
                catch (Exception ex) { Console.Error.WriteLine($"seed: deploying {s.Name} failed — {ex.Message}"); }
            }
        });

        var app2 = app.MapGroup("/api").RequireAuthorization();
        app2.MapTargetEndpoints(targetStore, targets, secrets, deployments, provision);
        var docker = new DockerService(deploy);
        var devProxy = new DevProxyService(deploy);
        var dockerGrp = app2.MapGroup("/docker").RequirePerm(Perm.Docker);
        dockerGrp.MapGet("/images", () => Results.Ok(docker.Images()));
        dockerGrp.MapGet("/volumes", () => Results.Ok(docker.Volumes()));
        dockerGrp.MapGet("/containers", () => Results.Ok(docker.Containers()));
        dockerGrp.MapDelete("/images/{id}", (string id) => { var (ok, log) = docker.RemoveImage(id); return ok ? Results.NoContent() : Results.BadRequest(new { message = log }); });
        dockerGrp.MapDelete("/containers/{id}", (string id) => { var (ok, log) = docker.RemoveContainer(id); return ok ? Results.NoContent() : Results.BadRequest(new { message = log }); });
        dockerGrp.MapDelete("/volumes/{name}", (string name) => { var (ok, log) = docker.RemoveVolume(name); return ok ? Results.NoContent() : Results.BadRequest(new { message = log }); });
        dockerGrp.MapPost("/prune", (PruneRequest b) => { var (ok, log) = docker.Prune(b.Kind ?? ""); return ok ? Results.Ok(new { log }) : Results.BadRequest(new { message = log }); });

        string Dir(string id) => Path.Combine(wsRoot, id);
        static string Uid(HttpContext ctx) => ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "";
        var gitJson = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

        app2.MapGet("/api-tokens", (HttpContext ctx) => Results.Ok(apiTokens.List(Uid(ctx))));
        app2.MapPost("/api-tokens", (CreateTokenRequest b, HttpContext ctx) =>
        {
            var (token, record) = apiTokens.Create(Uid(ctx), b.Name ?? "token");
            return Results.Ok(new { token, record });
        });
        app2.MapDelete("/api-tokens/{id}", (string id, HttpContext ctx) =>
            apiTokens.Delete(id, Uid(ctx)) ? Results.NoContent() : Results.NotFound());

        StackModel New(StackModel s, HttpContext ctx)
        {
            var created = s with
            {
                Id = Guid.NewGuid().ToString("n"),
                CreatedAt = DateTime.UtcNow.ToString("O"),
                CreatedBy = ctx.User.Identity?.Name ?? "admin",
            };
            // The activity log reads its subject from the url, and a stack being created has no url yet.
            AuditMiddleware.Names(ctx, created.Id, created.Name);
            return created;
        }

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
        }).RequirePerm(Perm.OpenEditor);
        app2.MapDelete("/snippets/{id}", (string id) =>
            snippets.Delete(id) ? Results.NoContent() : Results.NotFound()).RequirePerm(Perm.OpenEditor);

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
        }).RequirePerm(Perm.OpenEditor);

        app2.MapGet("/catalog", () => catalog.GetCatalog());
        app2.MapGet("/catalog/presets", () => catalog.GetPresets());

        app2.MapGet("/store/exclusions", () => Results.Ok(
            (settings.GetValue("StoreExclusions") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        app2.MapPut("/store/exclusions", (StoreExclusionsRequest body) =>
        {
            settings.SetValue("StoreExclusions", string.Join(",", (body.Ids ?? new()).Distinct()));
            return Results.NoContent();
        }).RequirePerm(Perm.Store);

        // Extra app sources: admin-managed manifest URLs, fetched on demand and cached on disk.
        var appSources = new AppSourceService(settings, CatalogService.AppSourceCacheDir());
        app2.MapGet("/store/sources", () => Results.Ok(appSources.List()))
            .RequirePerm(Perm.Store);
        app2.MapPost("/store/sources", async (AppSourceRequest b) =>
        {
            var (source, error) = appSources.Add(b.Name ?? "", b.Url ?? "");
            if (source is null) return Results.BadRequest(new { message = error });
            var refreshed = await appSources.RefreshAsync(source);
            var all = appSources.List().Select(s => s.Id == refreshed.Id ? refreshed : s).ToList();
            settings.SetValue("AppSources", System.Text.Json.JsonSerializer.Serialize(all, gitJson));
            return Results.Ok(refreshed);
        }).RequirePerm(Perm.Store);
        app2.MapDelete("/store/sources/{id}", (string id) =>
            appSources.Remove(id) ? Results.NoContent() : Results.NotFound())
            .RequirePerm(Perm.Store);
        app2.MapPost("/store/sources/refresh", async () => Results.Ok(await appSources.RefreshAllAsync()))
            .RequirePerm(Perm.Store);
        app2.MapGet("/templates", () => templates.List()
            .Concat(userTemplates.List().Select(t => new TemplateInfo("user:" + t.Id, t.Name, t.Description)))
            .ToList());
        app2.MapPost("/templates", (SaveTemplateRequest body) =>
        {
            if (store.Get(body.StackId) is not { } s) return Results.NotFound();
            var id = Guid.NewGuid().ToString("n");
            userTemplates.Save(id, string.IsNullOrWhiteSpace(body.Name) ? s.Name : body.Name, body.Description ?? "", s);
            return Results.Ok(new TemplateInfo("user:" + id, body.Name ?? s.Name, body.Description ?? ""));
        }).RequirePerm(Perm.OpenEditor);
        app2.MapDelete("/templates/user/{id}", (string id) =>
            userTemplates.Delete(id) ? Results.NoContent() : Results.NotFound()).RequirePerm(Perm.OpenEditor);
        app2.MapGet("/stacks", () => store.List());
        app2.MapGet("/stacks/{id}", (string id) =>
            store.Get(id) is { } s
                ? Results.Ok(new { s.Id, s.Name, s.TargetFramework, s.Nodes, s.Edges, s.RawStatements,
                    s.ExtraFiles, s.ExtraPackages, s.Notes, s.Groups, s.CreatedAt, s.CreatedBy,
                    s.HostingUrlPath, s.RunAsIs, s.AppHostProject, s.FromGit, s.HasSource,
                    deployment = deployments.GetByStack(id) })
                : Results.NotFound());

        app2.MapPost("/stacks", (StackModel body, HttpContext ctx) => Persist(New(body, ctx))).RequirePerm(Perm.OpenEditor);

        app2.MapPost("/stacks/{id}/duplicate", (string id, HttpContext ctx) =>
            store.Get(id) is { } s
                ? Persist(New(s, ctx) with { Name = s.Name + " copy" })
                : Results.NotFound()).RequirePerm(Perm.OpenEditor);

        app2.MapPost("/stacks/from-template/{templateId}", (string templateId, HttpContext ctx) =>
        {
            var s = templateId.StartsWith("user:")
                ? userTemplates.Get(templateId["user:".Length..])
                : templates.Create(templateId);
            return s is not null ? Persist(New(s, ctx)) : Results.NotFound();
        }).RequirePerm(Perm.OpenEditor);

        app2.MapPut("/stacks/{id}", (string id, StackModel body) =>
            LockGuard(id) ?? (store.Get(id) is null ? Results.NotFound() : Persist(body with { Id = id }))).RequirePerm(Perm.OpenEditor);

        void DeleteStackFully(string id)
        {
            run.Stop(id);
            if (deployments.GetByStack(id) is { } dep) hosting.Undeploy(dep.Id, wipe: true);
            RemoveCloneHooks(id);
            RemoveAllDomainHosts(id);
            if (settings.GetValue($"clonedomain:{id}") is { } pidRaw && int.TryParse(pidRaw, out var proxyId))
            {
                var t = targetStore.Resolve(deployments.GetByStack(id)?.TargetId);
                try { domains.DeleteAsync(t, proxyId).GetAwaiter().GetResult(); } catch { }
                settings.SetValue($"clonedomain:{id}", null);
            }
            store.Delete(id);
            void Rm(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
            Rm(Dir(id));
            Rm(Path.Combine(wsRoot, "_publish", id));
            Rm(Path.Combine(wsRoot, "_backups", id));
            if (settings.GetValue($"git:{id}") is { } cfgRaw)
            {
                try
                {
                    if (System.Text.Json.JsonSerializer.Deserialize<GitStackRef>(cfgRaw, gitJson) is { } g) settings.SetValue($"githook:{g.Token}", null);
                }
                catch { }
                settings.SetValue($"git:{id}", null);
            }
        }
        app2.MapDelete("/stacks/{id}", (string id) =>
        {
            if (LockGuard(id) is { } r) return r;
            DeleteStackFully(id);
            return Results.NoContent();
        }).RequirePerm(Perm.OpenEditor);

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
        }).RequirePerm(Perm.OpenEditor);

        app2.MapDelete("/stacks/{id}/edges/{edgeId}", (string id, string edgeId) =>
        {
            if (LockGuard(id) is { } r) return r;
            if (store.Get(id) is not { } s) return Results.NotFound();
            s.Edges.RemoveAll(e => e.Id == edgeId);
            return Persist(s);
        }).RequirePerm(Perm.OpenEditor);

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
                var real = DirImporter.ReadAppHostEntry(progDir);
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
        }).RequirePerm(Perm.OpenEditor);

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
        }).RequirePerm(Perm.OpenEditor);

        // Docker Compose import: services -> AddContainer nodes, ports/env/depends_on mapped.
        app2.MapPost("/stacks/import-compose", (ComposeRequest body, HttpContext ctx) =>
        {
            var (stack, error) = compose.Import(Guid.NewGuid().ToString("n"), body.Name, body.Yaml);
            return stack is null ? Results.UnprocessableEntity(error) : Persist(New(stack, ctx));
        }).RequirePerm(Perm.OpenEditor);

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

            var (rebuilt, err) = dirs.Build(id, dir, existing.RunAsIs ? "runasis" : "compose", existing.Name, g.Files, g.Services, g.Env, g.ServicePorts);
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
            if (r.Error is not null) return Results.UnprocessableEntity(new { message = r.Error });
            var apps = r.Manifest is null ? new List<ContainerPreset>() : ManifestImporter.Parse(r.Manifest).apps;
            return Results.Ok(new
            {
                r.HasCompose, r.HasAppHost, r.Name, composeFiles = r.ComposeFiles ?? new(),
                manifest = apps.Count == 0 ? null : new { file = GitService.ManifestName, app = apps[0].Label, apps[0].Image, apps[0].Port },
            });
        });
        app2.MapPost("/git/branches", (GitImportRequest b) =>
        {
            var (branches, error) = GitService.ListBranches(b.Url, b.AuthToken);
            return branches is null ? Results.UnprocessableEntity(new { message = error })
                : Results.Ok(new { branches });
        });
        app2.MapPost("/git/import", (GitImportRequest b, HttpContext ctx) =>
        {
            // No mode = let the clone decide (aspireui-app.json, then compose, then AppHost).
            var mode = string.IsNullOrWhiteSpace(b.Mode) ? "" : b.Mode!.ToLowerInvariant();
            var sid = Guid.NewGuid().ToString("n");
            var dir = Dir(sid);
            void RmDir() { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }

            var (name, cerr) = GitService.CloneInto(b.Url, b.Branch, b.Subdir, dir, b.AuthToken);
            if (cerr is not null) { RmDir(); return Results.UnprocessableEntity(new { message = cerr }); }
            var manifestMode = mode == "manifest" || (mode.Length == 0 && GitService.FindManifest(dir) is not null);
            var stackName = string.IsNullOrWhiteSpace(b.Name) ? (manifestMode ? "" : name ?? "git app") : b.Name!;

            var (stack, err) = dirs.Build(sid, dir, mode, stackName, b.Files, b.Services, b.Env, b.ServicePorts);
            if (stack is null) { RmDir(); return Results.UnprocessableEntity(new { message = err }); }
            stack = stack with { FromGit = true };

            var withMeta = stack with { CreatedAt = DateTime.UtcNow.ToString("O"), CreatedBy = ctx.User.Identity?.Name ?? "admin" };
            var token = Guid.NewGuid().ToString("n");
            settings.SetValue($"git:{sid}", System.Text.Json.JsonSerializer.Serialize(new GitStackRef(b.Url, b.Branch, b.Subdir, token, b.AuthToken, b.Files, b.Env, b.Services, b.ServicePorts), gitJson));
            settings.SetValue($"githook:{token}", sid);
            store.Save(withMeta);
            gen.Materialize(withMeta, Dir(sid));
            return Results.Ok(withMeta);
        }).RequirePerm(Perm.OpenEditor);
        app2.MapGet("/stacks/{id}/git", (string id) =>
        {
            var cfgRaw = settings.GetValue($"git:{id}");
            if (cfgRaw is null) return Results.NotFound();
            var g = System.Text.Json.JsonSerializer.Deserialize<GitStackRef>(cfgRaw, gitJson)!;
            return Results.Ok(new { url = g.Url, branch = g.Branch, subdir = g.Subdir, webhookPath = $"/api/git/hook/{g.Token}" });
        });
        app2.MapPost("/stacks/{id}/git/pull", (string id, HttpContext ctx) => GitPullRedeploy(id, ctx)).RequirePerm(Perm.Deploy);
        app2.MapPost("/git/hook/{token}", (string token, HttpContext ctx) =>
        {
            var sid = settings.GetValue($"githook:{token}");
            return string.IsNullOrEmpty(sid) ? Results.NotFound() : GitPullRedeploy(sid, ctx);
        }).AllowAnonymous();

        // --- Clone hooks: a webhook that spins up an auto-expiring, optionally domain-bound copy of any hosted app ---
        List<string> CloneHookTokens(string stackId) =>
            settings.GetValue($"clonehooks:{stackId}") is { } raw
                ? (System.Text.Json.JsonSerializer.Deserialize<List<string>>(raw, gitJson) ?? new()) : new();
        void SaveCloneHookTokens(string stackId, List<string> toks) =>
            settings.SetValue($"clonehooks:{stackId}", System.Text.Json.JsonSerializer.Serialize(toks, gitJson));
        void RemoveCloneHooks(string stackId)
        {
            foreach (var tok in CloneHookTokens(stackId)) settings.SetValue($"clonehook:{tok}", null);
            settings.SetValue($"clonehooks:{stackId}", null);
        }
        // Track NPM proxy hosts bound to a stack so we can remove them when the app leaves hosting.
        List<int> DomainHosts(string id) => settings.GetValue($"domainhosts:{id}") is { } raw
            ? (System.Text.Json.JsonSerializer.Deserialize<List<int>>(raw, gitJson) ?? new()) : new();
        void SaveDomainHosts(string id, List<int> ids) => settings.SetValue($"domainhosts:{id}", System.Text.Json.JsonSerializer.Serialize(ids, gitJson));
        void AddDomainHost(string id, int proxyId) { var ids = DomainHosts(id); if (!ids.Contains(proxyId)) { ids.Add(proxyId); SaveDomainHosts(id, ids); } }
        void RemoveDomainHost(string id, int proxyId) { var ids = DomainHosts(id); if (ids.Remove(proxyId)) SaveDomainHosts(id, ids); }
        void RemoveAllDomainHosts(string id)
        {
            var t = targetStore.Resolve(deployments.GetByStack(id)?.TargetId);
            foreach (var pid in DomainHosts(id)) { try { domains.DeleteAsync(t, pid).GetAwaiter().GetResult(); } catch { } }
            settings.SetValue($"domainhosts:{id}", null);
        }

        app2.MapGet("/stacks/{id}/clone-hooks", (string id) => Results.Ok(new
        {
            npmConfigured = targetStore.List().Any(t => domains.Configured(t)),
            targets = targetStore.List().Select(t => new { t.Id, t.Name, domains = domains.Configured(t) }),
            hooks = CloneHookTokens(id).Select(tok =>
            {
                var cfg = System.Text.Json.JsonSerializer.Deserialize<CloneHookCfg>(settings.GetValue($"clonehook:{tok}") ?? "{}", gitJson)!;
                return new { token = tok, cfg.ExpireDays, cfg.BindDomain, cfg.DomainFormat, cfg.TargetId, webhookPath = $"/api/clone-hook/{tok}" };
            }).ToList(),
        }));
        app2.MapPost("/stacks/{id}/clone-hooks", (string id, CloneHookCfg b) =>
        {
            if (store.Get(id) is null) return Results.NotFound();
            var tok = Guid.NewGuid().ToString("n");
            settings.SetValue($"clonehook:{tok}", System.Text.Json.JsonSerializer.Serialize(b with { SourceStackId = id }, gitJson));
            var toks = CloneHookTokens(id); toks.Add(tok); SaveCloneHookTokens(id, toks);
            return Results.Ok(new { token = tok, webhookPath = $"/api/clone-hook/{tok}" });
        }).RequirePerm(Perm.Deploy);
        app2.MapDelete("/stacks/{id}/clone-hooks/{token}", (string id, string token) =>
        {
            settings.SetValue($"clonehook:{token}", null);
            var toks = CloneHookTokens(id); toks.Remove(token); SaveCloneHookTokens(id, toks);
            return Results.NoContent();
        }).RequirePerm(Perm.Deploy);

        // Anonymous by design — a registry cannot log in. The token in the path is the credential, and
        // all it can do is update apps that already run the image it names.
        app2.MapPost("/image-hook/{token}", async (string token, string? image, HttpContext ctx) =>
        {
            var expected = settings.GetValue("ImageHookToken");
            if (string.IsNullOrEmpty(expected) || token != expected) return Results.NotFound();

            var named = image;
            if (string.IsNullOrWhiteSpace(named) && ctx.Request.ContentLength is > 0)
            {
                // Docker Hub posts {"repository":{"repo_name":"acme/app"}}; GitHub and the rest each
                // have their own shape, so this reads the few fields that actually appear in the wild.
                try
                {
                    using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
                    var root = doc.RootElement;
                    named = Str(root, "image")
                        ?? (root.TryGetProperty("repository", out var repo)
                            ? Str(repo, "repo_name") ?? Str(repo, "full_name") ?? Str(repo, "name") : null)
                        ?? (root.TryGetProperty("package", out var pkg) ? Str(pkg, "name") : null)
                        ?? Str(root, "repository_name");
                }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(named))
                return Results.BadRequest(new { message = "no image in the request — pass ?image=owner/name" });

            var wanted = named!.Split(':')[0].Trim().ToLowerInvariant();
            var updated = new List<string>();
            foreach (var d in deployments.List().Where(x => x.State is "running"))
            {
                if (!hosting.ImagesOf(d.Id).Any(img => img.Split(':')[0].ToLowerInvariant().EndsWith(wanted, StringComparison.Ordinal)))
                    continue;
                try { hosting.Update(d.Id); updated.Add(d.Name); } catch { }
            }
            if (updated.Count > 0)
                _ = NotifyService.DispatchAll(settings, $"⬆️ {wanted} was pushed",
                    "\n\nUpdated: " + string.Join(", ", updated));
            return Results.Ok(new { image = wanted, updated });
        }).AllowAnonymous();

        app2.MapPost("/clone-hook/{token}", async (string token, HttpContext ctx) =>
        {
            if (settings.GetValue($"clonehook:{token}") is not { } raw) return Results.NotFound();
            var cfg = System.Text.Json.JsonSerializer.Deserialize<CloneHookCfg>(raw, gitJson)!;
            if (store.Get(cfg.SourceStackId) is not { } src) return Results.NotFound(new { message = "source stack no longer exists" });

            var newId = Guid.NewGuid().ToString("n");
            var shortId = newId[..8];
            var expireAt = cfg.ExpireDays >= 0 ? DateTime.UtcNow.AddDays(cfg.ExpireDays).ToString("O") : (string?)null;
            var clone = src with { Id = newId, Name = $"{src.Name}-{shortId}", CreatedAt = DateTime.UtcNow.ToString("O"), CreatedBy = "clone-hook", ExpireAt = expireAt, ClonedFrom = src.Id };
            try { if (Directory.Exists(Dir(src.Id))) GitService.CopyTree(Dir(src.Id), Dir(newId)); } catch { }
            store.Save(clone);
            gen.Materialize(clone, Dir(newId));

            var dc = DashCfg();
            var host = PublicHost(ctx);
            var dep = hosting.Deploy(clone, PublishRoot(newId), host, dc.Host, dc.Token,
                (clone.FromGit || clone.HasSource) ? Path.GetFullPath(Dir(newId)) : null, cfg.TargetId);
            var url = dep.Urls.FirstOrDefault();

            if (cfg.BindDomain && !string.IsNullOrWhiteSpace(cfg.DomainFormat))
            {
                var t = targetStore.Resolve(dep.TargetId);
                var port = (dep.Ports ?? new()).FirstOrDefault(p => p.Public)?.Host ?? 0;
                if (domains.Configured(t) && port > 0)
                {
                    var domain = cfg.DomainFormat!.Replace("{id}", shortId)
                        .Replace("{name}", System.Text.RegularExpressions.Regex.Replace(src.Name.ToLowerInvariant(), "[^a-z0-9-]", "-"));
                    try
                    {
                        var pr = await domains.UpsertAsync(t, null, new List<string> { domain }, "http",
                            domains.ForwardHost(t, host), port, true, false, 0);
                        settings.SetValue($"clonedomain:{newId}", pr.Id.ToString());
                        url = $"http://{domain}";
                    }
                    catch { /* domain binding failed — clone still runs on its port */ }
                }
            }
            return Results.Ok(new { stackId = newId, url, expireDate = expireAt });
        }).AllowAnonymous();

        // Sweep expired clones (auto-delete): on startup + every 30 min.
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    foreach (var s in store.List())
                        if (s.ExpireAt is { } e && DateTime.TryParse(e, null, System.Globalization.DateTimeStyles.RoundtripKind, out var due) && due <= DateTime.UtcNow)
                            DeleteStackFully(s.Id);
                }
                catch { }
                await Task.Delay(TimeSpan.FromMinutes(30));
            }
        });

        app2.MapPost("/stacks/{id}/import", (string id, ImportRequest req) =>
        {
            var s = import.Import(id, req.Name, req.ProgramCs, req.SidecarJson ?? "");
            return Persist(s);
        }).RequirePerm(Perm.OpenEditor);

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
        }).RequirePerm(Perm.Settings);

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
            var (stack, err) = dirs.Build(sid, dir, b.Mode, stackName, b.Files, b.Services, b.Env, b.ServicePorts);
            if (stack is null) { RmDir(); return Results.UnprocessableEntity(new { message = err }); }
            var withMeta = stack with { CreatedAt = DateTime.UtcNow.ToString("O"), CreatedBy = ctx.User.Identity?.Name ?? "admin" };
            store.Save(withMeta);
            gen.Materialize(withMeta, dir);
            return Results.Ok(withMeta);
        }).RequirePerm(Perm.OpenEditor);

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
        }).RequirePerm(Perm.OpenEditor);

        string PublicHost(HttpContext ctx)
        {
            var cfg = settings.GetValue("PublicHost");
            return !string.IsNullOrWhiteSpace(cfg) ? cfg! : ctx.Request.Host.Host;
        }
        // NPM forwards from ITS host to the target, so the target must never be loopback (localhost = NPM's own box).
        string ForwardHost(HttpContext ctx)
        {
            var h = PublicHost(ctx);
            return string.IsNullOrEmpty(h) || h == "localhost" || h.StartsWith("127.") || h == "::1"
                ? HostUrls.CandidateIPs().FirstOrDefault() ?? h
                : h;
        }
        RunStatus WithHost(RunStatus s, HttpContext ctx) =>
            s.DashboardUrl is null ? s : s with { DashboardUrl = HostUrls.Rewrite(s.DashboardUrl, PublicHost(ctx)) };

        app2.MapPost("/stacks/{id}/run", (string id, HttpContext ctx) =>
        {
            if (LockGuard(id) is { } r) return r;
            if (store.Get(id) is not { } s) return Results.NotFound();
            gen.Materialize(s, Dir(id));
            return Results.Ok(WithHost(run.Start(id, Path.GetFullPath(Dir(id)), s.RunAsIs ? s.AppHostProject : null), ctx));
        }).RequirePerm(Perm.OpenEditor);
        app2.MapPost("/stacks/{id}/stop", (string id) => { devProxy.Teardown(id); return Results.Ok(run.Stop(id)); }).RequirePerm(Perm.OpenEditor);
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
        }).RequirePerm(Perm.OpenEditor);
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
        }).RequirePerm(Perm.Deploy);

        app2.MapPost("/stacks/{id}/deploy", (string id) =>
            File.Exists(Path.Combine(PublishOut(id), "docker-compose.yaml"))
                ? Results.Ok(deploy.Up(PublishOut(id)))
                : Results.Conflict(new { message = "publish first" })).RequirePerm(Perm.Deploy);

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
        }).RequirePerm(Perm.OpenEditor);

        app2.MapPost("/stacks/{id}/deploy/down", (string id) =>
            Directory.Exists(PublishOut(id))
                ? Results.Ok(deploy.Down(PublishOut(id)))
                : Results.Conflict(new { message = "nothing deployed" })).RequirePerm(Perm.Deploy);

        // --- Hosting (persistent compose deploy, tracked, separate from dev Run) ---
        // Admin-controlled: host the Aspire dashboard with each deployment? + a browser token so AspireUI
        // can hand out a one-click login link.
        (bool Host, string? Token) DashCfg() => ((settings.GetValue("HostDashboard") ?? "false") == "true", settings.GetValue("DashboardToken"));
        app2.MapPost("/stacks/{id}/hosting/deploy", (string id, HttpContext ctx, HostingDeployRequest? body) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            if (body?.TargetId is { Length: > 0 } tid && targetStore.Get(tid) is null)
                return Results.BadRequest(new { message = $"unknown deploy target '{tid}'" });
            gen.Materialize(s, Dir(id));
            var dc = DashCfg();
            return Results.Ok(hosting.Deploy(s, PublishRoot(id), PublicHost(ctx), dc.Host, dc.Token,
                (s.FromGit || s.HasSource) ? Path.GetFullPath(Dir(id)) : null, body?.TargetId));
        }).RequirePerm(Perm.Deploy);
        app2.MapPost("/stacks/{id}/hosting/stop", (string id) =>
        {
            if (deployments.GetByStack(id) is not { } d) return Results.NotFound();
            hosting.Stop(d.Id);
            return Results.Ok(deployments.Get(d.Id));
        }).RequirePerm(Perm.Deploy);
        app2.MapPost("/stacks/{id}/hosting/start", (string id) =>
        {
            if (deployments.GetByStack(id) is not { } d) return Results.NotFound();
            hosting.Start(d.Id);
            return Results.Ok(deployments.Get(d.Id));
        }).RequirePerm(Perm.Deploy);
        app2.MapPost("/stacks/{id}/hosting/restart", (string id) =>
        {
            if (deployments.GetByStack(id) is not { } d) return Results.NotFound();
            deploy.RestartProject(d.ComposeDir, d.Project);
            return Results.Ok(hosting.Refresh(d.Id) ?? deployments.Get(d.Id));
        }).RequirePerm(Perm.Deploy);
        app2.MapPost("/stacks/{id}/hosting/undeploy", (string id, bool? wipe) =>
        {
            // Idempotent: no deployment = already undeployed, not an error.
            if (deployments.GetByStack(id) is { } d) hosting.Undeploy(d.Id, wipe == true);
            RemoveCloneHooks(id); // app left hosting → its clone-hooks go (already-spun-up clones stay)
            RemoveAllDomainHosts(id); // and its bound NPM domains (dead target otherwise)
            return Results.NoContent();
        }).RequirePerm(Perm.Deploy);
        app2.MapPost("/stacks/{id}/hosting/update", (string id) =>
            deployments.GetByStack(id) is { } d ? Results.Ok(hosting.Update(d.Id)) : Results.NotFound()).RequirePerm(Perm.Deploy);
        app2.MapPost("/stacks/{id}/hosting/check-updates", (string id) =>
        {
            if (deployments.GetByStack(id) is not { } d) return Results.NotFound();
            var results = hosting.CheckImages(d.Id);
            return Results.Ok(new
            {
                images = results.Select(r => new { image = r.Image, updateAvailable = r.UpdateAvailable }),
                anyUpdate = results.Any(r => r.UpdateAvailable),
            });
        });

        // Tags are for finding and acting on a group of apps, so they are not part of the code either
        // and do not wait for the app to be stopped.
        app2.MapGet("/tags", () => Results.Ok(store.List()
            .SelectMany(s => s.Tags ?? new())
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { tag = g.Key, count = g.Count() })));
        app2.MapPut("/stacks/{id}/tags", (string id, TagsRequest b) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            var clean = (b.Tags ?? new())
                .Select(t => t.Trim().Trim('#'))
                .Where(t => t.Length is > 0 and <= 32)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12).ToList();
            store.Save(s with { Tags = clean.Count == 0 ? null : clean });
            return Results.Ok(clean);
        }).RequirePerm(Perm.Configure);

        // Scheduled actions live on the stack but are not part of its code, so this does not go through
        // the edit lock: setting a nightly restart on a running app is the point of it.
        app2.MapGet("/stacks/{id}/schedules", (string id) =>
            store.Get(id) is { } s
                ? Results.Ok(new
                {
                    schedules = s.Schedules ?? new List<AppSchedule>(),
                    actions = AppScheduler.Actions,
                    lastRuns = (s.Schedules ?? new()).GroupBy(x => x.Action)
                        .ToDictionary(g => g.Key, g => settings.GetValue(AppScheduler.LastRunKey(id, g.Key))),
                })
                : Results.NotFound()).RequirePerm(Perm.Configure);
        app2.MapPut("/stacks/{id}/schedules", (string id, SchedulesRequest b) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            var clean = AppScheduler.Clean(b.Schedules);
            store.Save(s with { Schedules = clean.Count == 0 ? null : clean });
            return Results.Ok(clean);
        }).RequirePerm(Perm.Configure);
        string BackupsRoot() => Path.Combine(wsRoot, "_backups");
        app2.MapPost("/stacks/{id}/hosting/backup", (string id) =>
            deployments.GetByStack(id) is { } d
                ? Results.Ok(new { dir = hosting.Backup(d.Id, BackupsRoot()) })
                : Results.NotFound()).RequirePerm(Perm.Deploy);
        app2.MapGet("/stacks/{id}/hosting/backups", (string id) =>
            deployments.GetByStack(id) is { } d ? Results.Ok(hosting.ListBackups(d.Id, BackupsRoot())) : Results.NotFound());
        app2.MapPost("/stacks/{id}/hosting/backups/{stamp}/restore", (string id, string stamp) =>
            deployments.GetByStack(id) is not { } d ? Results.NotFound()
                : hosting.Restore(d.Id, BackupsRoot(), stamp)
                    ? Results.Ok(hosting.Refresh(d.Id) ?? deployments.Get(d.Id))
                    : Results.BadRequest(new { message = "restore failed" })).RequirePerm(Perm.Deploy);
        app2.MapDelete("/stacks/{id}/hosting/backups/{stamp}", (string id, string stamp) =>
            deployments.GetByStack(id) is { } d && hosting.DeleteBackup(d.Id, BackupsRoot(), stamp)
                ? Results.NoContent() : Results.NotFound()).RequirePerm(Perm.Deploy);
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
            .RequirePerm(Perm.Files);
        app2.MapGet("/hosting/{id}/volumes/{vol}/ls", (string id, string vol, string? path) =>
            Results.Ok(hosting.BrowseVolume(id, vol, path ?? "")))
            .RequirePerm(Perm.Files);
        // `download` keeps the old behaviour, a file the browser saves. Without it the file is served
        // inline with the content type its extension implies, which is what a viewer needs: a PDF or an
        // image handed out as application/octet-stream can only be downloaded, not shown.
        app2.MapGet("/hosting/{id}/volumes/{vol}/file", (string id, string vol, string path, bool? download) =>
        {
            var (data, error) = hosting.ReadVolumeFile(id, vol, path);
            if (data is null) return Results.BadRequest(new { message = error ?? "could not read file" });
            var name = path.Replace('\\', '/').Split('/').LastOrDefault() ?? "file";
            if (download == true) return Results.File(data, "application/octet-stream", name);
            return Results.File(data, MimeOf(name));
        }).RequirePerm(Perm.Files);
        app2.MapDelete("/hosting/{id}/volumes/{vol}/file", (string id, string vol, string path) =>
        {
            var (ok, error) = hosting.DeleteVolumeFile(id, vol, path);
            return ok ? Results.NoContent() : Results.BadRequest(new { message = error ?? "could not delete" });
        }).RequirePerm(Perm.FilesWrite);
        app2.MapPost("/hosting/{id}/volumes/{vol}/dir", (string id, string vol, VolumePathRequest b) =>
        {
            var (ok, error) = hosting.MakeVolumeDir(id, vol, b.Path);
            return ok ? Results.NoContent() : Results.BadRequest(new { message = error ?? "could not create the folder" });
        }).RequirePerm(Perm.FilesWrite);
        app2.MapPost("/hosting/{id}/volumes/{vol}/rename", (string id, string vol, VolumeRenameRequest b) =>
        {
            var (ok, error) = hosting.MoveVolumeFile(id, vol, b.From, b.To);
            return ok ? Results.NoContent() : Results.BadRequest(new { message = error ?? "could not rename" });
        }).RequirePerm(Perm.FilesWrite);
        // Multipart, streamed straight into the container's stdin: an upload never lands on this disk.
        app2.MapPost("/hosting/{id}/volumes/{vol}/upload", async (string id, string vol, string? path, HttpRequest req) =>
        {
            if (!req.HasFormContentType) return Results.BadRequest(new { message = "expected a file upload" });
            var form = await req.ReadFormAsync();
            if (form.Files.Count == 0) return Results.BadRequest(new { message = "no file in the request" });
            var written = new List<string>();
            foreach (var file in form.Files)
            {
                var name = Path.GetFileName(file.FileName);
                if (string.IsNullOrWhiteSpace(name)) continue;
                await using var stream = file.OpenReadStream();
                var rel = string.IsNullOrWhiteSpace(path) ? name : path!.TrimEnd('/') + "/" + name;
                var (ok, error) = hosting.WriteVolumeFile(id, vol, rel, stream);
                if (!ok) return Results.BadRequest(new { message = error ?? $"could not write {name}" });
                written.Add(name);
            }
            return Results.Ok(new { written });
        }).RequirePerm(Perm.FilesWrite).DisableAntiforgery();
        app2.MapPost("/hosting/{id}/exec", (string id, ExecRequest b) =>
        {
            if (deployments.Get(id) is not { } d) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(b.Cmd)) return Results.BadRequest(new { message = "empty command" });
            // Fresh: a one-off container from the service's image — the only way into a service that
            // crash-loops or is stopped, since `docker exec` needs a running container.
            if (b.Fresh == true)
            {
                if (string.IsNullOrWhiteSpace(b.Service) || !System.Text.RegularExpressions.Regex.IsMatch(b.Service, @"^[A-Za-z0-9][A-Za-z0-9._-]*$"))
                    return Results.BadRequest(new { message = "pick a compose service" });
                var one = targets.Runner(d.TargetId).RunOneOff(d.ComposeDir, d.Project, b.Service!, b.Cmd);
                return Results.Ok(new { ok = one.Ok, output = one.Log });
            }
            // An orchestrator target has no container names: the shell goes through its own tool.
            if (hosting.IsOrchestrated(d))
            {
                var k = orchestrator.Exec(d, b.Cmd);
                return Results.Ok(new { ok = k.Ok, output = k.Log });
            }
            if (string.IsNullOrWhiteSpace(b.Container) || !b.Container.StartsWith(d.Project, StringComparison.Ordinal))
                return Results.BadRequest(new { message = "container is not part of this app" });
            var r = targets.Runner(d.TargetId).Exec(b.Container, b.Cmd);
            return Results.Ok(new { ok = r.Ok, output = r.Log });
        }).RequirePerm(Perm.Terminal);
        // Compose services of an app, running or not — the terminal needs them for a one-off repair container.
        app2.MapGet("/hosting/{id}/compose-services", (string id) =>
            deployments.Get(id) is { } d
                ? Results.Ok(targets.Runner(d.TargetId).Docker(d.ComposeDir, $"compose -p {d.Project} config --services").Log
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())
                    .Where(s => s.Length > 0 && !s.Contains(' ') && !s.Contains("dashboard")).ToList())
                : Results.NotFound()).RequirePerm(Perm.Terminal);
        app2.MapGet("/stacks/{id}/hosting/config", (string id) =>
            store.Get(id) is { } s ? Results.Ok(HostingService.NodeConfigs(s)) : Results.NotFound()).RequirePerm(Perm.Configure);
        // What the app may use and how it reports healthy. Deploy writes both into the compose file,
        // so changing them means a redeploy — which is what reconfigure already does.
        app2.MapGet("/stacks/{id}/hosting/runtime", (string id) =>
            store.Get(id) is { } s
                ? Results.Ok(new
                {
                    limits = s.Limits ?? new AppLimits(),
                    healthchecks = s.Healthchecks ?? new List<AppHealthcheck>(),
                    services = HostingService.NodeConfigs(s).Select(n => n.Name).ToList(),
                })
                : Results.NotFound()).RequirePerm(Perm.Configure);
        // What a redeploy would change. The stack is published into a throwaway directory and put
        // through exactly the same post-processing Deploy uses, including the app's existing port
        // mapping — otherwise the diff would be a page of port noise and nothing else.
        app2.MapPost("/stacks/{id}/hosting/diff", (string id) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            if (deployments.GetByStack(id) is not { } d) return Results.NotFound();
            var deployed = Path.Combine(d.ComposeDir ?? "", "docker-compose.yaml");
            if (!File.Exists(deployed)) return Results.BadRequest(new { message = "this app has never been deployed" });

            var tmp = Path.Combine(wsRoot, "_diff", id);
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            try
            {
                gen.Materialize(s, Dir(id));
                var pub = publish.Publish(s, tmp, "compose", (s.FromGit || s.HasSource) ? Path.GetFullPath(Dir(id)) : null);
                if (!pub.Ok) return Results.UnprocessableEntity(new { message = pub.Log });

                var dc = DashCfg();
                var next = HostingService.ApplyRuntime(
                    HostingService.InjectDockerfileBuilds(
                        HostingService.EnsureCompanionDatabases(
                            HostingService.ConfigureDashboard(
                                HostingService.AddRestartPolicy(File.ReadAllText(Path.Combine(pub.OutputDir, "docker-compose.yaml"))),
                                dc.Host, dc.Token)),
                        s, Path.Combine(tmp, "src")),
                    s.Limits, s.Healthchecks);
                var ports = (d.Ports ?? new()).Where(p => p.Public && p.Host > 0).ToDictionary(p => p.Container, p => p.Host);
                var keepInternal = (d.Ports ?? new()).Where(p => !p.Public).Select(p => p.Container).ToHashSet();
                next = HostingService.PublishExposedPorts(next, ports, keepInternal);

                var diff = TextDiff.Unified(File.ReadAllText(deployed), next);
                return Results.Ok(new { diff.Changed, diff.Added, diff.Removed, diff.Text });
            }
            finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } }
        }).RequirePerm(Perm.Deploy);

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
            // Null means "leave it alone"; an empty Limits object is how the UI clears the caps.
            if (body.Limits is not null) updated = updated with { Limits = body.Limits.IsEmpty ? null : body.Limits };
            if (body.Healthchecks is not null)
                updated = updated with
                {
                    Healthchecks = body.Healthchecks.Where(h => !string.IsNullOrWhiteSpace(h.Test)).ToList() is { Count: > 0 } keep
                        ? keep : null,
                };
            store.Save(updated);
            gen.Materialize(updated, Dir(id));
            var dc = DashCfg();
            return Results.Ok(hosting.Deploy(updated, PublishRoot(id), PublicHost(ctx), dc.Host, dc.Token, (updated.FromGit || updated.HasSource) ? Path.GetFullPath(Dir(id)) : null));
        }).RequirePerm(Perm.Configure);
        // Move an app to another target: data goes with it unless asked otherwise.
        app2.MapPost("/stacks/{id}/hosting/move", (string id, MoveRequest b, HttpContext ctx) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            if (deployments.GetByStack(id) is null) return Results.Conflict(new { message = "this stack is not deployed" });
            if (targetStore.Get(b.TargetId) is null) return Results.BadRequest(new { message = $"unknown target '{b.TargetId}'" });
            gen.Materialize(s, Dir(id));
            var dc = DashCfg();
            var r = hosting.Move(s, PublishRoot(id), PublicHost(ctx), dc.Host, dc.Token,
                (s.FromGit || s.HasSource) ? Path.GetFullPath(Dir(id)) : null, b.TargetId, b.WithData);
            return r.Ok ? Results.Ok(new { r.Ok, r.Log, deployment = r.Deployment })
                : Results.BadRequest(new { message = r.Log, deployment = r.Deployment });
        }).RequirePerm(Perm.Deploy);

        // Copy an app to another target: a second, independent instance (its own stack, its own data).
        app2.MapPost("/stacks/{id}/hosting/copy", (string id, MoveRequest b, HttpContext ctx) =>
        {
            if (store.Get(id) is not { } src) return Results.NotFound();
            if (targetStore.Get(b.TargetId) is not { } t) return Results.BadRequest(new { message = $"unknown target '{b.TargetId}'" });
            var srcDep = deployments.GetByStack(id);
            var newId = Guid.NewGuid().ToString("n");
            var clone = src with
            {
                Id = newId,
                Name = $"{src.Name} @ {t.Name}",
                CreatedAt = DateTime.UtcNow.ToString("O"),
                CreatedBy = "copy",
                ClonedFrom = src.Id,
            };
            try { if (Directory.Exists(Dir(src.Id))) GitService.CopyTree(Dir(src.Id), Dir(newId)); } catch { }
            store.Save(clone);
            gen.Materialize(clone, Dir(newId));
            var dc = DashCfg();
            var dep = hosting.Deploy(clone, PublishRoot(newId), PublicHost(ctx), dc.Host, dc.Token,
                (clone.FromGit || clone.HasSource) ? Path.GetFullPath(Dir(newId)) : null, t.Id);
            var log = "";
            if (b.WithData && srcDep is not null && dep.State != "failed")
                log = hosting.CopyData(srcDep, dep).Log;
            return Results.Ok(new { stackId = newId, deployment = dep, log });
        }).RequirePerm(Perm.Deploy);

        app2.MapGet("/hosting/dashboard-settings", (HttpContext ctx) => Results.Ok(new
        {
            hostDashboard = (settings.GetValue("HostDashboard") ?? "false") == "true",
            dashboardToken = settings.GetValue("DashboardToken") ?? "",
            publicHost = PublicHost(ctx),
            publicHostSetting = settings.GetValue("PublicHost") ?? "",
            requestHost = ctx.Request.Host.Host,
        }));
        app2.MapGet("/hosting/detect-ip", () => Results.Ok(HostUrls.CandidateIPs())).RequirePerm(Perm.Settings);

        // --- Image webhook: a registry says an image moved, the apps using it update themselves ---
        string ImageHookToken()
        {
            if (settings.GetValue("ImageHookToken") is { Length: > 0 } existing) return existing;
            var fresh = Guid.NewGuid().ToString("n");
            settings.SetValue("ImageHookToken", fresh);
            return fresh;
        }
        app2.MapGet("/hosting/image-hook", (HttpContext ctx) => Results.Ok(new
        {
            url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/api/image-hook/{ImageHookToken()}",
            token = ImageHookToken(),
        })).RequirePerm(Perm.Settings);
        app2.MapPost("/hosting/image-hook/rotate", () =>
        {
            settings.SetValue("ImageHookToken", "");
            return Results.Ok(new { token = ImageHookToken() });
        }).RequirePerm(Perm.Settings);
        app2.MapPut("/hosting/dashboard-settings", (DashboardSettingsRequest b) =>
        {
            settings.SetValue("HostDashboard", b.HostDashboard ? "true" : "false");
            settings.SetValue("DashboardToken", b.DashboardToken ?? "");
            settings.SetValue("PublicHost", b.PublicHost?.Trim() ?? "");
            return Results.NoContent();
        }).RequirePerm(Perm.Settings);

        // Kept at their old paths, now backed by the local target's domain configuration: "this machine"
        // is a target like any other, and its Nginx Proxy Manager lives with it.
        DeployTarget LocalTarget() => targetStore.Resolve(DeployTarget.LocalId);
        app2.MapGet("/hosting/npm-configured", () => Results.Ok(new
        {
            configured = domains.Configured(LocalTarget()),
            anyTarget = targetStore.List().Any(t => domains.Configured(t)),
            targets = targetStore.List().Count,
        }));
        app2.MapGet("/hosting/npm-settings", () =>
        {
            var t = LocalTarget();
            var n = t.Domains?.Npm;
            return Results.Ok(new
            {
                enabled = domains.KindOf(t) == DomainService.KindNpm,
                baseUrl = n?.BaseUrl ?? "", email = n?.Email ?? "",
                hasPassword = !string.IsNullOrEmpty(n?.PasswordRef),
                forwardHost = n?.ForwardHost ?? "",
            });
        }).RequirePerm(Perm.Settings);
        app2.MapPut("/hosting/npm-settings", (NpmSettingsRequest b) =>
        {
            var t = LocalTarget();
            var cur = t.Domains?.Npm;
            var pwRef = b.Password is { Length: > 0 } ? secrets.Replace(cur?.PasswordRef, b.Password, "npm password (this machine)") : cur?.PasswordRef;
            targetStore.Upsert(t with
            {
                Domains = new TargetDomains(b.Enabled ? DomainService.KindNpm : DomainService.KindNone,
                    new TargetNpm(b.BaseUrl ?? cur?.BaseUrl ?? "", b.Email ?? cur?.Email ?? "", pwRef,
                        b.ForwardHost ?? cur?.ForwardHost ?? "")),
            });
            return Results.NoContent();
        }).RequirePerm(Perm.Settings);
        app2.MapPost("/hosting/npm/test", async (NpmSettingsRequest b) =>
        {
            var cur = LocalTarget().Domains?.Npm;
            var pw = b.Password is { Length: > 0 } ? b.Password : secrets.Resolve(cur?.PasswordRef) ?? "";
            var (ok, error) = await NpmService.TestAsync(new NpmConfig(true, b.BaseUrl ?? "", b.Email ?? "", pw, b.ForwardHost ?? ""));
            return Results.Ok(new { ok, error });
        }).RequirePerm(Perm.Settings);
        app2.MapGet("/hosting/backup-settings", () => Results.Ok(new
        {
            intervalHours = int.TryParse(settings.GetValue("BackupIntervalHours"), out var h) ? h : 0,
            retain = int.TryParse(settings.GetValue("BackupRetain"), out var r) ? r : 7,
            lastRun = settings.GetValue("BackupLastRun"),
        })).RequirePerm(Perm.Settings);
        app2.MapPut("/hosting/backup-settings", (BackupSettingsRequest b) =>
        {
            settings.SetValue("BackupIntervalHours", Math.Max(0, b.IntervalHours).ToString());
            settings.SetValue("BackupRetain", (b.Retain <= 0 ? 7 : b.Retain).ToString());
            return Results.NoContent();
        }).RequirePerm(Perm.Settings);
        app2.MapGet("/hosting/notify-settings", () => Results.Ok(new
        {
            webhookUrl = settings.GetValue("NotifyWebhookUrl") ?? "",
            telegramToken = settings.GetValue("NotifyTelegramToken") ?? "",
            telegramChat = settings.GetValue("NotifyTelegramChat") ?? "",
        })).RequirePerm(Perm.Settings);
        app2.MapPut("/hosting/notify-settings", (NotifySettingsRequest b) =>
        {
            settings.SetValue("NotifyWebhookUrl", b.WebhookUrl?.Trim() ?? "");
            settings.SetValue("NotifyTelegramToken", b.TelegramToken?.Trim() ?? "");
            settings.SetValue("NotifyTelegramChat", b.TelegramChat?.Trim() ?? "");
            return Results.NoContent();
        }).RequirePerm(Perm.Settings);
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
        }).RequirePerm(Perm.Settings);
        app2.MapGet("/stacks/{id}/hosting/domain", async (string id, HttpContext ctx) =>
        {
            if (deployments.GetByStack(id) is not { } d) return Results.NotFound();
            var t = targetStore.Resolve(d.TargetId);
            if (!domains.Configured(t))
                return Results.Ok(new { configured = false, kind = domains.KindOf(t), target = t.Name });
            var port = (d.Ports ?? new()).FirstOrDefault(p => p.Public)?.Host ?? 0;
            var fwdHost = domains.ForwardHost(t, PublicHost(ctx));
            NpmProxyHost? existing = null; string? error = null;
            try { existing = (await domains.ListAsync(t, cached: false)).FirstOrDefault(h => port > 0 && h.ForwardPort == port); }
            catch (Exception e) { error = e.Message; }
            return Results.Ok(new
            {
                configured = true, error, kind = domains.KindOf(t), target = t.Name,
                manual = domains.KindOf(t) == DomainService.KindManual,
                proposal = new { forwardHost = fwdHost, forwardPort = port, scheme = "http", websockets = true },
                existing,
            });
        });
        app2.MapPut("/stacks/{id}/hosting/domain", async (string id, DomainRequest b) =>
        {
            if (deployments.GetByStack(id) is not { } dep) return Results.NotFound();
            var t = targetStore.Resolve(dep.TargetId);
            if (!domains.Configured(t))
                return Results.BadRequest(new { message = $"'{t.Name}' has no domain provider configured (Settings → Deploy targets)." });
            try
            {
                var pr = await domains.UpsertAsync(t, b.Id, b.DomainNames ?? new(), b.Scheme ?? "http",
                    b.ForwardHost, b.ForwardPort, b.Websockets, b.Ssl, b.CertificateId);
                AddDomainHost(id, pr.Id);
                return Results.Ok(pr);
            }
            catch (Exception e) { return Results.BadRequest(new { message = e.Message }); }
        }).RequirePerm(Perm.Configure);
        app2.MapDelete("/stacks/{id}/hosting/domain/{proxyId:int}", async (string id, int proxyId, string? hostname) =>
        {
            var t = targetStore.Resolve(deployments.GetByStack(id)?.TargetId);
            try { await domains.DeleteAsync(t, proxyId, hostname); RemoveDomainHost(id, proxyId); return Results.NoContent(); }
            catch (Exception e) { return Results.BadRequest(new { message = e.Message }); }
        }).RequirePerm(Perm.Configure);
        app2.MapPost("/stacks/{id}/hosting/domain/{proxyId:int}/enabled", async (string id, int proxyId, EnabledRequest b) =>
        {
            var t = targetStore.Resolve(deployments.GetByStack(id)?.TargetId);
            try { await domains.SetEnabledAsync(t, proxyId, b.Enabled); return Results.NoContent(); }
            catch (Exception e) { return Results.BadRequest(new { message = e.Message }); }
        }).RequirePerm(Perm.Configure);

        // Every target answers for its own apps: its own proxy for domains, and its own address for
        // URLs — rewriting a remote app's URL to the request host would point at the wrong machine.
        app2.MapGet("/hosting", async (HttpContext ctx) =>
        {
            var host = PublicHost(ctx);
            var list = deployments.List().Select(d => hosting.Refresh(d.Id) ?? d).ToList();
            var hostsByTarget = new Dictionary<string, List<NpmProxyHost>>();
            foreach (var tid in list.Select(d => d.Target).Distinct())
                hostsByTarget[tid] = await domains.ListAsync(targetStore.Resolve(tid));
            return Results.Ok(list.Select(d =>
            {
                var t = targetStore.Resolve(d.TargetId);
                return d with
                {
                    Urls = t.IsLocal ? d.Urls.Select(u => HostUrls.ForceHost(u, host)).ToList() : d.Urls,
                    Domains = HostingService.DomainUrls(hostsByTarget.GetValueOrDefault(d.Target) ?? new(), d),
                    TargetName = t.Name, TargetKind = t.Kind, TargetCompose = TargetKind.IsCompose(t.Kind),
                    Tags = store.Get(d.StackId)?.Tags,
                };
            }));
        });
        app2.MapGet("/hosting/{id}/logs", async (string id, HttpContext ctx) =>
        {
            if (deployments.Get(id) is not { } d) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.Headers.Append("Content-Type", "text/event-stream");
            var logs = hosting.IsOrchestrated(d)
                ? orchestrator.Logs(d)
                : targets.Runner(d.TargetId).Logs(d.ComposeDir, d.Project);
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

    private static readonly Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider Mimes = new();
    // Anything the table does not know stays a byte stream: a wrong content type makes a browser render
    // a file as something it is not, which is worse than offering it for download.
    private static string MimeOf(string name) =>
        Mimes.TryGetContentType(name, out var mime) ? mime : "application/octet-stream";

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
    public record ReconfigureRequest(Dictionary<string, List<string[]>> Env, List<AspireUI.Server.Models.PortMapping>? Ports = null,
        AspireUI.Server.Models.AppLimits? Limits = null, List<AspireUI.Server.Models.AppHealthcheck>? Healthchecks = null);
    public record StoreExclusionsRequest(List<string>? Ids);
    public record AppSourceRequest(string? Name, string? Url);
    public record DashboardSettingsRequest(bool HostDashboard, string? DashboardToken, string? PublicHost = null);
    public record ImportSettingsRequest(int? MaxFileMb = null, bool? RespectGitignore = null);
    public record CreateTokenRequest(string? Name);
    public record PruneRequest(string? Kind);
    public record NpmSettingsRequest(bool Enabled, string? BaseUrl, string? Email, string? Password, string? ForwardHost);
    public record DomainRequest(int? Id, List<string>? DomainNames, string? Scheme, string ForwardHost, int ForwardPort, bool Websockets, bool Ssl = false, int CertificateId = 0);
    public record NotifySettingsRequest(string? WebhookUrl, string? TelegramToken, string? TelegramChat);
    public record SchedulesRequest(List<AspireUI.Server.Models.AppSchedule>? Schedules);
    public record TagsRequest(List<string>? Tags);

    private static string? Str(System.Text.Json.JsonElement e, string name) =>
        e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
    public record VolumePathRequest(string Path);
    public record VolumeRenameRequest(string From, string To);
    public record ExecRequest(string Container, string Cmd, string? Service = null, bool? Fresh = null);
    public record BackupSettingsRequest(int IntervalHours, int Retain);
    public record GitImportRequest(string Url, string? Branch, string? Subdir, string? Name, string? Mode = null, string? AuthToken = null, string[]? Files = null, Dictionary<string, string>? Env = null, string[]? Services = null, Dictionary<string, int>? ServicePorts = null);
    public record GitStackRef(string Url, string? Branch, string? Subdir, string Token, string? AuthToken = null, string[]? Files = null, Dictionary<string, string>? Env = null, string[]? Services = null, Dictionary<string, int>? ServicePorts = null);
    public record CloneHookCfg(string SourceStackId = "", int ExpireDays = 7, bool BindDomain = false, string? DomainFormat = null, string? TargetId = null);
    public record EnabledRequest(bool Enabled);
    public record HostingDeployRequest(string? TargetId = null);
    public record MoveRequest(string TargetId, bool WithData = true, bool KeepSource = false);
    public record SourceFile(string Path, string Content);
    public record LocalImportRequest(string? Name, string? Mode, List<SourceFile> Sources, string[]? Files = null, string[]? Services = null, Dictionary<string, string>? Env = null, Dictionary<string, int>? ServicePorts = null);
}
