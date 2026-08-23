using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

// The managed container platforms are driven through their own CLI, not through docker: the tool is the
// only place that knows about identity, regions and the hundred flags each platform wants. We keep the
// commands here so they are inspectable in one file, and every one of them is also shown in the deploy
// log so a failure can be reproduced by hand.
public static class CloudCli
{
    public static string Exe(string kind) => kind switch
    {
        TargetKind.Aca => "az",
        TargetKind.CloudRun => "gcloud",
        TargetKind.Ecs => "aws",
        _ => "docker",
    };

    // Credentials for the CLI. All three accept environment variables, which keeps the secrets out of
    // the command line (and out of the process list).
    public static IReadOnlyDictionary<string, string> EnvFor(DeployTarget t, SecretStore secrets)
    {
        var env = new Dictionary<string, string>();
        var cred = secrets.Resolve(t.Cloud?.CredRef);
        switch (t.Kind)
        {
            case TargetKind.Aca:
                // A service principal as "tenant:appId:secret", or nothing at all when `az login` was used.
                if (cred is { Length: > 0 })
                {
                    var parts = cred.Split(':', 3);
                    if (parts.Length == 3)
                    {
                        env["AZURE_TENANT_ID"] = parts[0];
                        env["AZURE_CLIENT_ID"] = parts[1];
                        env["AZURE_CLIENT_SECRET"] = parts[2];
                    }
                }
                if (t.Cloud?.SubscriptionId is { Length: > 0 } sub) env["AZURE_SUBSCRIPTION_ID"] = sub;
                break;
            case TargetKind.CloudRun:
                // A service-account key file's JSON: written next to the target and pointed at.
                if (cred is { Length: > 0 })
                {
                    var dir = Path.Combine(Path.GetTempPath(), "aspireui-gcp");
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, t.Id + ".json");
                    if (!File.Exists(path) || File.ReadAllText(path) != cred) { File.WriteAllText(path, cred); FileGuard.OwnerOnly(path); }
                    env["GOOGLE_APPLICATION_CREDENTIALS"] = path;
                    env["CLOUDSDK_AUTH_CREDENTIAL_FILE_OVERRIDE"] = path;
                }
                if (t.Cloud?.Project is { Length: > 0 } proj) env["CLOUDSDK_CORE_PROJECT"] = proj;
                if (t.Cloud?.Location is { Length: > 0 } loc) env["CLOUDSDK_RUN_REGION"] = loc;
                break;
            case TargetKind.Ecs:
                // "accessKeyId:secretAccessKey[:sessionToken]".
                if (cred is { Length: > 0 })
                {
                    var parts = cred.Split(':', 3);
                    if (parts.Length >= 2)
                    {
                        env["AWS_ACCESS_KEY_ID"] = parts[0];
                        env["AWS_SECRET_ACCESS_KEY"] = parts[1];
                        if (parts.Length == 3) env["AWS_SESSION_TOKEN"] = parts[2];
                    }
                }
                if (t.Cloud?.Location is { Length: > 0 } region) env["AWS_DEFAULT_REGION"] = region;
                break;
        }
        return env;
    }

    // "Can we talk to this account at all" — the same check the UI's Test button runs.
    public static TargetProbe Probe(DeployTarget t, SecretStore secrets)
    {
        var env = EnvFor(t, secrets);
        switch (t.Kind)
        {
            case TargetKind.Aca:
            {
                if (env.ContainsKey("AZURE_CLIENT_SECRET"))
                {
                    var login = Cli.Run("az", ["login", "--service-principal", "-u", env["AZURE_CLIENT_ID"],
                        "-p", env["AZURE_CLIENT_SECRET"], "--tenant", env["AZURE_TENANT_ID"], "-o", "none"], env);
                    if (!login.Ok) return new TargetProbe(false, Trim(login.Log));
                }
                var acc = Cli.Run("az", ["account", "show", "-o", "json"], env);
                if (!acc.Ok) return new TargetProbe(false, Trim(acc.Log));
                var ver = Cli.Run("az", ["version", "-o", "tsv", "--query", "\"azure-cli\""], env);
                return new TargetProbe(true, null, Version: ver.Ok ? "az " + ver.Log.Trim() : null,
                    Os: "azure container apps", Arch: "amd64");
            }
            case TargetKind.CloudRun:
            {
                var acc = Cli.Run("gcloud", ["auth", "list", "--format=value(account)"], env);
                if (!acc.Ok) return new TargetProbe(false, Trim(acc.Log));
                if (string.IsNullOrWhiteSpace(acc.Log)) return new TargetProbe(false, "gcloud has no active account — run `gcloud auth login` or add a service-account key");
                var ver = Cli.Run("gcloud", ["version", "--format=value(\"Google Cloud SDK\")"], env);
                return new TargetProbe(true, null, Version: ver.Ok ? "gcloud " + ver.Log.Trim() : null,
                    Os: "cloud run", Arch: "amd64");
            }
            case TargetKind.Ecs:
            {
                var who = Cli.Run("aws", ["sts", "get-caller-identity", "--output", "json"], env);
                if (!who.Ok) return new TargetProbe(false, Trim(who.Log));
                var ver = Cli.Run("aws", ["--version"], env);
                return new TargetProbe(true, null, Version: ver.Ok ? ver.Log.Split(' ').FirstOrDefault() : null,
                    Os: "ecs fargate", Arch: "amd64");
            }
        }
        return new TargetProbe(false, $"unknown target kind '{t.Kind}'");
    }

    private static string Trim(string log) =>
        log.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "failed";
}
