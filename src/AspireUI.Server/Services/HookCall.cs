using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace AspireUI.Server.Services;

public record HookFailure(int Status, string Error);

public static class HookCall
{
    public static Dictionary<string, string> MergeArgs(IEnumerable<KeyValuePair<string, string>> query, string? body)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in query) args[k] = v;
        if (string.IsNullOrWhiteSpace(body)) return args;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return args;
            foreach (var p in doc.RootElement.EnumerateObject())
                args[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
        }
        catch (JsonException) { }
        return args;
    }

    public static async Task<Dictionary<string, string>> ReadArgsAsync(HttpRequest req)
    {
        using var reader = new StreamReader(req.Body);
        var body = await reader.ReadToEndAsync();
        return MergeArgs(req.Query.Select(q => KeyValuePair.Create(q.Key, q.Value.ToString())), body);
    }

    public static HookFailure? CheckEnabled(HookSettings s, Hook h) =>
        !s.Enabled ? new(503, "all hooks disabled") : !h.Enabled ? new(503, "hook disabled") : null;

    public static (Dictionary<string, string> Values, HookFailure? Failure) ResolveParams(Hook h, IReadOnlyDictionary<string, string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in h.Params ?? [])
        {
            args.TryGetValue(p.Key, out var given);
            var has = !string.IsNullOrEmpty(given);
            switch (p.Mode)
            {
                case "required":
                    if (!has) return (values, new(400, $"missing parameter: {p.Key}"));
                    values[p.Key] = given!;
                    break;
                case "optional":
                    values[p.Key] = has ? given! : p.Value ?? "";
                    break;
                case "generated":
                    values[p.Key] = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                    break;
                default:
                    values[p.Key] = p.Value ?? "";
                    break;
            }
        }
        return (values, null);
    }

    private static string Gb(double gb) => gb.ToString("0.#", CultureInfo.InvariantCulture);

    public static HookFailure? CheckResources(HookSettings s, string target, (long? DiskFreeMb, long? RamFreeMb)? measured)
    {
        if (measured is not { } m) return null;
        if (m.DiskFreeMb is not { } disk || m.RamFreeMb is not { } ram) return new(503, $"cannot check resources on {target}");
        var diskGb = disk / 1024d;
        var ramGb = ram / 1024d;
        if (diskGb < s.MinDiskGb) return new(507, $"not enough disk on {target}: {Gb(diskGb)} GB free, {Gb(s.MinDiskGb)} GB required");
        if (ramGb < s.MinRamGb) return new(507, $"not enough memory on {target}: {Gb(ramGb)} GB free, {Gb(s.MinRamGb)} GB required");
        return null;
    }
}
