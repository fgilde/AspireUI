using AspireUI.Server.Models;
using AspireUI.Server.Services;

public class ExpirySweepTests
{
    private static StackModel S(string id, string? expireAt) =>
        new(id, id, "net10.0", new(), new(), new(), new(), new(), ExpireAt: expireAt);

    [Fact]
    public void Only_stacks_past_their_expiry_are_due()
    {
        var now = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        var due = ExpirySweep.Due([
            S("past", now.AddMinutes(-1).ToString("O")),
            S("exact", now.ToString("O")),
            S("future", now.AddDays(1).ToString("O")),
            S("never", null),
            S("broken", "not a date"),
        ], now);
        Assert.Equal(["past", "exact"], due);
    }
}
