using System.Diagnostics;

namespace AspireUI.Server.Services;

/// <summary>
/// An interactive shell in a container, over a WebSocket. `docker exec -it` needs a terminal on our
/// side too, and a server process has none — so on Linux the exec is wrapped in <c>script</c>, which
/// allocates a pty for it. That is the same trick every remote-shell tool uses, and it is what makes
/// <c>top</c>, <c>vim</c> and a prompt with line editing work instead of a pipe that echoes nothing.
/// Where <c>script</c> is missing (a Windows dev box) it falls back to a pipe, which is still an
/// interactive session — just without terminal semantics.
/// </summary>
public class PtyService
{
    public record Session(Process Process, bool HasTty);

    /// <summary>Whether a real pty can be allocated here. Decided once: the answer cannot change while we run.</summary>
    public static readonly bool CanAllocatePty = OperatingSystem.IsLinux() && File.Exists("/usr/bin/script");

    /// <summary>
    /// Starts a shell in <paramref name="container"/>. The shell is chosen by the container, not by
    /// us: bash where it exists, sh everywhere else.
    /// </summary>
    public Session? Start(string container, IReadOnlyDictionary<string, string>? env, int cols = 120, int rows = 30)
    {
        // -t only makes sense with a pty on this side; without one it makes docker refuse.
        var inner = $"docker exec -i {(CanAllocatePty ? "-t " : "")}" +
                    $"-e TERM=xterm-256color -e COLUMNS={cols} -e LINES={rows} " +
                    $"{container} sh -c \"exec $(command -v bash || command -v sh)\"";

        var psi = CanAllocatePty
            ? new ProcessStartInfo("/usr/bin/script", ["-q", "-f", "-c", inner, "/dev/null"])
            : new ProcessStartInfo("docker", ["exec", "-i", container, "sh"]);

        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = null;   // raw bytes: a terminal stream is not text
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;

        try
        {
            var p = Process.Start(psi);
            return p is null ? null : new Session(p, CanAllocatePty);
        }
        catch { return null; }
    }

    /// <summary>
    /// Tells the shell its window changed. With a pty the slave side can set its own size, which is
    /// what stty does; without one there is nothing to set and nothing to do.
    /// </summary>
    public static string ResizeCommand(int cols, int rows) =>
        $" stty rows {Math.Clamp(rows, 4, 300)} cols {Math.Clamp(cols, 20, 500)}\n";

    /// <summary>Everything the client may not do: leave the app's own containers.</summary>
    public static bool Allowed(string container, string project) =>
        container.StartsWith(project + "-", StringComparison.Ordinal)
        || container.StartsWith(project + "_", StringComparison.Ordinal);

    /// <summary>Reads whatever is there right now, as bytes, without waiting for a line.</summary>
    public static async Task<int> ReadAvailableAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        try { return await stream.ReadAsync(buffer, ct); }
        catch (OperationCanceledException) { return 0; }
        catch (IOException) { return 0; }
    }
}
