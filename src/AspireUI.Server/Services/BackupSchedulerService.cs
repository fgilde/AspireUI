using Microsoft.Extensions.Hosting;

namespace AspireUI.Server.Services;

public class BackupSchedulerService : BackgroundService
{
    private static string DataDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
    private static string Db() => Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(DataDir(), "aspireui.db");
    private static string WsRoot() => Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Path.Combine(DataDir(), "workspace");
    private static string BackupsRoot() => Path.Combine(WsRoot(), "_backups");

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        while (!stop.IsCancellationRequested)
        {
            try { RunDueBackups(); } catch { }
            try { if (!await timer.WaitForNextTickAsync(stop)) break; } catch (OperationCanceledException) { break; }
        }
    }

    private static void RunDueBackups()
    {
        var settings = new SettingsStore(Db());
        if (!int.TryParse(settings.GetValue("BackupIntervalHours"), out var hours) || hours <= 0) return;
        var last = DateTime.TryParse(settings.GetValue("BackupLastRun"), out var lr) ? lr : DateTime.MinValue;
        if (DateTime.UtcNow - last < TimeSpan.FromHours(hours)) return;

        var retain = int.TryParse(settings.GetValue("BackupRetain"), out var r) && r > 0 ? r : 7;
        var store = new DeploymentStore(Db());
        var hosting = new HostingService(store, new PublishService(new CodeGenService()), new DeployService());
        foreach (var d in store.List().Where(d => d.State == "running"))
        {
            try
            {
                hosting.Backup(d.Id, BackupsRoot());
                foreach (var old in hosting.ListBackups(d.Id, BackupsRoot()).OrderByDescending(b => b.Stamp).Skip(retain))
                    hosting.DeleteBackup(d.Id, BackupsRoot(), old.Stamp);
            }
            catch { }
        }
        settings.SetValue("BackupLastRun", DateTime.UtcNow.ToString("O"));
    }
}
