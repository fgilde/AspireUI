using System.Diagnostics;

namespace AspireUI.Server.Services;

public record DeployResult(bool Ok, string Log);

// Runs docker/compose commands. Every process this class starts goes through Psi(), so pointing it at
// another machine is a matter of environment (DOCKER_HOST, DOCKER_CONTEXT, TLS paths) — see
// TargetService.Runner. The command factory stays for tests that want to fake docker entirely.
public class DeployService
{
    private readonly Func<string, string, ProcessStartInfo> _commandFactory;
    private readonly IReadOnlyDictionary<string, string>? _env;

    public DeployService(Func<string, string, ProcessStartInfo>? commandFactory = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        _commandFactory = commandFactory ?? DefaultCommand;
        _env = env;
    }

    // Same docker plumbing, different daemon.
    public DeployService WithEnvironment(IReadOnlyDictionary<string, string>? env) => new(_commandFactory, env);

    public IReadOnlyDictionary<string, string> Environment => _env ?? new Dictionary<string, string>();

    private static ProcessStartInfo DefaultCommand(string workdir, string args) => new()
    {
        FileName = "docker",
        Arguments = args,
        WorkingDirectory = workdir,
    };

    private ProcessStartInfo Psi(string? workdir, IEnumerable<string>? argv = null, string? args = null)
    {
        var psi = argv is not null || args is null
            ? new ProcessStartInfo("docker") { WorkingDirectory = workdir ?? "" }
            : _commandFactory(workdir ?? "", args);
        if (argv is not null) foreach (var a in argv) psi.ArgumentList.Add(a);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        if (_env is not null)
            foreach (var (k, v) in _env) psi.Environment[k] = v;
        return psi;
    }

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
    public DeployResult Exec(string container, string cmd, int timeoutMs = 30_000) =>
        RunArgv(timeoutMs, "exec", container, "sh", "-c", cmd);

    private static string SafeRel(string p) =>
        string.Join('/', (p ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".." && s != "."));

    public DeployResult VolumeLs(string volume, string relPath)
        => RunArgv(20_000, "run", "--rm", "-v", $"{volume}:/data:ro", "alpine", "ls", "-la", "--", "/data/" + SafeRel(relPath));
    public DeployResult VolumeDu(string volume)
        => RunArgv(20_000, "run", "--rm", "-v", $"{volume}:/data:ro", "alpine", "du", "-sk", "/data");

    // Stream file from volume for download; null on error or non-regular file.
    public (byte[]? data, string? error) VolumeCat(string volume, string relPath)
    {
        var (data, err) = StreamOut(120_000, "run", "--rm", "-v", $"{volume}:/data:ro", "alpine", "cat", "--", "/data/" + SafeRel(relPath));
        return (data, err);
    }

    // Volume contents as a tar stream we read ourselves. A bind mount would land on the *daemon's* host,
    // which is the wrong machine as soon as the target is remote — stdout always comes back to us.
    public (byte[]? data, string? error) VolumeTarOut(string volume) =>
        StreamOut(600_000, "run", "--rm", "-v", $"{volume}:/data:ro", "alpine", "tar", "cf", "-", "-C", "/data", ".");

    // Same thing without holding the whole volume in memory: straight into the caller's stream.
    public DeployResult VolumeTarOutTo(string volume, Stream dest, int timeoutMs = 1_800_000)
    {
        try
        {
            using var p = Process.Start(Psi(null, ["run", "--rm", "-v", $"{volume}:/data:ro", "alpine", "tar", "cf", "-", "-C", "/data", "."]));
            if (p is null) return new(false, "could not start docker");
            var errp = p.StandardError.ReadToEndAsync();
            p.StandardOutput.BaseStream.CopyTo(dest);
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(entireProcessTree: true); } catch { } return new(false, "timed out"); }
            return new(p.ExitCode == 0, p.ExitCode == 0 ? "" : errp.Result.Trim());
        }
        catch (Exception e) { return new(false, e.Message); }
    }

    // Volume-to-volume across two daemons: one tar writing to stdout piped into one tar reading stdin,
    // so a 40 GB volume moves between machines without ever being buffered here.
    public static DeployResult TransferVolume(DeployService from, string fromVolume, DeployService to, string toVolume,
        int timeoutMs = 3_600_000)
    {
        try
        {
            using var src = Process.Start(from.Psi(null, ["run", "--rm", "-v", $"{fromVolume}:/data:ro", "alpine", "tar", "cf", "-", "-C", "/data", "."]));
            if (src is null) return new(false, "could not start docker on the source");
            var dstPsi = to.Psi(null, ["run", "--rm", "-i", "-v", $"{toVolume}:/data", "alpine", "sh", "-c",
                "find /data -mindepth 1 -delete; tar xf - -C /data"]);
            dstPsi.RedirectStandardInput = true;
            using var dst = Process.Start(dstPsi);
            if (dst is null) { try { src.Kill(true); } catch { } return new(false, "could not start docker on the destination"); }
            var srcErr = src.StandardError.ReadToEndAsync();
            var dstErr = dst.StandardError.ReadToEndAsync();
            src.StandardOutput.BaseStream.CopyTo(dst.StandardInput.BaseStream);
            dst.StandardInput.BaseStream.Flush();
            dst.StandardInput.Close();
            var okSrc = src.WaitForExit(timeoutMs);
            var okDst = dst.WaitForExit(timeoutMs);
            if (!okSrc || !okDst)
            {
                try { src.Kill(true); } catch { }
                try { dst.Kill(true); } catch { }
                return new(false, "the transfer timed out");
            }
            var ok = src.ExitCode == 0 && dst.ExitCode == 0;
            return new(ok, ok ? $"{fromVolume} → {toVolume}" : (srcErr.Result + dstErr.Result).Trim());
        }
        catch (Exception e) { return new(false, e.Message); }
    }

    // The other direction: replace a volume's contents from a tar stream we send on stdin.
    public DeployResult VolumeTarIn(string volume, Stream tar, bool wipe = true)
    {
        var script = wipe ? "find /data -mindepth 1 -delete; tar xf - -C /data" : "tar xf - -C /data";
        return StreamIn(600_000, tar, "run", "--rm", "-i", "-v", $"{volume}:/data", "alpine", "sh", "-c", script);
    }

    // Ports already taken on the target daemon, so a remote host's ports are not guessed from ours.
    public DeployResult UsedPorts() => RunArgv(20_000, "ps", "--all", "--format", "{{.Ports}}");

    public DeployResult Version() => RunArgv(20_000, "version", "--format", "{{json .}}");
    public DeployResult Info(string format) => RunArgv(20_000, "info", "--format", format);
    public DeployResult ComposeVersion() => RunArgv(20_000, "compose", "version", "--short");
    public DeployResult ImageManifest(string image) => RunArgv(60_000, "manifest", "inspect", image);
    public DeployResult Login(string registry, string user, string password) =>
        StreamIn(60_000, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(password)),
            "login", registry, "-u", user, "--password-stdin");
    public DeployResult Push(string image) => RunArgv(600_000, "push", image);
    public DeployResult Tag(string from, string to) => RunArgv(20_000, "tag", from, to);

    private DeployResult RunArgvIn(string workdir, int timeoutMs, params string[] argv)
    {
        var psi = Psi(workdir, argv);
        return Collect(psi, timeoutMs, includeStdErrOnSuccess: true);
    }

    private DeployResult RunArgv(int timeoutMs, params string[] argv)
    {
        var psi = Psi(null, argv);
        return Collect(psi, timeoutMs, includeStdErrOnSuccess: false);
    }

    private static DeployResult Collect(ProcessStartInfo psi, int timeoutMs, bool includeStdErrOnSuccess)
    {
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new(false, "could not start docker");
            var outp = p.StandardOutput.ReadToEndAsync();
            var errp = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(entireProcessTree: true); } catch { } return new(false, $"timed out after {timeoutMs / 1000}s"); }
            var text = p.ExitCode == 0 && !includeStdErrOnSuccess ? outp.Result : outp.Result + errp.Result;
            return new(p.ExitCode == 0, text.TrimEnd());
        }
        catch (Exception e) { return new(false, e.Message); }
    }

    private (byte[]? data, string? error) StreamOut(int timeoutMs, params string[] argv)
    {
        try
        {
            using var p = Process.Start(Psi(null, argv));
            if (p is null) return (null, "could not start docker");
            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
            var errp = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(entireProcessTree: true); } catch { } return (null, "timed out"); }
            copy.Wait();
            return p.ExitCode == 0 ? (ms.ToArray(), null) : (null, errp.Result.Trim());
        }
        catch (Exception e) { return (null, e.Message); }
    }

    private DeployResult StreamIn(int timeoutMs, Stream input, params string[] argv)
    {
        try
        {
            var psi = Psi(null, argv);
            psi.RedirectStandardInput = true;
            using var p = Process.Start(psi);
            if (p is null) return new(false, "could not start docker");
            var outp = p.StandardOutput.ReadToEndAsync();
            var errp = p.StandardError.ReadToEndAsync();
            input.CopyTo(p.StandardInput.BaseStream);
            p.StandardInput.BaseStream.Flush();
            p.StandardInput.Close();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(entireProcessTree: true); } catch { } return new(false, "timed out"); }
            return new(p.ExitCode == 0, (outp.Result + errp.Result).TrimEnd());
        }
        catch (Exception e) { return new(false, e.Message); }
    }

    private DeployResult Run(string workdir, string args)
    {
        var psi = Psi(workdir, args: args);

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
