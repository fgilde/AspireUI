using System.Security.Cryptography;
using System.Text;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

public record RetrievedApp(string Name, string Kind, bool Healthy, List<string> Urls);

/// <summary>
/// A read-only list of the hosted apps and running stacks, for another app to pick up. Off unless
/// <c>ASPIREUI_RETRIEVE_API=true</c> or a key is set; <c>ASPIREUI_RETRIEVE_API_KEY</c> makes callers send
/// it as <c>X-Api-Key</c>. <c>ASPIREUI_RETRIEVE_API_HIDDEN=a;b</c> hides apps by name until someone shows
/// them again, which is stored on the stack and wins over the list.
/// </summary>
public static class RetrieveApi
{
    public const string KeyHeader = "X-Api-Key";
    public const string Path = "/api/retrieve/apps";

    private static string? Env(string key) =>
        Environment.GetEnvironmentVariable(key) is { } v && v.Trim() is { Length: > 0 } t ? t : null;

    public static string? Key => Env("ASPIREUI_RETRIEVE_API_KEY");

    public static bool Enabled => Key is not null
        || Env("ASPIREUI_RETRIEVE_API") is "1" || string.Equals(Env("ASPIREUI_RETRIEVE_API"), "true", StringComparison.OrdinalIgnoreCase);

    public static string[] HiddenNames =>
        (Env("ASPIREUI_RETRIEVE_API_HIDDEN") ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool IsHidden(StackModel? stack, string name) =>
        stack?.RetrieveHidden ?? HiddenNames.Any(n =>
            n.Equals(name, StringComparison.OrdinalIgnoreCase) || n.Equals(stack?.Name, StringComparison.OrdinalIgnoreCase));

    public static bool Authorized(HttpRequest request) =>
        Key is not { } key || CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(request.Headers[KeyHeader].ToString()), Encoding.UTF8.GetBytes(key));

    public static bool HostedHealthy(Deployment d) => d.State == "running" && d.Health is null or "ok";

    private static readonly string[] NotReady =
        ["Starting", "Waiting", "NotStarted", "FailedToStart", "Stopping", "RuntimeUnhealthy", "Unknown"];

    // Aspire has more states than running or not; a one-shot resource that finished cleanly is fine.
    public static bool StackHealthy(IEnumerable<LiveResource> resources)
    {
        var visible = resources.Where(r => !r.Hidden).ToList();
        return visible.Count > 0 && visible.All(r => r.StateStyle != "error" && !NotReady.Contains(r.State, StringComparer.OrdinalIgnoreCase));
    }

    public static List<string> PublicUrls(IEnumerable<LiveResource> resources) =>
        resources.Where(r => !r.Hidden).SelectMany(r => r.Urls)
            .Where(u => !u.IsInternal && !u.IsInactive).Select(u => u.Url).Distinct().ToList();
}
