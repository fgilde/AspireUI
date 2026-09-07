using AspireUI.Server.Endpoints;
using AspireUI.Server.Services;

// The two decisions the terminal makes before any bytes move: whose container this is, and whether a
// frame is something the user typed or a window change.
public class PtyTests
{
    [Fact]
    public void Only_the_apps_own_containers_may_be_attached_to()
    {
        const string project = "aspireui-1a2b3c4d";

        Assert.True(PtyService.Allowed($"{project}-web-1", project));
        Assert.True(PtyService.Allowed($"{project}_db_1", project));

        Assert.False(PtyService.Allowed("aspireui-99999999-web-1", project));
        Assert.False(PtyService.Allowed("someone-elses-postgres", project));
        Assert.False(PtyService.Allowed(project, project));            // the prefix alone is not a container
        Assert.False(PtyService.Allowed($"x{project}-web-1", project)); // and it has to be a prefix
        Assert.False(PtyService.Allowed("", project));
    }

    [Fact]
    public void A_resize_frame_is_told_apart_from_something_typed()
    {
        Assert.Equal((120, 30), PtyEndpoints.Resize("""{"resize":{"cols":120,"rows":30}}"""));
        Assert.Null(PtyEndpoints.Resize("ls -la\n"));
        Assert.Null(PtyEndpoints.Resize(""));
        // Something that looks like one but is not.
        Assert.Null(PtyEndpoints.Resize("""{"resize":{"cols":"wide"}}"""));
        Assert.Null(PtyEndpoints.Resize("""{"resize":"""));
    }

    [Fact]
    public void The_resize_command_is_clamped_to_something_a_terminal_can_be()
    {
        Assert.Contains("rows 30", PtyService.ResizeCommand(120, 30));
        Assert.Contains("cols 120", PtyService.ResizeCommand(120, 30));
        Assert.Contains("rows 4", PtyService.ResizeCommand(1, 0));
        Assert.Contains("cols 500", PtyService.ResizeCommand(99999, 40));
        // A leading space keeps it out of the shell's history.
        Assert.StartsWith(" stty", PtyService.ResizeCommand(80, 24));
    }
}
