using System.Diagnostics;
using AspireUI.Server.Services;

public class DeployServiceProjectTests
{
    private static (DeployService svc, List<string> calls) Fake()
    {
        var calls = new List<string>();
        var svc = new DeployService((workdir, args) =>
        {
            calls.Add(args);
            return new ProcessStartInfo { FileName = "cmd", Arguments = "/c exit 0" };
        });
        return (svc, calls);
    }

    [Fact]
    public void UpProject_passes_project_and_up_detached()
    {
        var (svc, calls) = Fake();
        svc.UpProject("/dir", "aspireui-abc");
        Assert.Contains("compose -p aspireui-abc up -d", calls);
    }

    [Fact]
    public void Stop_Start_Down_Ps_use_project()
    {
        var (svc, calls) = Fake();
        svc.StopProject("/d", "p"); svc.StartProject("/d", "p"); svc.DownProject("/d", "p"); svc.Ps("/d", "p");
        Assert.Contains("compose -p p stop", calls);
        Assert.Contains("compose -p p start", calls);
        Assert.Contains("compose -p p down", calls);
        Assert.Contains("compose -p p ps --format json", calls);
    }
    [Fact]
    public void VolumeRm_refuses_a_path_that_would_mean_the_whole_volume()
    {
        var (svc, _) = Fake();
        // `..` and `.` are dropped from the path, so these all reduce to /data itself. Deleting a whole
        // volume is what the volume list is for; the file browser must not do it by accident.
        foreach (var path in new[] { "", "/", "..", "../..", "./", "./../" })
        {
            var r = svc.VolumeRm("app_data", path);
            Assert.False(r.Ok, $"{path} should have been refused");
            Assert.Contains("refusing", r.Log);
        }
    }
}
