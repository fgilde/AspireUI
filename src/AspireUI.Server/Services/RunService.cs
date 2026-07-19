using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AspireUI.Server.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunState { NotRunning, Starting, Running, Failed }

public record RunStatus(RunState State, string? DashboardUrl, List<string> Log);

public class RunService : IDisposable
{
    private static readonly Regex DashboardRx =
        new(@"https?://localhost:\d+/login\?t=\S+", RegexOptions.Compiled);

    private class Handle
    {
        public Process Process = default!;
        public RunState State = RunState.Starting;
        public string? DashboardUrl;
        public readonly List<string> Log = new();
    }

    private readonly ConcurrentDictionary<string, Handle> _runs = new();
    private readonly Func<string, ProcessStartInfo> _commandFactory;

    public RunService(Func<string, ProcessStartInfo>? commandFactory = null)
        => _commandFactory = commandFactory ?? DefaultCommand;

    private static ProcessStartInfo DefaultCommand(string workdir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{workdir}\"",
            WorkingDirectory = workdir,
        };
        // Aspire refuses to start on a plain-http dashboard unless this is set; the
        // generated project has no launch profile, so allow unsecured transport for local runs.
        psi.Environment["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true";
        return psi;
    }

    public static string? ParseDashboardUrl(string line)
    {
        var m = DashboardRx.Match(line);
        return m.Success ? m.Value : null;
    }

    public RunStatus Start(string id, string workdir)
    {
        if (_runs.TryGetValue(id, out var existing) &&
            existing.State is RunState.Running or RunState.Starting)
            return Snapshot(existing);

        var psi = _commandFactory(workdir);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        var h = new Handle();
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        h.Process = proc;

        void OnLine(string? line)
        {
            if (line is null) return;
            lock (h.Log)
            {
                h.Log.Add(line);
                if (h.Log.Count > 200) h.Log.RemoveAt(0);
            }
            var url = ParseDashboardUrl(line);
            if (url is not null) { h.DashboardUrl = url; h.State = RunState.Running; }
        }
        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
        proc.Exited += (_, _) =>
        {
            if (h.State != RunState.Running) h.State = proc.ExitCode == 0 ? RunState.NotRunning : RunState.Failed;
        };

        _runs[id] = h;
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return Snapshot(h);
    }

    public RunStatus Stop(string id)
    {
        if (_runs.TryRemove(id, out var h))
        {
            try { if (!h.Process.HasExited) h.Process.Kill(entireProcessTree: true); } catch { }
            h.State = RunState.NotRunning;
        }
        return new RunStatus(RunState.NotRunning, null, h?.Log ?? new());
    }

    public RunStatus Status(string id) =>
        _runs.TryGetValue(id, out var h) ? Snapshot(h) : new RunStatus(RunState.NotRunning, null, new());

    private static RunStatus Snapshot(Handle h)
    {
        lock (h.Log) return new RunStatus(h.State, h.DashboardUrl, new List<string>(h.Log));
    }

    public void Dispose()
    {
        foreach (var h in _runs.Values)
            try { if (!h.Process.HasExited) h.Process.Kill(entireProcessTree: true); } catch { }
    }
}
