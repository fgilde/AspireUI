using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

/// <summary>
/// When a scheduled action is due, and what it means. Kept apart from the service that runs it so
/// the decision can be tested without a clock, a database or a docker daemon.
/// </summary>
public static class AppScheduler
{
    /// <summary>Restart, stop, start, update, check for updates, back up.</summary>
    public static readonly string[] Actions = ["restart", "stop", "start", "update", "check-updates", "backup"];

    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    /// <summary>Where the last run of one action on one app is remembered.</summary>
    public static string LastRunKey(string stackId, string action) => $"sched:{stackId}:{action}";

    /// <summary>
    /// True when this schedule should run now. A daily schedule fires in the hour and minute it names
    /// and not again for the rest of the day; an interval schedule fires once the interval since the
    /// last run has passed. A schedule that has never run does not fire retroactively — it waits for
    /// its next real slot, so adding one at 17:00 does not immediately restart the app.
    /// </summary>
    public static bool IsDue(AppSchedule s, DateTime? lastRun, DateTime nowUtc, int tickMinutes = 5)
    {
        if (!s.Enabled || !Actions.Contains(s.Action)) return false;
        if (!DayAllowed(s.Days, nowUtc)) return false;

        if (s.AtHour is { } hour)
        {
            if (nowUtc.Hour != Math.Clamp(hour, 0, 23)) return false;
            var minute = Math.Clamp(s.AtMinute, 0, 59);
            if (nowUtc.Minute < minute || nowUtc.Minute >= minute + Math.Max(1, tickMinutes)) return false;
            // Once per day, not once per tick inside that minute window.
            return lastRun is null || nowUtc - lastRun.Value >= TimeSpan.FromHours(23);
        }

        if (s.EveryHours is > 0)
        {
            if (lastRun is null) return false;   // first slot is one interval from now, not now
            return nowUtc - lastRun.Value >= TimeSpan.FromHours(s.EveryHours.Value);
        }

        return false;
    }

    /// <summary>An interval schedule that has never run gets its clock started, so the first slot is real.</summary>
    public static bool NeedsClockStarted(AppSchedule s, DateTime? lastRun) =>
        s.Enabled && lastRun is null && s.AtHour is null && s.EveryHours is > 0;

    /// <summary><c>mon,wed,fri</c> — empty or missing means every day.</summary>
    public static bool DayAllowed(string? days, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(days)) return true;
        var today = DayNames[(int)nowUtc.DayOfWeek];
        return days.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(d => d.StartsWith(today, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Drops the entries that name nothing runnable, so a saved list is always a valid one.</summary>
    public static List<AppSchedule> Clean(IEnumerable<AppSchedule>? schedules) =>
        (schedules ?? [])
        .Where(s => Actions.Contains(s.Action) && (s.AtHour is not null || s.EveryHours is > 0))
        .Select(s => s with
        {
            AtHour = s.AtHour is { } h ? Math.Clamp(h, 0, 23) : null,
            AtMinute = Math.Clamp(s.AtMinute, 0, 59),
            EveryHours = s.EveryHours is { } e ? Math.Clamp(e, 1, 8760) : null,
        })
        .ToList();
}
