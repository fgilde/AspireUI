using AspireUI.Server.Services;

public class TargetResourcesTests
{
    [Fact]
    public void Df_output_gives_free_megabytes()
    {
        var log = "Filesystem     1024-blocks      Used Available Capacity Mounted on\noverlay          102400000  50000000  51200000      50% /\n";
        Assert.Equal(50000, TargetService.ParseDfFreeMb(log));
        Assert.Null(TargetService.ParseDfFreeMb("garbage"));
    }

    [Fact]
    public void Meminfo_gives_available_megabytes()
    {
        var log = "MemTotal:       16384000 kB\nMemFree:          512000 kB\nMemAvailable:    2048000 kB\n";
        Assert.Equal(2000, TargetService.ParseMemAvailableMb(log));
        Assert.Null(TargetService.ParseMemAvailableMb("MemTotal: 1 kB"));
    }
}
