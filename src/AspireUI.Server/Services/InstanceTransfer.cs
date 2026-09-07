using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

/// <summary>
/// A whole instance in one file: the stacks, the accounts, the deploy targets, the store sources and
/// the settings. Enough to stand the same AspireUI up somewhere else, or to put this one back.
/// </summary>
public record InstanceExport(
    int Version,
    string ExportedAt,
    string? AppVersion,
    List<ExportUser>? Users = null,
    List<DeployTarget>? Targets = null,
    Dictionary<string, string>? Settings = null,
    List<AppSource>? AppSources = null,
    List<StackModel>? Stacks = null,
    List<ExportDeployment>? Deployments = null,
    bool ContainsSecrets = false);

/// <summary>An account with its hash: a restore that asks everybody for a new password is not a restore.</summary>
public record ExportUser(string Username, string PasswordHash, bool IsAdmin, bool Disabled,
    bool MustChangePassword, List<string>? Permissions, List<string>? ViewModes);

/// <summary>Which app was deployed where, so an import knows what to bring up again.</summary>
public record ExportDeployment(string StackId, string Name, string State, string? TargetId,
    List<PortMapping>? Ports);

public static class InstanceTransfer
{
    public const int FormatVersion = 1;
    public const string DocumentName = "aspireui.instance.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Settings that are secrets or belong to this machine only. Kept out unless the export is asked
    /// to include secrets — a file that travels should not carry a proxy password by default.
    /// </summary>
    private static readonly string[] SecretSettings =
        ["AiApiKey", "NpmPassword", "DashboardToken", "NotifyTelegramToken", "NotifyWebhookUrl"];

    private static readonly string[] LocalSettings =
        ["BackupLastRun", Seeder.PendingDeployKey, "StoreExclusions"];

    public static byte[] Write(InstanceExport doc, IReadOnlyDictionary<string, string>? extraFiles = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(DocumentName, CompressionLevel.Optimal);
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                w.Write(JsonSerializer.Serialize(doc, Json));
            foreach (var (name, content) in extraFiles ?? new Dictionary<string, string>())
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Reads the document out of an export zip, or out of a bare json file.</summary>
    public static (InstanceExport? doc, string? error) Read(Stream file)
    {
        try
        {
            using var buffered = new MemoryStream();
            file.CopyTo(buffered);
            buffered.Position = 0;

            // A zip starts with PK; anything else is treated as the json document itself.
            var head = buffered.ReadByte();
            var second = buffered.ReadByte();
            buffered.Position = 0;
            string text;
            if (head == 'P' && second == 'K')
            {
                using var zip = new ZipArchive(buffered, ZipArchiveMode.Read);
                var entry = zip.GetEntry(DocumentName)
                    ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
                if (entry is null) return (null, $"no {DocumentName} in the archive");
                using var r = new StreamReader(entry.Open());
                text = r.ReadToEnd();
            }
            else
            {
                using var r = new StreamReader(buffered);
                text = r.ReadToEnd();
            }

            var doc = JsonSerializer.Deserialize<InstanceExport>(text, Json);
            if (doc is null) return (null, "the file held no export");
            if (doc.Version > FormatVersion)
                return (null, $"this file was written by a newer AspireUI (format {doc.Version})");
            return (doc, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public static InstanceExport Collect(UserStore users, StackStore stacks, SettingsStore settings,
        TargetStore targets, DeploymentStore deployments, AppSourceService? appSources,
        SecretStore? secrets, bool includeSecrets, string? appVersion)
    {
        var settingValues = new Dictionary<string, string>();
        foreach (var (key, value) in settings.All())
        {
            if (LocalSettings.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            if (key.StartsWith("git:", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("githook:", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("clonehook:", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("sched:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!includeSecrets && SecretSettings.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            settingValues[key] = value;
        }

        // A secret reference is worthless without this machine's key, so it either travels resolved or
        // it does not travel at all.
        var exported = targets.List().Select(t => includeSecrets && secrets is not null ? Resolve(t, secrets) : Strip(t)).ToList();

        return new InstanceExport(
            FormatVersion,
            DateTime.UtcNow.ToString("O"),
            appVersion,
            users.List().Select(u => new ExportUser(u.Username, u.PasswordHash, u.IsAdmin, u.Disabled,
                u.MustChangePassword, u.Permissions, u.ViewModes)).ToList(),
            exported,
            settingValues,
            appSources?.List().ToList(),
            stacks.List().ToList(),
            deployments.List().Select(d => new ExportDeployment(d.StackId, d.Name, d.State, d.TargetId, d.Ports)).ToList(),
            includeSecrets);
    }

    private static DeployTarget Strip(DeployTarget t) => t with
    {
        Ssh = t.Ssh is null ? null : t.Ssh with { KeyRef = null, PassphraseRef = null },
        Tls = t.Tls is null ? null : new TargetTls(null, null, null),
        Kube = t.Kube is null ? null : t.Kube with { KubeconfigRef = null },
        Cloud = t.Cloud is null ? null : t.Cloud with { CredRef = null },
        Provider = t.Provider is null ? null : t.Provider with { CredRef = null },
        Registry = t.Registry is null ? null : t.Registry with { PasswordRef = null },
        Domains = t.Domains?.Npm is null ? t.Domains : t.Domains with { Npm = t.Domains.Npm with { PasswordRef = null } },
        Probe = null,
    };

    private static DeployTarget Resolve(DeployTarget t, SecretStore secrets) => t with
    {
        Ssh = t.Ssh is null ? null : t.Ssh with
        {
            KeyRef = secrets.Resolve(t.Ssh.KeyRef),
            PassphraseRef = secrets.Resolve(t.Ssh.PassphraseRef),
        },
        Tls = t.Tls is null ? null
            : new TargetTls(secrets.Resolve(t.Tls.CaRef), secrets.Resolve(t.Tls.CertRef), secrets.Resolve(t.Tls.KeyRef)),
        Kube = t.Kube is null ? null : t.Kube with { KubeconfigRef = secrets.Resolve(t.Kube.KubeconfigRef) },
        Cloud = t.Cloud is null ? null : t.Cloud with { CredRef = secrets.Resolve(t.Cloud.CredRef) },
        Registry = t.Registry is null ? null : t.Registry with { PasswordRef = secrets.Resolve(t.Registry.PasswordRef) },
        Domains = t.Domains?.Npm is null ? t.Domains
            : t.Domains with { Npm = t.Domains.Npm with { PasswordRef = secrets.Resolve(t.Domains.Npm.PasswordRef) } },
        Probe = null,
    };

    public record ImportReport(int Users, int Targets, int Settings, int AppSources, int Stacks, List<string> Skipped);

    /// <summary>
    /// Puts an export into this instance. Everything is matched by name and nothing existing is
    /// overwritten unless <paramref name="overwrite"/> says so: an import is a merge, not a wipe,
    /// because the usual reason to run one is to bring something over — not to lose what is here.
    /// </summary>
    public static ImportReport Apply(InstanceExport doc, UserStore users, StackStore stacks,
        SettingsStore settings, TargetStore targets, AppSourceService? appSources, SecretStore? secrets,
        bool overwrite = false)
    {
        var report = new ImportReport(0, 0, 0, 0, 0, []);
        var skipped = report.Skipped;

        foreach (var u in doc.Users ?? [])
        {
            if (string.IsNullOrWhiteSpace(u.Username) || string.IsNullOrWhiteSpace(u.PasswordHash)) continue;
            if (users.FindByUsername(u.Username) is { } existing)
            {
                if (!overwrite) { skipped.Add($"user {u.Username}"); continue; }
                users.SetPassword(existing.Id, u.PasswordHash, u.MustChangePassword);
                users.SetAdmin(existing.Id, u.IsAdmin);
                users.SetDisabled(existing.Id, u.Disabled);
                if (u.Permissions is not null) users.SetPermissions(existing.Id, u.Permissions);
                if (u.ViewModes is not null) users.SetViewModes(existing.Id, u.ViewModes);
                report = report with { Users = report.Users + 1 };
                continue;
            }
            var created = users.Create(u.Username, u.PasswordHash, u.IsAdmin);
            if (u.Permissions is not null) users.SetPermissions(created.Id, u.Permissions);
            if (u.ViewModes is not null) users.SetViewModes(created.Id, u.ViewModes);
            if (u.Disabled) users.SetDisabled(created.Id, true);
            if (u.MustChangePassword) users.SetPassword(created.Id, u.PasswordHash, mustChange: true);
            report = report with { Users = report.Users + 1 };
        }

        foreach (var t in doc.Targets ?? [])
        {
            if (t.IsLocal) continue;   // every instance has its own
            var existing = targets.List().FirstOrDefault(x => string.Equals(x.Name, t.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && !overwrite) { skipped.Add($"target {t.Name}"); continue; }
            var id = existing?.Id ?? targets.UniqueId(t.Name);
            targets.Upsert(Store(t with { Id = id, Probe = null }, secrets));
            report = report with { Targets = report.Targets + 1 };
        }

        foreach (var (key, value) in doc.Settings ?? new())
        {
            if (!overwrite && !string.IsNullOrEmpty(settings.GetValue(key))) { skipped.Add($"setting {key}"); continue; }
            settings.SetValue(key, value);
            report = report with { Settings = report.Settings + 1 };
        }

        foreach (var s in doc.AppSources ?? [])
        {
            if (appSources is null) break;
            if (AppSourceService.Validate(s.Name, s.Url) is not null) continue;
            if (appSources.List().Any(x => x.Id == AppSourceService.IdFor(s.Url))) { skipped.Add($"source {s.Name}"); continue; }
            appSources.Add(s.Name, s.Url);
            report = report with { AppSources = report.AppSources + 1 };
        }

        foreach (var s in doc.Stacks ?? [])
        {
            var existing = stacks.List().FirstOrDefault(x => string.Equals(x.Name, s.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && !overwrite) { skipped.Add($"stack {s.Name}"); continue; }
            stacks.Save(s with { Id = existing?.Id ?? s.Id });
            report = report with { Stacks = report.Stacks + 1 };
        }

        return report;
    }

    // The other direction of Resolve: material that came in as text goes into the secret store, and
    // the target keeps a reference. A value that is already a reference is left as it is.
    private static DeployTarget Store(DeployTarget t, SecretStore? secrets)
    {
        if (secrets is null) return t;
        string? Put(string? value, string label) =>
            string.IsNullOrWhiteSpace(value) ? null : SecretStore.IsRef(value) ? value : secrets.Put(value, label);

        return t with
        {
            Ssh = t.Ssh is null ? null : t.Ssh with
            {
                KeyRef = Put(t.Ssh.KeyRef, $"ssh key for {t.Name}"),
                PassphraseRef = Put(t.Ssh.PassphraseRef, $"ssh passphrase for {t.Name}"),
            },
            Tls = t.Tls is null ? null : new TargetTls(Put(t.Tls.CaRef, $"ca for {t.Name}"),
                Put(t.Tls.CertRef, $"cert for {t.Name}"), Put(t.Tls.KeyRef, $"tls key for {t.Name}")),
            Kube = t.Kube is null ? null : t.Kube with { KubeconfigRef = Put(t.Kube.KubeconfigRef, $"kubeconfig for {t.Name}") },
            Cloud = t.Cloud is null ? null : t.Cloud with { CredRef = Put(t.Cloud.CredRef, $"credentials for {t.Name}") },
            Registry = t.Registry is null ? null : t.Registry with { PasswordRef = Put(t.Registry.PasswordRef, $"registry password for {t.Name}") },
            Domains = t.Domains?.Npm is null ? t.Domains
                : t.Domains with { Npm = t.Domains.Npm with { PasswordRef = Put(t.Domains.Npm.PasswordRef, $"proxy password for {t.Name}") } },
        };
    }
}
