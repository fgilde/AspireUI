using AspireUI.Server.Services;
using Xunit;

namespace AspireUI.Server.Tests;

public class NotifyServiceTests
{
    [Theory]
    [InlineData(null, "running", false)]
    [InlineData("deploying", "running", true)]
    [InlineData("running", "failed", true)]
    [InlineData("running", "stopped", true)]
    [InlineData("running", "running", false)]
    [InlineData("stopped", "deploying", false)]
    [InlineData("failed", "deploying", false)]
    public void ShouldNotify_only_on_real_transition_into_notable_state(string? prev, string next, bool expected)
        => Assert.Equal(expected, NotifyService.ShouldNotify(prev, next));
}
