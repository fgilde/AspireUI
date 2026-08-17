using System.Diagnostics;

namespace AspireUI.Server.Services;

public record DeployResult(bool Ok, string Log);

// Runs docker compose up/down in published output dir via injectable factory for testability.
public class DeployService
{
    private readonly Func<string, string, ProcessStartInfo> _commandFactory;

    public DeployService(Func<string, string, ProcessStartInfo>? commandFactory = null)
        => _commandFactory = commandFactory ?? DefaultCommand;

    private static ProcessStartInfo DefaultCommand(string workdir, string args) => new()
    {
        FileName = "docker",
        Arguments = args,
        WorkingDirectory = workdir,
    };

    public DeployResult Up(string outputDir) => Run(outputDir, "compose up -d");
    public DeployResult Down(string outputDir) => Run(outputDir, "compose down");

    // Project-scoped variants: stop/start/ps/logs target the same compose project deterministically.
    public DeployResult UpProject(string dir, string project, bool build = false) => Run(dir, $"compose -p {project} up -d{(build ? " --build" : "")}");
    public DeployResult StopProject(string dir, string project) => Run(dir, $"compose -p {project} stop");
    public DeployResult StartProject(string dir, string project) => Run(dir, $"compose -p {project} start");
    public DeployResult RestartProject(string dir, string project) => Run(dir, $"compose -p {project} restart");
    public DeployResult DownProject(string dir, string project, bool volumes = false) => Run(dir, $"compose -p {project} down{(volumes ? " -v" : "")}");
    public DeployResult Ps(string dir, string project) => Run(dir, $"compose -p {project} ps --format json");
    public DeployResult PullProject(string dir, string project) => Run(dir, $"compose -p {project} pull");
    public DeployResult ConfigImages(string dir, string project) => Run(dir, $"compose -p {project} config --images");
    public DeployResult Logs(string dir, string project, int tail = 200) => Run(dir, $"compose -p {project} logs --tail {tail}");
    public DeployResult Docker(string dir, string args) => Run(dir, args);

    // A one-off container from a service's own image, env and volumes, with the entrypoint replaced by a
    // shell. The only way to repair a service that crash-loops or is stopped: `docker exec` needs a
    // running container, this does not. --no-deps so a broken app can be fixed without starting the rest.
    public DeployResult RunOneOff(string dir, string project, string service, string cmd, int timeoutMs = 120_000) =>
        RunArgvIn(dir, timeoutMs, "compose", "-p", project, "run", "--rm", "--no-deps", "--entrypoint", "sh", service, "-c", cmd);

    // Run shell command in container via docker exec; ponytail: no PTY (non-interactive), WS+PTY when needed.
    public DeployResult Exec(string container, string cmd, int timeoutMs = 30_000)
    {
        var psi = new ProcessStartInfo("docker")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "exec", container, "sh", "-c", cmd }) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new(false, "could not start docker");
            var outp = p.StandardOutput.ReadToEndAsync();
            var errp = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(entireProcessTree: true); } catch { } return new(false, $"timed out after {timeoutMs / 1000}s"); }
            var text = (outp.Result + errp.Result).TrimEnd();
            return new(p.ExitCode == 0, text);
        }
        catch (Exception e) { return new(false, e.Message); }
    }

    private static string SafeRel(string p) =>
        string.Join('/', (p ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".." && s != "."));

    public DeployResult VolumeLs(string volume, string relPath)
        => RunArgv(20_000, "run", "--rm", "-v", $"{volume}:/data:ro", "alpine", "ls", "-la", "--", "/data/" + SafeRel(relPath));
    public DeployResult VolumeDu(string volume)
        => RunArgv(20_000, "run", "--rm", "-v", $"{volume}:/data:ro", "alpine", "du", "-sk", "/data");

    // Stream file from volume for download; null on error or non-regular file.
    public (byte[]? data, string? error) VolumeCat(string volume, string relPath)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "run", "--rm", "-v", $"{volume}:/data:ro", "alpine", "cat", "--", "/data/" + SafeRel(relPath) }) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (null, "could not start docker");
            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
            var errp = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30_000)) { try { p.Kill(entireProcessTree: true); } catch { } return (null, "timed out"); }
            copy.Wait();
            return p.ExitCode == 0 ? (ms.ToArray(), null) : (null, errp.Result.Trim());
        }
        catch (Exception e) { return (null, e.Message); }
    }

    private DeployResult RunArgvIn(string workdir, int timeoutMs, params string[] argv)
    {
        var psi = new ProcessStartInfo("docker")
        { WorkingDirectory = workdir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in argv) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new(false, "could not start docker");
            var outp = p.StandardOutput.ReadToEndAsync();
            var errp = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(entireProcessTree: true); } catch { } return new(false, $"timed out after {timeoutMs / 1000}s"); }
            return new(p.ExitCode == 0, (outp.Result + errp.Result).TrimEnd());
        }
        catch (Exception e) { return new(false, e.Message); }
    }

    private DeployResult RunArgv(int timeoutMs, params string[] argv)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in argv) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new(false, "could not start docker");
            var outp = p.StandardOutput.ReadToEndAsync();
            var errp = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(entireProcessTree: true); } catch { } return new(false, "timed out"); }
            return new(p.ExitCode == 0, (p.ExitCode == 0 ? outp.Result : outp.Result + errp.Result).TrimEnd());
        }
        catch (Exception e) { return new(false, e.Message); }
    }

    private DeployResult Run(string workdir, string args)
    {
        var psi = _commandFactory(workdir, args);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        var log = new List<string>();
        void OnLine(string? line) { if (line is not null) lock (log) log.Add(line); }

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);

        bool ok;
        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            if (!proc.WaitForExit(300_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                OnLine("docker compose timed out after 5 minutes.");
                ok = false;
            }
            else { proc.WaitForExit(); ok = proc.ExitCode == 0; }
        }
        catch (Exception ex)
        {
            OnLine($"Failed to run docker: {ex.Message}");
            ok = false;
        }

        string logText;
        lock (log) logText = string.Join("\n", log);
        return new DeployResult(ok, logText);
    }
}
