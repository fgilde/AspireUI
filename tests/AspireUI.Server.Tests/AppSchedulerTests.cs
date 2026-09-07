using AspireUI.Server.Models;
using AspireUI.Server.Services;

// When a scheduled action fires. No clock, no docker: just the decision.
public class AppSchedulerTests
{
    private static DateTime At(int hour, int minute = 0, int day = 5) =>
        new(2026, 9, day, hour, minute, 0, DateTimeKind.Utc);   // 2026-09-05 is a Saturday

    [Fact]
    public void A_daily_schedule_fires_in_the_minute_it_names()
    {
        var s = new AppSchedule("restart", AtHour: 4, AtMinute: 30);
        var yesterday = At(4, 30, 4);

        Assert.True(AppScheduler.IsDue(s, yesterday, At(4, 30)));
        Assert.True(AppScheduler.IsDue(s, yesterday, At(4, 34)));   // still inside the tick window
        Assert.False(AppScheduler.IsDue(s, yesterday, At(4, 35)));  // the window has passed
        Assert.False(AppScheduler.IsDue(s, yesterday, At(4, 29)));
        Assert.False(AppScheduler.IsDue(s, yesterday, At(5, 30)));
    }

    [Fact]
    public void A_daily_schedule_fires_once_a_day_not_once_a_tick()
    {
        var s = new AppSchedule("restart", AtHour: 4);
        Assert.True(AppScheduler.IsDue(s, At(4, 0, 4), At(4, 0)));
        Assert.False(AppScheduler.IsDue(s, At(4, 1), At(4, 3)));
    }

    [Fact]
    public void A_daily_schedule_that_never_ran_fires_at_its_first_slot()
    {
        var s = new AppSchedule("backup", AtHour: 3);
        Assert.True(AppScheduler.IsDue(s, null, At(3, 2)));
        Assert.False(AppScheduler.IsDue(s, null, At(9)));
    }

    [Fact]
    public void Days_narrow_a_daily_schedule()
    {
        var s = new AppSchedule("update", AtHour: 4, Days: "mon,wed");
        Assert.False(AppScheduler.IsDue(s, null, At(4, 0, 5)));      // Saturday
        Assert.True(AppScheduler.IsDue(s, null, At(4, 0, 7)));       // Monday
        Assert.True(AppScheduler.DayAllowed("sat", At(4, 0, 5)));
        Assert.True(AppScheduler.DayAllowed(null, At(4, 0, 5)));
        Assert.True(AppScheduler.DayAllowed("saturday", At(4, 0, 5)));
    }

    [Fact]
    public void An_interval_schedule_waits_one_interval_and_does_not_fire_retroactively()
    {
        var s = new AppSchedule("check-updates", EveryHours: 6);
        // A new schedule has no last run: its clock is started instead of firing at once.
        Assert.True(AppScheduler.NeedsClockStarted(s, null));
        Assert.False(AppScheduler.IsDue(s, null, At(12)));

        Assert.False(AppScheduler.IsDue(s, At(7), At(12)));
        Assert.True(AppScheduler.IsDue(s, At(6), At(12)));
    }

    [Fact]
    public void A_daily_schedule_never_needs_its_clock_started()
    {
        Assert.False(AppScheduler.NeedsClockStarted(new AppSchedule("restart", AtHour: 4), null));
    }

    [Fact]
    public void A_disabled_or_unknown_schedule_never_fires()
    {
        Assert.False(AppScheduler.IsDue(new AppSchedule("restart", AtHour: 4, Enabled: false), null, At(4)));
        Assert.False(AppScheduler.IsDue(new AppSchedule("rm -rf", AtHour: 4), null, At(4)));
    }

    [Fact]
    public void Cleaning_drops_what_names_nothing_and_clamps_the_rest()
    {
        var cleaned = AppScheduler.Clean(
        [
            new AppSchedule("restart", AtHour: 99, AtMinute: 77),
            new AppSchedule("update", EveryHours: 0),          // neither a time nor an interval
            new AppSchedule("nonsense", AtHour: 4),            // not an action
            new AppSchedule("backup", EveryHours: 99999),
        ]);

        Assert.Equal(2, cleaned.Count);
        Assert.Equal(23, cleaned[0].AtHour);
        Assert.Equal(59, cleaned[0].AtMinute);
        Assert.Equal(8760, cleaned[1].EveryHours);
        Assert.Empty(AppScheduler.Clean(null));
    }

    [Fact]
    public void Every_action_the_ui_offers_is_one_the_scheduler_runs()
    {
        Assert.Equal(["restart", "stop", "start", "update", "check-updates", "backup"], AppScheduler.Actions);
    }
}
