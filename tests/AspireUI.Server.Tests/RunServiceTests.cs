using AspireUI.Server.Services;

public class RunServiceTests
{
    [Fact]
    public void ParseDashboardUrl_ExtractsLoginUrl()
    {
        var line = "Login to the dashboard at https://localhost:17123/login?t=abc123def";
        Assert.Equal("https://localhost:17123/login?t=abc123def", RunService.ParseDashboardUrl(line));
        Assert.Null(RunService.ParseDashboardUrl("nothing here"));
    }

    [Fact]
    public void Lifecycle_StartRunsThenStop()
    {
        // Dummy command that prints a dashboard line then sleeps, cross-platform via dotnet fsi is heavy;
        // use a shell that echoes the URL and stays alive.
        var svc = new RunService(_ =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo();
            if (OperatingSystem.IsWindows())
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c echo Login to the dashboard at https://localhost:18888/login?t=tok && ping -n 30 127.0.0.1 > NUL";
            }
            else
            {
                psi.FileName = "/bin/sh";
                psi.Arguments = "-c \"echo Login to the dashboard at https://localhost:18888/login?t=tok; sleep 30\"";
            }
            return psi;
        });

        svc.Start("s1", ".");
        // poll up to ~5s for the dashboard url to be parsed
        RunStatus st = svc.Status("s1");
        for (int i = 0; i < 50 && st.DashboardUrl is null; i++) { System.Threading.Thread.Sleep(100); st = svc.Status("s1"); }
        Assert.Equal("https://localhost:18888/login?t=tok", st.DashboardUrl);
        Assert.Equal(RunState.Running, st.State);

        var stopped = svc.Stop("s1");
        Assert.Equal(RunState.NotRunning, stopped.State);
    }
}
