using System.Globalization;
using System.Text.Json;

namespace AspireUI.Server.Services;

public record HookParam(string Key, string Mode = "fixed", string? Value = null, bool Secret = false);

public record Hook(string Token, string Kind, string Name, bool Enabled = true, string? CreatedAt = null,
    int ExpireDays = 7, bool BindDomain = false, string? DomainFormat = null, string? TargetId = null,
    string? SourceStackId = null, string? AppId = null, string? SourceId = null,
    string? Repo = null, string? Subdir = null, string? Mode = null, string? AuthToken = null,
    string? Image = null, int? Port = null, List<HookParam>? Params = null);

public record HookSettings(bool Enabled = true, double MinDiskGb = 5, double MinRamGb = 1);

public class HookStore(SettingsStore settings)
{
    public const string Masked = "••••";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private record OldCloneHook(string SourceStackId = "", int ExpireDays = 7, bool BindDomain = false, string? DomainFormat = null, string? TargetId = null);

    private List<string> Tokens() =>
        settings.GetValue("hooks") is { Length: > 0 } raw ? JsonSerializer.Deserialize<List<string>>(raw, Json) ?? [] : [];
    private void SaveTokens(List<string> t) => settings.SetValue("hooks", JsonSerializer.Serialize(t, Json));

    public IReadOnlyList<Hook> List() => Tokens().Select(Get).OfType<Hook>().ToList();

    public Hook? Get(string token) =>
        settings.GetValue($"hook:{token}") is { Length: > 0 } raw ? JsonSerializer.Deserialize<Hook>(raw, Json) : null;

    public void Save(Hook h)
    {
        settings.SetValue($"hook:{h.Token}", JsonSerializer.Serialize(h, Json));
        var t = Tokens();
        if (!t.Contains(h.Token)) { t.Add(h.Token); SaveTokens(t); }
    }

    public void Delete(string token)
    {
        settings.SetValue($"hook:{token}", null);
        var t = Tokens();
        if (t.Remove(token)) SaveTokens(t);
    }

    public Hook Regenerate(string token)
    {
        var h = Get(token) ?? throw new KeyNotFoundException(token);
        var fresh = h with { Token = Guid.NewGuid().ToString("n") };
        Delete(token);
        Save(fresh);
        return fresh;
    }

    public HookSettings Settings() => new(
        settings.GetValue("HooksEnabled") != "false",
        double.TryParse(settings.GetValue("HookMinDiskGb"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 5,
        double.TryParse(settings.GetValue("HookMinRamGb"), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 1);

    public void SaveSettings(HookSettings s)
    {
        settings.SetValue("HooksEnabled", s.Enabled ? "true" : "false");
        settings.SetValue("HookMinDiskGb", Math.Max(0, s.MinDiskGb).ToString(CultureInfo.InvariantCulture));
        settings.SetValue("HookMinRamGb", Math.Max(0, s.MinRamGb).ToString(CultureInfo.InvariantCulture));
    }

    public int MigrateCloneHooks(Func<string, string?> stackName)
    {
        var n = 0;
        foreach (var (key, value) in settings.All())
        {
            if (key.StartsWith("clonehooks:", StringComparison.Ordinal)) { settings.SetValue(key, null); continue; }
            if (!key.StartsWith("clonehook:", StringComparison.Ordinal)) continue;
            var token = key["clonehook:".Length..];
            var old = JsonSerializer.Deserialize<OldCloneHook>(value, Json) ?? new();
            if (Get(token) is null)
            {
                Save(new Hook(token, "clone", stackName(old.SourceStackId) ?? "clone", CreatedAt: DateTime.UtcNow.ToString("O"),
                    ExpireDays: old.ExpireDays, BindDomain: old.BindDomain, DomainFormat: old.DomainFormat,
                    TargetId: old.TargetId, SourceStackId: old.SourceStackId));
                n++;
            }
            settings.SetValue(key, null);
        }
        return n;
    }

    private static bool KeepsValue(HookParam p) => p.Mode is "fixed" or "optional";

    public static Hook Mask(Hook h) => h with
    {
        AuthToken = string.IsNullOrEmpty(h.AuthToken) ? h.AuthToken : Masked,
        Params = h.Params?.Select(p => p.Secret && !string.IsNullOrEmpty(p.Value) ? p with { Value = Masked } : p).ToList(),
    };

    public static Hook KeepSecrets(Hook incoming, Hook existing) => incoming with
    {
        AuthToken = incoming.AuthToken == Masked ? existing.AuthToken : incoming.AuthToken,
        Params = incoming.Params?.Select(p => !KeepsValue(p) ? p with { Value = null }
            : p.Value == Masked ? p with { Value = existing.Params?.FirstOrDefault(e => e.Key == p.Key)?.Value }
            : p).ToList(),
    };
}
