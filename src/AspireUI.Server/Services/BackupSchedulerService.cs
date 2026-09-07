using Microsoft.Extensions.Hosting;

namespace AspireUI.Server.Services;

/// <summary>
/// The clock the hosted apps run on: the global backup interval, and every app's own scheduled
/// actions (restart, stop, start, update, check for updates, back up). One timer for both, because
/// they need exactly the same set of stores and the same "is it due yet" question.
/// </summary>
public class BackupSchedulerService : BackgroundService
{
    private const int TickMinutes = 5;

    private static string DataDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireUI");
    private static string Db() => Environment.GetEnvironmentVariable("DB_PATH") ?? Path.Combine(DataDir(), "aspireui.db");
    private static string WsRoot() => Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Path.Combine(DataDir(), "workspace");
    private static string BackupsRoot() => Path.Combine(WsRoot(), "_backups");

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(TickMinutes));
        while (!stop.IsCancellationRequested)
        {
            try { RunDueBackups(); } catch { }
            try { RunDueAppSchedules(); } catch { }
            try { if (!await timer.WaitForNextTickAsync(stop)) break; } catch (OperationCanceledException) { break; }
        }
    }

    private static HostingService Hosting(DeploymentStore store) =>
        new(store, new PublishService(new CodeGenService()), new DeployService(), targets: TargetService.FromEnvironment());

    private static void RunDueBackups()
    {
        var settings = new SettingsStore(Db());
        if (!int.TryParse(settings.GetValue("BackupIntervalHours"), out var hours) || hours <= 0) return;
        var last = DateTime.TryParse(settings.GetValue("BackupLastRun"), out var lr) ? lr : DateTime.MinValue;
        if (DateTime.UtcNow - last < TimeSpan.FromHours(hours)) return;

        var store = new DeploymentStore(Db());
        var hosting = Hosting(store);
        foreach (var d in store.List().Where(d => d.State == "running"))
            try { BackupAndPrune(hosting, settings, d.Id); } catch { }
        settings.SetValue("BackupLastRun", DateTime.UtcNow.ToString("O"));
    }

    private static void BackupAndPrune(HostingService hosting, SettingsStore settings, string deploymentId)
    {
        var retain = int.TryParse(settings.GetValue("BackupRetain"), out var r) && r > 0 ? r : 7;
        var dir = hosting.Backup(deploymentId, BackupsRoot());
        Offsite(settings, deploymentId, dir);
        foreach (var old in hosting.ListBackups(deploymentId, BackupsRoot()).OrderByDescending(b => b.Stamp).Skip(retain))
            hosting.DeleteBackup(deploymentId, BackupsRoot(), old.Stamp);
    }

    // A scheduled backup that only ever lands on the same disk is a backup of that disk being fine.
    private static void Offsite(SettingsStore settings, string deploymentId, string? dir)
    {
        if (dir is null || !Directory.Exists(dir)) return;
        var remote = new RemoteBackupService(settings, new SecretStore(Db(), DataDir()));
        if (!remote.Config().Enabled) return;
        var store = new DeploymentStore(Db());
        if (store.Get(deploymentId) is not { } d) return;
        var stamp = Path.GetFileName(dir);
        foreach (var file in Directory.GetFiles(dir))
        {
            var (ok, error) = remote.UploadAsync(file, $"{d.StackId}/{stamp}/{Path.GetFileName(file)}")
                .GetAwaiter().GetResult();
            if (!ok) Console.Error.WriteLine($"backup: off-site copy of {Path.GetFileName(file)} failed — {error}");
        }
    }

    // Per-app schedules. Anything that fails is left to the next tick: a scheduler that stops at the
    // first app with a problem is worse than one that skips it.
    private static void RunDueAppSchedules()
    {
        var stacks = new StackStore(Db());
        var withSchedules = stacks.List().Where(s => s.Schedules is { Count: > 0 }).ToList();
        if (withSchedules.Count == 0) return;

        var settings = new SettingsStore(Db());
        var store = new DeploymentStore(Db());
        var hosting = Hosting(store);
        var now = DateTime.UtcNow;

        foreach (var stack in withSchedules)
        {
            if (store.GetByStack(stack.Id) is not { } dep) continue;
            foreach (var schedule in stack.Schedules!)
            {
                var key = AppScheduler.LastRunKey(stack.Id, schedule.Action);
                var last = DateTime.TryParse(settings.GetValue(key), out var lr) ? lr : (DateTime?)null;
                if (AppScheduler.NeedsClockStarted(schedule, last))
                {
                    settings.SetValue(key, now.ToString("O"));
                    continue;
                }
                if (!AppScheduler.IsDue(schedule, last, now, TickMinutes)) continue;

                settings.SetValue(key, now.ToString("O"));
                try { Run(hosting, settings, dep, schedule.Action); }
                catch (Exception ex) { Console.Error.WriteLine($"schedule: {schedule.Action} on {stack.Name} failed — {ex.Message}"); }
            }
        }
    }

    private static void Run(HostingService hosting, SettingsStore settings, Models.Deployment dep, string action)
    {
        switch (action)
        {
            case "restart":
                hosting.Stop(dep.Id);
                hosting.Start(dep.Id);
                break;
            case "stop": hosting.Stop(dep.Id); break;
            case "start": hosting.Start(dep.Id); break;
            case "update": hosting.Update(dep.Id); break;
            // Reporting only: the pull tells us whether a newer image exists, the notification says so.
            case "check-updates":
                var newer = hosting.CheckImages(dep.Id).Where(i => i.UpdateAvailable).Select(i => i.Image).ToList();
                if (newer.Count > 0)
                    _ = NotifyService.DispatchAll(settings, $"⬆️ {dep.Name} has updates",
                        "\n\nA newer image is available for: " + string.Join(", ", newer));
                break;
            case "backup": BackupAndPrune(hosting, settings, dep.Id); break;
        }
    }
}
