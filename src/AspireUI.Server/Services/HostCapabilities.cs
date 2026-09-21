namespace AspireUI.Server.Services;

/// <summary>
/// What the Docker host can do, for the things an app needs from the host rather than from its
/// image. <c>Available</c> is null when the question could not be answered — docker unreachable,
/// probe image not pullable — which is not the same as a no.
/// </summary>
public record HostCapability(string Id, bool? Available, string Detail, DateTimeOffset Checked);

/// <summary>
/// The Android emulator needs <c>/dev/kvm</c> and refuses to run without it, while the container
/// around it starts anyway and keeps serving its landing page. The symptom is an app that never
/// appears, which is a bad thing to discover after installing. The host can answer this, so ask it
/// once and remember.
/// </summary>
public class HostCapabilities(DeployService deploy)
{
    public const string Kvm = "kvm";

    /// <summary>Small and almost always already pulled; the probe never runs anything real.</summary>
    public const string ProbeImage = "alpine:3.20";

    private HostCapability? _kvm;

    /// <summary>
    /// Whether the Docker host can hand <c>/dev/kvm</c> to a container. Docker refuses to create a
    /// container for a device the host does not have, so trying is the answer.
    /// </summary>
    public HostCapability KvmSupport(bool refresh = false)
    {
        if (!refresh && _kvm is { } cached)
            return cached;

        return _kvm = Read(deploy.Docker(".", $"run --rm --device /dev/kvm {ProbeImage} true"), DateTimeOffset.UtcNow);
    }

    public Dictionary<string, HostCapability> All(bool refresh = false) =>
        new() { [Kvm] = KvmSupport(refresh) };

    /// <summary>
    /// The decision, over docker's answer and nothing else — which is what makes it checkable
    /// without a docker to ask.
    /// </summary>
    public static HostCapability Read(DeployResult probe, DateTimeOffset now)
    {
        if (probe.Ok)
            return new(Kvm, true, "the Docker host has /dev/kvm.", now);

        var log = probe.Log ?? "";
        // What docker says when the device is not there, in either of the two shapes it says it.
        var missing = log.Contains("/dev/kvm", StringComparison.OrdinalIgnoreCase)
                      && (log.Contains("no such file", StringComparison.OrdinalIgnoreCase)
                          || log.Contains("error gathering device information", StringComparison.OrdinalIgnoreCase));

        return missing
            ? new(Kvm, false,
                "the Docker host has no /dev/kvm, so an Android emulator will not start on it. " +
                "That needs a Linux host, or nested virtualisation enabled for the Docker Desktop VM.", now)
            : new(Kvm, null, "could not ask the Docker host: " + Trim(log), now);
    }

    private static string Trim(string log)
    {
        var line = log.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "no output";
        return line.Length > 300 ? line[..300] : line;
    }
}
