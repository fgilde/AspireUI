using System.Text;
using System.Text.Json;
using AspireUI.Server.Models;
using AspireUI.Server.Services;

namespace AspireUI.Server.Endpoints;

/// <summary>
/// The instance as a whole: take it with you (export), bring one over (import), or hand somebody a
/// support bundle when something is wrong and a screenshot is not enough.
/// </summary>
public static class InstanceEndpoints
{
    public static void MapInstanceEndpoints(this WebApplication app)
    {
        var users = app.Services.GetRequiredService<UserStore>();
        var stacks = app.Services.GetRequiredService<StackStore>();
        var settings = app.Services.GetRequiredService<SettingsStore>();
        var deployments = app.Services.GetRequiredService<DeploymentStore>();
        var audit = app.Services.GetRequiredService<AuditStore>();

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
        var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(dataDir, "aspireui.db");
        var wsRoot = Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Path.Combine(dataDir, "workspace");
        var secrets = new SecretStore(dbPath, dataDir);
        var targetStore = new TargetStore(dbPath);
        var appSources = new AppSourceService(settings, CatalogService.AppSourceCacheDir());
        var deploy = new DeployService();

        var api = app.MapGroup("/api").RequireAuthorization();

        // A POST rather than a GET: an export carries the accounts and the settings out of the
        // machine, which is an event the activity log should hold — and it only logs what changes.
        api.MapPost("/instance/export", (bool? secretsToo) =>
        {
            var doc = InstanceTransfer.Collect(users, stacks, settings, targetStore, deployments,
                appSources, secrets, secretsToo == true, Version());
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmm");
            return Results.File(InstanceTransfer.Write(doc), "application/zip", $"aspireui-instance-{stamp}.zip");
        }).RequirePerm(Perm.Settings);

        api.MapPost("/instance/import", async (HttpRequest req, bool? overwrite) =>
        {
            if (!req.HasFormContentType) return Results.BadRequest(new { message = "expected a file upload" });
            var form = await req.ReadFormAsync();
            if (form.Files.FirstOrDefault() is not { } file) return Results.BadRequest(new { message = "no file in the request" });

            await using var stream = file.OpenReadStream();
            var (doc, error) = InstanceTransfer.Read(stream);
            if (doc is null) return Results.BadRequest(new { message = error ?? "the file could not be read" });

            var report = InstanceTransfer.Apply(doc, users, stacks, settings, targetStore, appSources, secrets,
                overwrite == true);
            return Results.Ok(new
            {
                exportedAt = doc.ExportedAt, appVersion = doc.AppVersion,
                report.Users, report.Targets, report.Settings, report.AppSources, report.Stacks,
                skipped = report.Skipped,
            });
        }).RequirePerm(Perm.Settings).DisableAntiforgery();

        // What the file would do, without doing it.
        api.MapPost("/instance/import/preview", async (HttpRequest req) =>
        {
            if (!req.HasFormContentType) return Results.BadRequest(new { message = "expected a file upload" });
            var form = await req.ReadFormAsync();
            if (form.Files.FirstOrDefault() is not { } file) return Results.BadRequest(new { message = "no file in the request" });

            await using var stream = file.OpenReadStream();
            var (doc, error) = InstanceTransfer.Read(stream);
            if (doc is null) return Results.BadRequest(new { message = error ?? "the file could not be read" });

            var existingStacks = stacks.List().Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existingUsers = users.List().Select(u => u.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existingTargets = targetStore.List().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Results.Ok(new
            {
                doc.Version, doc.ExportedAt, doc.AppVersion, doc.ContainsSecrets,
                users = (doc.Users ?? []).Select(u => new { u.Username, exists = existingUsers.Contains(u.Username) }),
                targets = (doc.Targets ?? []).Where(t => !t.IsLocal).Select(t => new { t.Name, exists = existingTargets.Contains(t.Name) }),
                stacks = (doc.Stacks ?? []).Select(s => new { s.Name, exists = existingStacks.Contains(s.Name) }),
                settings = (doc.Settings ?? new()).Count,
                appSources = (doc.AppSources ?? []).Count,
            });
        }).RequirePerm(Perm.Settings).DisableAntiforgery();

        // Everything somebody needs to understand a broken install, and nothing they should not have:
        // no passwords, no tokens, no keys.
        api.MapPost("/instance/support-bundle", async () =>
        {
            var files = new Dictionary<string, string>();
            var report = new StringBuilder();
            report.AppendLine("# AspireUI support bundle");
            report.AppendLine();
            report.AppendLine($"- taken: {DateTime.UtcNow:O}");
            report.AppendLine($"- version: {Version() ?? "unknown"}");
            report.AppendLine($"- runtime: {Environment.Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}" +
                              $" ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
            report.AppendLine($"- data: {dbPath}");
            report.AppendLine($"- workspace: {wsRoot}");
            report.AppendLine();

            var health = await new EnvHealth().CheckAsync();
            report.AppendLine("## Environment");
            report.AppendLine();
            report.AppendLine($"- dotnet: {(health.Dotnet.Ok ? "ok" : "missing")} — {health.Dotnet.Detail}");
            report.AppendLine($"- docker: {(health.Docker.Ok ? "ok" : "missing")} — {health.Docker.Detail}");
            report.AppendLine($"- git: {(health.Git.Ok ? "ok" : "missing")} — {health.Git.Detail}");
            report.AppendLine($"- compose: {deploy.ComposeVersion().Log.Trim()}");
            report.AppendLine();

            report.AppendLine("## Deploy targets");
            report.AppendLine();
            foreach (var t in targetStore.List())
                report.AppendLine($"- {t.Name} ({t.Kind}){(t.Default ? " · default" : "")}" +
                                  $"{(t.Probe is { } p ? $" · {(p.Ok ? "reachable" : "unreachable")}{(p.Error is { Length: > 0 } e ? $" ({e})" : "")}" : "")}");
            report.AppendLine();

            report.AppendLine("## Apps");
            report.AppendLine();
            foreach (var d in deployments.List())
            {
                report.AppendLine($"### {d.Name}");
                report.AppendLine();
                report.AppendLine($"- state: {d.State}{(d.Health is { Length: > 0 } h ? $" · health {h}" : "")}");
                report.AppendLine($"- target: {d.TargetId ?? "local"} · project {d.Project}");
                report.AppendLine($"- urls: {string.Join(", ", d.Urls)}");
                if (d.LastError is { Length: > 0 } err) report.AppendLine($"- last error: {err.Split('\n').LastOrDefault(l => l.Trim().Length > 0)}");
                report.AppendLine();

                var slug = new string(d.Name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());
                var compose = Path.Combine(d.ComposeDir ?? "", "docker-compose.yaml");
                if (File.Exists(compose))
                    try { files[$"apps/{slug}/docker-compose.yaml"] = await File.ReadAllTextAsync(compose); } catch { }
                if (d.State == "running")
                    try { files[$"apps/{slug}/logs.txt"] = deploy.Logs(d.ComposeDir!, d.Project, 200).Log; } catch { }
            }

            files["report.md"] = report.ToString();
            // Redacted: keys and passwords are exactly what a bundle must not carry.
            files["settings.json"] = JsonSerializer.Serialize(
                settings.All().ToDictionary(kv => kv.Key, kv => Redact(kv.Key, kv.Value)),
                new JsonSerializerOptions { WriteIndented = true });
            files["activity.json"] = JsonSerializer.Serialize(audit.List(200),
                new JsonSerializerOptions { WriteIndented = true });

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmm");
            var bundle = InstanceTransfer.Write(
                new InstanceExport(InstanceTransfer.FormatVersion, DateTime.UtcNow.ToString("O"), Version(),
                    Stacks: stacks.List().Select(s => s with { ExtraFiles = [] }).ToList()),
                files);
            return Results.File(bundle, "application/zip", $"aspireui-support-{stamp}.zip");
        }).RequirePerm(Perm.Settings);
    }

    private static string? Version() =>
        typeof(InstanceEndpoints).Assembly.GetName().Version?.ToString();

    private static string Redact(string key, string value)
    {
        var k = key.ToLowerInvariant();
        var sensitive = k.Contains("password") || k.Contains("apikey") || k.Contains("token")
            || k.Contains("secret") || k.Contains("webhook");
        return sensitive && value.Length > 0 ? "***" : value;
    }
}
