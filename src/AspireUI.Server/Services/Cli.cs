using System.Diagnostics;

namespace AspireUI.Server.Services;

// Plain external-process helper for the tools an orchestrator target needs: kubectl, helm, az, aws,
// gcloud, ssh. Same result shape as DeployService so callers can treat both the same.
public static class Cli
{
    public static bool Exists(string exe) => Run(exe, ["--version"], timeoutMs: 15_000).Ok
        || Run(exe, ["version"], timeoutMs: 15_000).Ok;

    public static DeployResult Run(string exe, string[] args,
        IReadOnlyDictionary<string, string>? env = null, string? workdir = null,
        int timeoutMs = 120_000, string? stdin = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workdir ?? "",
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        if (stdin is not null) psi.RedirectStandardInput = true;
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new(false, $"could not start {exe}");
            var outp = p.StandardOutput.ReadToEndAsync();
            var errp = p.StandardError.ReadToEndAsync();
            if (stdin is not null) { p.StandardInput.Write(stdin); p.StandardInput.Flush(); p.StandardInput.Close(); }
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new(false, $"{exe} timed out after {timeoutMs / 1000}s");
            }
            var text = (p.ExitCode == 0 ? outp.Result : outp.Result + errp.Result).TrimEnd();
            return new(p.ExitCode == 0, text);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new(false, $"{exe} is not installed or not on PATH");
        }
        catch (Exception e) { return new(false, e.Message); }
    }
}
