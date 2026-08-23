using System.Text;
using System.Text.Json;
using AspireUI.Server.Models;
using YamlDotNet.Serialization;

namespace AspireUI.Server.Services;

// One compose service becomes one managed service: a Container App, a Cloud Run service, an ECS service.
// The compose file `aspire publish` produces is the source of truth — image, environment, ports — and
// every CLI call is written into the deploy log so a failure can be replayed by hand.
//
// The honest limits, surfaced in the UI as well:
//   * no volumes. These platforms have no local disk that survives a revision; an app that needs one
//     belongs on a compose target (or gets a managed database wired in by hand).
//   * images must be pullable by the platform. A stack that builds from a Dockerfile needs a registry
//     on the target, which is then used to push before deploying.
//   * Container Apps resolves other apps in the same environment by name, so compose's service names
//     keep working. Cloud Run and ECS do not: there, multi-service stacks need the URLs wired by hand.
public static class ManagedDeploy
{
    public record ComposeService(string Name, string Image, Dictionary<string, string> Env, List<int> Ports, bool HasVolumes);

    public static List<ComposeService> ReadCompose(string path)
    {
        var list = new List<ComposeService>();
        if (!File.Exists(path)) return list;
        var root = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(File.ReadAllText(path));
        if (root is null || !root.TryGetValue("services", out var svcs) || svcs is not Dictionary<object, object> map) return list;
        foreach (var (nameObj, defObj) in map)
        {
            var name = nameObj?.ToString() ?? "";
            if (name.Contains("dashboard", StringComparison.OrdinalIgnoreCase)) continue;   // our own sidecar
            if (defObj is not Dictionary<object, object> def) continue;
            var image = def.TryGetValue("image", out var img) ? img?.ToString() ?? "" : "";
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (def.TryGetValue("environment", out var e))
            {
                if (e is Dictionary<object, object> em)
                    foreach (var (k, v) in em) env[k?.ToString() ?? ""] = v?.ToString() ?? "";
                else if (e is List<object> el)
                    foreach (var item in el)
                    {
                        var s = item?.ToString() ?? "";
                        var i = s.IndexOf('=');
                        if (i > 0) env[s[..i]] = s[(i + 1)..];
                    }
            }
            var ports = new List<int>();
            if (def.TryGetValue("ports", out var p) && p is List<object> pl)
                foreach (var item in pl)
                {
                    var s = item?.ToString() ?? "";
                    var last = s.Split(':').LastOrDefault() ?? s;
                    if (int.TryParse(last.Split('/')[0], out var n)) ports.Add(n);
                }
            if (def.TryGetValue("expose", out var ex) && ex is List<object> exl)
                foreach (var item in exl)
                    if (int.TryParse((item?.ToString() ?? "").Split('/')[0], out var n) && !ports.Contains(n)) ports.Add(n);
            var hasVolumes = def.ContainsKey("volumes");
            list.Add(new ComposeService(name, image, env, ports, hasVolumes));
        }
        return list;
    }

    private static (string Dir, List<ComposeService> Services, string Log, bool Ok) Prepare(
        OrchestratorService o, StackModel stack, string publishRoot, string? cloneSrc)
    {
        var pub = o.Publisher.Publish(stack, publishRoot, "compose", cloneSrc);
        if (!pub.Ok) return ("", [], pub.Log, false);
        var path = Path.Combine(pub.OutputDir, "docker-compose.yaml");
        var services = ReadCompose(path);
        return (pub.OutputDir, services, pub.Log, services.Count > 0);
    }

    // Dockerfile-built services have no image a cloud platform can pull: push them to the target's registry.
    private static string? PushImages(OrchestratorService o, DeployTarget t, List<ComposeService> services, StringBuilder log)
    {
        var needsPush = services.Where(s => !s.Image.Contains('/') || s.Image.StartsWith("aspireui-", StringComparison.OrdinalIgnoreCase)).ToList();
        if (needsPush.Count == 0) return null;
        if (t.Registry?.Url is not { Length: > 0 } reg)
            return $"{needsPush[0].Image} is a locally built image — set a registry on this target so it can be pushed";
        var local = o.Targets.Runner(DeployTarget.LocalId);
        if (t.Registry.User is { Length: > 0 } user && o.Secrets.Resolve(t.Registry.PasswordRef) is { Length: > 0 } pw)
            log.Append(local.Login(reg, user, pw).Log);
        foreach (var s in needsPush)
        {
            var remote = $"{reg.TrimEnd('/')}/{s.Image.Split('/').Last()}";
            log.Append(local.Tag(s.Image, remote).Log);
            var push = local.Push(remote);
            log.Append(push.Log);
            if (!push.Ok) return $"could not push {remote}";
        }
        return null;
    }

    private static string Rewrite(string image, DeployTarget t) =>
        t.Registry?.Url is { Length: > 0 } reg && !image.Contains('/')
            ? $"{reg.TrimEnd('/')}/{image}" : image;

    // ---------- Azure Container Apps ----------

    public static Deployment Aca(OrchestratorService o, StackModel stack, string publishRoot, string id, DeployTarget t, string? cloneSrc)
    {
        var log = new StringBuilder();
        var rg = t.Cloud?.ResourceGroup;
        if (string.IsNullOrWhiteSpace(rg)) return o.Fail(id, "this target has no resource group");
        var envName = t.Cloud?.Environment is { Length: > 0 } e ? e : "aspireui";
        var loc = t.Cloud?.Location is { Length: > 0 } l ? l : "westeurope";
        var cli = CloudCli.EnvFor(t, o.Secrets);

        var prep = Prepare(o, stack, publishRoot, cloneSrc);
        log.Append(prep.Log);
        if (!prep.Ok) return o.Fail(id, OrchestratorService.Tail(log.ToString()));
        if (PushImages(o, t, prep.Services, log) is { } pushErr) return o.Fail(id, pushErr);

        log.Append(Cli.Run("az", ["extension", "add", "--name", "containerapp", "--upgrade", "--only-show-errors", "-o", "none"], cli, timeoutMs: 300_000).Log);
        log.Append(Cli.Run("az", ["group", "create", "-n", rg!, "-l", loc, "-o", "none"], cli, timeoutMs: 300_000).Log);
        var envShow = Cli.Run("az", ["containerapp", "env", "show", "-g", rg!, "-n", envName, "-o", "none"], cli);
        if (!envShow.Ok)
        {
            log.AppendLine($"creating container apps environment {envName}");
            var envCreate = Cli.Run("az", ["containerapp", "env", "create", "-g", rg!, "-n", envName, "-l", loc, "-o", "none"], cli, timeoutMs: 900_000);
            log.Append(envCreate.Log);
            if (!envCreate.Ok) return o.Fail(id, OrchestratorService.Tail(log.ToString()));
        }

        var prefix = OrchestratorService.SafeName(stack.Name);
        var urls = new List<string>();
        var ok = true;
        foreach (var s in prep.Services)
        {
            var app = OrchestratorService.SafeName($"{prefix}-{s.Name}");
            var port = s.Ports.FirstOrDefault();
            // The first service with a port gets public ingress; the rest stay inside the environment,
            // where compose's own service names keep resolving.
            var external = port > 0 && s == prep.Services.First(x => x.Ports.Count > 0);
            var args = new List<string>
            {
                "containerapp", (Cli.Run("az", ["containerapp", "show", "-g", rg!, "-n", app, "-o", "none"], cli).Ok ? "update" : "create"),
                "-g", rg!, "-n", app, "--image", Rewrite(s.Image, t), "-o", "json",
            };
            if (args[1] == "create")
            {
                args.AddRange(["--environment", envName]);
                if (port > 0)
                {
                    args.AddRange(["--target-port", port.ToString(), "--ingress", external ? "external" : "internal"]);
                    if (port is not (80 or 443 or 8080 or 3000 or 5000)) args.AddRange(["--transport", "auto"]);
                }
            }
            if (s.Env.Count > 0)
            {
                args.Add("--env-vars");
                args.AddRange(s.Env.Select(kv => $"{kv.Key}={kv.Value}"));
            }
            if (s.HasVolumes) log.AppendLine($"note: {s.Name} declares a volume — Container Apps keeps no local disk, data will not survive a revision");
            var r = Cli.Run("az", args.ToArray(), cli, timeoutMs: 900_000);
            log.AppendLine("az " + string.Join(' ', args.Take(6)));
            if (!r.Ok) { log.Append(r.Log); ok = false; continue; }
            if (external)
            {
                var fqdn = Cli.Run("az", ["containerapp", "show", "-g", rg!, "-n", app, "--query", "properties.configuration.ingress.fqdn", "-o", "tsv"], cli);
                if (fqdn.Ok && fqdn.Log.Trim().Length > 0) urls.Add("https://" + fqdn.Log.Trim());
            }
        }
        o.Store.Upsert(o.Store.Get(id)! with { ComposeDir = prep.Dir, Project = prefix });
        return o.Save(id, ok, urls, log.ToString());
    }

    // ---------- Google Cloud Run ----------

    public static Deployment CloudRun(OrchestratorService o, StackModel stack, string publishRoot, string id, DeployTarget t, string? cloneSrc)
    {
        var log = new StringBuilder();
        if (t.Cloud?.Project is not { Length: > 0 } project) return o.Fail(id, "this target has no project");
        var region = t.Cloud?.Location is { Length: > 0 } l ? l : "europe-west3";
        var cli = CloudCli.EnvFor(t, o.Secrets);

        var prep = Prepare(o, stack, publishRoot, cloneSrc);
        log.Append(prep.Log);
        if (!prep.Ok) return o.Fail(id, OrchestratorService.Tail(log.ToString()));
        if (PushImages(o, t, prep.Services, log) is { } pushErr) return o.Fail(id, pushErr);
        if (prep.Services.Count > 1)
            log.AppendLine("note: Cloud Run has no service-to-service DNS by name — a multi-service stack needs its URLs wired by hand");

        var prefix = OrchestratorService.SafeName(stack.Name);
        var urls = new List<string>();
        var ok = true;
        foreach (var s in prep.Services)
        {
            var name = OrchestratorService.SafeName($"{prefix}-{s.Name}");
            var args = new List<string>
            {
                "run", "deploy", name, "--image", Rewrite(s.Image, t), "--region", region,
                "--project", project, "--platform", "managed", "--allow-unauthenticated", "--format", "json",
            };
            if (s.Ports.FirstOrDefault() is > 0 and var port) args.AddRange(["--port", port.ToString()]);
            if (s.Env.Count > 0)
                args.AddRange(["--set-env-vars", string.Join(",", s.Env.Select(kv => $"{kv.Key}={kv.Value.Replace(",", "^")}"))]);
            if (s.HasVolumes) log.AppendLine($"note: {s.Name} declares a volume — Cloud Run has no local disk that survives a revision");
            var r = Cli.Run("gcloud", args.ToArray(), cli, timeoutMs: 900_000);
            log.AppendLine("gcloud " + string.Join(' ', args.Take(5)));
            if (!r.Ok) { log.Append(r.Log); ok = false; continue; }
            try
            {
                using var doc = JsonDocument.Parse(r.Log);
                if (doc.RootElement.TryGetProperty("status", out var st) && st.TryGetProperty("url", out var u) && u.GetString() is { } url)
                    urls.Add(url);
            }
            catch { }
        }
        o.Store.Upsert(o.Store.Get(id)! with { ComposeDir = prep.Dir, Project = prefix });
        return o.Save(id, ok, urls, log.ToString());
    }

    // ---------- AWS ECS (Fargate) ----------

    public static Deployment Ecs(OrchestratorService o, StackModel stack, string publishRoot, string id, DeployTarget t, string? cloneSrc)
    {
        var log = new StringBuilder();
        if (t.Cloud?.Cluster is not { Length: > 0 } cluster) return o.Fail(id, "this target has no ECS cluster");
        if (t.Cloud?.Subnets is not { Length: > 0 } subnets) return o.Fail(id, "this target has no subnets — ECS needs the network to run in");
        if (t.Cloud?.ExecutionRoleArn is not { Length: > 0 } role) return o.Fail(id, "this target has no execution role (ecsTaskExecutionRole)");
        var region = t.Cloud?.Location is { Length: > 0 } l ? l : "eu-central-1";
        var cli = CloudCli.EnvFor(t, o.Secrets);

        var prep = Prepare(o, stack, publishRoot, cloneSrc);
        log.Append(prep.Log);
        if (!prep.Ok) return o.Fail(id, OrchestratorService.Tail(log.ToString()));
        if (PushImages(o, t, prep.Services, log) is { } pushErr) return o.Fail(id, pushErr);
        if (prep.Services.Count > 1)
            log.AppendLine("note: services are registered as separate ECS services — for name-based discovery add Service Connect in the console");

        var prefix = OrchestratorService.SafeName(stack.Name);
        var urls = new List<string>();
        var ok = true;
        foreach (var s in prep.Services)
        {
            var name = OrchestratorService.SafeName($"{prefix}-{s.Name}");
            var taskDef = TaskDefinition(name, Rewrite(s.Image, t), role, region, s);
            var defFile = Path.Combine(prep.Dir, name + ".taskdef.json");
            File.WriteAllText(defFile, taskDef);
            var reg = Cli.Run("aws", ["ecs", "register-task-definition", "--region", region,
                "--cli-input-json", "file://" + defFile, "--output", "json"], cli, timeoutMs: 300_000);
            log.AppendLine($"aws ecs register-task-definition ({name})");
            if (!reg.Ok) { log.Append(reg.Log); ok = false; continue; }

            var netCfg = $"awsvpcConfiguration={{subnets=[{subnets}],securityGroups=[{t.Cloud?.SecurityGroups ?? ""}],assignPublicIp={(t.Cloud?.AssignPublicIp != false ? "ENABLED" : "DISABLED")}}}";
            var exists = Cli.Run("aws", ["ecs", "describe-services", "--region", region, "--cluster", cluster,
                "--services", name, "--query", "services[0].status", "--output", "text"], cli);
            var run = exists.Ok && exists.Log.Trim() == "ACTIVE"
                ? Cli.Run("aws", ["ecs", "update-service", "--region", region, "--cluster", cluster, "--service", name,
                    "--task-definition", name, "--force-new-deployment", "--output", "json"], cli, timeoutMs: 600_000)
                : Cli.Run("aws", ["ecs", "create-service", "--region", region, "--cluster", cluster, "--service-name", name,
                    "--task-definition", name, "--desired-count", "1", "--launch-type", "FARGATE",
                    "--network-configuration", netCfg, "--output", "json"], cli, timeoutMs: 600_000);
            log.Append(run.Ok ? $"{name} deployed\n" : run.Log);
            if (!run.Ok) { ok = false; continue; }
            if (PublicIp(cli, region, cluster, name) is { } ip && s.Ports.FirstOrDefault() is > 0 and var port)
                urls.Add($"http://{ip}:{port}");
        }
        o.Store.Upsert(o.Store.Get(id)! with { ComposeDir = prep.Dir, Project = prefix });
        return o.Save(id, ok, urls, log.ToString());
    }

    private static string? PublicIp(IReadOnlyDictionary<string, string> cli, string region, string cluster, string service)
    {
        for (var i = 0; i < 12; i++)
        {
            var tasks = Cli.Run("aws", ["ecs", "list-tasks", "--region", region, "--cluster", cluster,
                "--service-name", service, "--query", "taskArns[0]", "--output", "text"], cli);
            if (tasks.Ok && tasks.Log.Trim() is { Length: > 4 } arn && arn != "None")
            {
                var eni = Cli.Run("aws", ["ecs", "describe-tasks", "--region", region, "--cluster", cluster, "--tasks", arn.Trim(),
                    "--query", "tasks[0].attachments[0].details[?name=='networkInterfaceId'].value | [0]", "--output", "text"], cli);
                if (eni.Ok && eni.Log.Trim() is { Length: > 4 } nic && nic != "None")
                {
                    var ip = Cli.Run("aws", ["ec2", "describe-network-interfaces", "--region", region,
                        "--network-interface-ids", nic.Trim(), "--query", "NetworkInterfaces[0].Association.PublicIp", "--output", "text"], cli);
                    if (ip.Ok && ip.Log.Trim() is { Length: > 6 } addr && addr != "None") return addr.Trim();
                }
            }
            Thread.Sleep(5000);
        }
        return null;
    }

    public static string TaskDefinition(string name, string image, string roleArn, string region, ComposeService s)
    {
        var container = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["image"] = image,
            ["essential"] = true,
            ["environment"] = s.Env.Select(kv => new { name = kv.Key, value = kv.Value }).ToArray(),
            ["logConfiguration"] = new
            {
                logDriver = "awslogs",
                options = new Dictionary<string, string>
                {
                    ["awslogs-group"] = "/ecs/" + name,
                    ["awslogs-region"] = region,
                    ["awslogs-stream-prefix"] = "aspireui",
                    ["awslogs-create-group"] = "true",
                },
            },
        };
        if (s.Ports.Count > 0)
            container["portMappings"] = s.Ports.Select(p => new { containerPort = p, protocol = "tcp" }).ToArray();
        var def = new Dictionary<string, object?>
        {
            ["family"] = name,
            ["networkMode"] = "awsvpc",
            ["requiresCompatibilities"] = new[] { "FARGATE" },
            ["cpu"] = "512",
            ["memory"] = "1024",
            ["executionRoleArn"] = roleArn,
            ["containerDefinitions"] = new[] { container },
        };
        return JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true });
    }

    // ---------- operations ----------

    public static DeployResult Logs(OrchestratorService o, Deployment d, DeployTarget t, int tail)
    {
        var cli = CloudCli.EnvFor(t, o.Secrets);
        return t.Kind switch
        {
            TargetKind.Aca => Cli.Run("az", ["containerapp", "logs", "show", "-g", t.Cloud?.ResourceGroup ?? "",
                "-n", FirstApp(o, d), "--tail", tail.ToString(), "-o", "table"], cli),
            TargetKind.CloudRun => Cli.Run("gcloud", ["run", "services", "logs", "read", FirstApp(o, d),
                "--region", t.Cloud?.Location ?? "europe-west3", "--project", t.Cloud?.Project ?? "", "--limit", tail.ToString()], cli),
            TargetKind.Ecs => Cli.Run("aws", ["logs", "tail", "/ecs/" + FirstApp(o, d), "--region", t.Cloud?.Location ?? "eu-central-1"], cli),
            _ => new DeployResult(false, "no logs for this target"),
        };
    }

    public static DeployResult Exec(OrchestratorService o, Deployment d, DeployTarget t, string cmd)
    {
        var cli = CloudCli.EnvFor(t, o.Secrets);
        return t.Kind switch
        {
            TargetKind.Aca => Cli.Run("az", ["containerapp", "exec", "-g", t.Cloud?.ResourceGroup ?? "", "-n", FirstApp(o, d),
                "--command", "sh -c \"" + cmd.Replace("\"", "\\\"") + "\""], cli, timeoutMs: 120_000),
            TargetKind.Ecs => Cli.Run("aws", ["ecs", "execute-command", "--region", t.Cloud?.Location ?? "eu-central-1",
                "--cluster", t.Cloud?.Cluster ?? "", "--task", FirstApp(o, d), "--interactive", "--command", "sh -c \"" + cmd + "\""], cli, timeoutMs: 120_000),
            _ => new DeployResult(false, "this target has no shell — Cloud Run instances are not reachable that way"),
        };
    }

    public static Deployment? Refresh(OrchestratorService o, Deployment d, DeployTarget t)
    {
        var cli = CloudCli.EnvFor(t, o.Secrets);
        var (state, health, detail) = t.Kind switch
        {
            TargetKind.Aca => AcaState(cli, t, FirstApp(o, d)),
            TargetKind.CloudRun => RunState(cli, t, FirstApp(o, d)),
            TargetKind.Ecs => EcsState(cli, t, FirstApp(o, d)),
            _ => ("stopped", "unknown", (string?)null),
        };
        if (state != d.State || health != d.Health || detail != d.HealthDetail)
            o.Store.Upsert(d with { State = state, Health = health, HealthDetail = detail, UpdatedAt = DateTime.UtcNow.ToString("O") });
        return o.Store.Get(d.Id);
    }

    private static (string, string, string?) AcaState(IReadOnlyDictionary<string, string> cli, DeployTarget t, string app)
    {
        var r = Cli.Run("az", ["containerapp", "show", "-g", t.Cloud?.ResourceGroup ?? "", "-n", app,
            "--query", "properties.runningStatus", "-o", "tsv"], cli);
        if (!r.Ok) return ("stopped", "unknown", null);
        var s = r.Log.Trim();
        return s switch
        {
            "Running" => ("running", "ok", null),
            "Progressing" => ("running", "starting", $"{app} is still starting up"),
            "Suspended" or "Stopped" => ("stopped", "unknown", null),
            _ => ("failed", "failing", $"{app}: {s}"),
        };
    }

    private static (string, string, string?) RunState(IReadOnlyDictionary<string, string> cli, DeployTarget t, string app)
    {
        var r = Cli.Run("gcloud", ["run", "services", "describe", app, "--region", t.Cloud?.Location ?? "europe-west3",
            "--project", t.Cloud?.Project ?? "", "--format", "value(status.conditions[0].status)"], cli);
        if (!r.Ok) return ("stopped", "unknown", null);
        return r.Log.Trim() == "True" ? ("running", "ok", null) : ("failed", "failing", $"{app} is not ready");
    }

    private static (string, string, string?) EcsState(IReadOnlyDictionary<string, string> cli, DeployTarget t, string app)
    {
        var r = Cli.Run("aws", ["ecs", "describe-services", "--region", t.Cloud?.Location ?? "eu-central-1",
            "--cluster", t.Cloud?.Cluster ?? "", "--services", app,
            "--query", "services[0].[status,runningCount,desiredCount]", "--output", "text"], cli);
        if (!r.Ok) return ("stopped", "unknown", null);
        var cols = r.Log.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (cols.Length < 3) return ("stopped", "unknown", null);
        var running = int.TryParse(cols[1], out var rc) ? rc : 0;
        var desired = int.TryParse(cols[2], out var dc) ? dc : 0;
        if (cols[0] != "ACTIVE") return ("stopped", "unknown", null);
        if (desired == 0) return ("stopped", "unknown", null);
        return running >= desired ? ("running", "ok", null) : ("running", "starting", $"{running}/{desired} tasks running");
    }

    public static DeployResult Scale(OrchestratorService o, Deployment d, DeployTarget t, int replicas)
    {
        var cli = CloudCli.EnvFor(t, o.Secrets);
        var app = FirstApp(o, d);
        return t.Kind switch
        {
            TargetKind.Aca => Cli.Run("az", ["containerapp", "update", "-g", t.Cloud?.ResourceGroup ?? "", "-n", app,
                "--min-replicas", replicas.ToString(), "--max-replicas", Math.Max(replicas, 1).ToString(), "-o", "none"], cli, timeoutMs: 600_000),
            TargetKind.CloudRun => Cli.Run("gcloud", ["run", "services", "update", app, "--region", t.Cloud?.Location ?? "europe-west3",
                "--project", t.Cloud?.Project ?? "", "--min-instances", replicas.ToString()], cli, timeoutMs: 600_000),
            TargetKind.Ecs => Cli.Run("aws", ["ecs", "update-service", "--region", t.Cloud?.Location ?? "eu-central-1",
                "--cluster", t.Cloud?.Cluster ?? "", "--service", app, "--desired-count", replicas.ToString(), "--output", "json"], cli, timeoutMs: 600_000),
            _ => new DeployResult(false, "cannot scale this target"),
        };
    }

    public static DeployResult Remove(OrchestratorService o, Deployment d, DeployTarget t)
    {
        var cli = CloudCli.EnvFor(t, o.Secrets);
        var log = new StringBuilder();
        var ok = true;
        foreach (var app in Apps(o, d))
        {
            var r = t.Kind switch
            {
                TargetKind.Aca => Cli.Run("az", ["containerapp", "delete", "-g", t.Cloud?.ResourceGroup ?? "", "-n", app, "--yes", "-o", "none"], cli, timeoutMs: 600_000),
                TargetKind.CloudRun => Cli.Run("gcloud", ["run", "services", "delete", app, "--region", t.Cloud?.Location ?? "europe-west3",
                    "--project", t.Cloud?.Project ?? "", "--quiet"], cli, timeoutMs: 600_000),
                TargetKind.Ecs => Cli.Run("aws", ["ecs", "delete-service", "--region", t.Cloud?.Location ?? "eu-central-1",
                    "--cluster", t.Cloud?.Cluster ?? "", "--service", app, "--force", "--output", "json"], cli, timeoutMs: 600_000),
                _ => new DeployResult(false, "cannot remove from this target"),
            };
            log.Append(r.Log);
            ok &= r.Ok;
        }
        return new DeployResult(ok, log.ToString());
    }

    public static List<ServiceStatus> Services(OrchestratorService o, Deployment d, DeployTarget t)
    {
        var cli = CloudCli.EnvFor(t, o.Secrets);
        var list = new List<ServiceStatus>();
        foreach (var app in Apps(o, d))
        {
            var (state, health, detail) = t.Kind switch
            {
                TargetKind.Aca => AcaState(cli, t, app),
                TargetKind.CloudRun => RunState(cli, t, app),
                TargetKind.Ecs => EcsState(cli, t, app),
                _ => ("unknown", "unknown", (string?)null),
            };
            list.Add(new ServiceStatus(app, app, "", state, detail ?? health, ""));
        }
        return list;
    }

    public static DeployResult Restart(OrchestratorService o, Deployment d, DeployTarget t)
    {
        var cli = CloudCli.EnvFor(t, o.Secrets);
        var log = new StringBuilder();
        var ok = true;
        var services = ReadCompose(Path.Combine(d.ComposeDir ?? "", "docker-compose.yaml"));
        foreach (var app in Apps(o, d))
        {
            var svc = services.FirstOrDefault(s => OrchestratorService.SafeName($"{d.Project}-{s.Name}") == app);
            var r = t.Kind switch
            {
                TargetKind.Aca => Cli.Run("az", ["containerapp", "revision", "restart", "-g", t.Cloud?.ResourceGroup ?? "",
                    "-n", app, "-o", "none"], cli, timeoutMs: 600_000),
                TargetKind.CloudRun when svc is not null => Cli.Run("gcloud", ["run", "deploy", app, "--image", Rewrite(svc.Image, t),
                    "--region", t.Cloud?.Location ?? "europe-west3", "--project", t.Cloud?.Project ?? "", "--quiet"], cli, timeoutMs: 900_000),
                TargetKind.Ecs => Cli.Run("aws", ["ecs", "update-service", "--region", t.Cloud?.Location ?? "eu-central-1",
                    "--cluster", t.Cloud?.Cluster ?? "", "--service", app, "--force-new-deployment", "--output", "json"], cli, timeoutMs: 600_000),
                _ => new DeployResult(false, "cannot restart this target"),
            };
            log.Append(r.Log);
            ok &= r.Ok;
        }
        return new DeployResult(ok, log.ToString());
    }

    // The managed service names we created, derived the same way as at deploy time.
    private static List<string> Apps(OrchestratorService o, Deployment d)
    {
        var compose = Path.Combine(d.ComposeDir ?? "", "docker-compose.yaml");
        var services = ReadCompose(compose);
        var prefix = string.IsNullOrWhiteSpace(d.Project) ? OrchestratorService.SafeName(d.Name) : d.Project;
        return services.Count > 0
            ? services.Select(s => OrchestratorService.SafeName($"{prefix}-{s.Name}")).ToList()
            : [OrchestratorService.SafeName(prefix)];
    }

    private static string FirstApp(OrchestratorService o, Deployment d) => Apps(o, d).FirstOrDefault() ?? d.Project;
}
