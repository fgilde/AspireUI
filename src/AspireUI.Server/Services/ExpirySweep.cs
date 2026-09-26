using System.Globalization;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

public static class ExpirySweep
{
    public static IReadOnlyList<string> Due(IEnumerable<StackModel> stacks, DateTime utcNow) =>
        stacks.Where(s => s.ExpireAt is { } e
                && DateTime.TryParse(e, null, DateTimeStyles.RoundtripKind, out var due)
                && due.ToUniversalTime() <= utcNow)
            .Select(s => s.Id).ToList();
}
