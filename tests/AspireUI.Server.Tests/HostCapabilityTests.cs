using System.Diagnostics;
using AspireUI.Server.Services;

// An app can need something from the Docker host that its image cannot bring: the Android emulator
// needs /dev/kvm and refuses to run without it, while the container around it starts anyway and
// keeps serving its landing page. Getting this wrong in either direction is bad — claiming a host
// cannot do it when docker was merely unreachable is as unhelpful as saying nothing.
public class HostCapabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_probe_that_ran_means_the_host_has_the_device()
    {
        var cap = HostCapabilities.Read(new DeployResult(true, ""), Now);

        Assert.True(cap.Available);
        Assert.Equal(HostCapabilities.Kvm, cap.Id);
        Assert.Equal(Now, cap.Checked);
    }

    // The two shapes docker uses when the device is not on the host.
    [Theory]
    [InlineData("docker: Error response from daemon: error gathering device information while adding custom device \"/dev/kvm\": no such file or directory")]
    [InlineData("Error response from daemon: linux runtime spec devices: error gathering device information while adding custom device \"/dev/kvm\": no such file or directory")]
    public void The_device_missing_is_a_no(string log)
    {
        var cap = HostCapabilities.Read(new DeployResult(false, log), Now);

        Assert.False(cap.Available);
        Assert.Contains("/dev/kvm", cap.Detail);
    }

    // Anything else is not an answer. A host that could run it must not be told it cannot because
    // docker happened to be down or the probe image could not be pulled.
    [Theory]
    [InlineData("error during connect: open //./pipe/dockerDesktopLinuxEngine: The system cannot find the file specified.")]
    [InlineData("docker: Error response from daemon: pull access denied for alpine, repository does not exist")]
    [InlineData("")]
    public void Anything_else_stays_unanswered(string log)
    {
        var cap = HostCapabilities.Read(new DeployResult(false, log), Now);

        Assert.Null(cap.Available);
        Assert.Contains("could not ask", cap.Detail);
    }

    [Fact]
    public void A_missing_file_that_is_not_the_device_is_not_a_no()
    {
        // "no such file or directory" on its own says nothing about /dev/kvm.
        var cap = HostCapabilities.Read(new DeployResult(false, "docker: open /var/run/docker.sock: no such file or directory"), Now);

        Assert.Null(cap.Available);
    }

    [Fact]
    public void The_answer_is_kept_rather_than_asked_again()
    {
        var runs = new List<string>();
        var caps = new HostCapabilities(new DeployService((_, args) => { runs.Add(args); return Exit(0); }));

        caps.KvmSupport();
        caps.KvmSupport();
        Assert.Single(runs);

        // The probe is the question: docker refuses to create a container for a device the host
        // does not have, so trying to pass it is what produces the answer.
        Assert.Contains("--device /dev/kvm", runs[0]);
        Assert.Contains(HostCapabilities.ProbeImage, runs[0]);

        caps.KvmSupport(refresh: true);
        Assert.Equal(2, runs.Count);
    }

    [Fact]
    public void The_android_template_says_what_it_needs()
    {
        var template = new TemplateService().List().Single(t => t.Id == ApkTemplate.Id);
        Assert.Equal([HostCapabilities.Kvm], template.Requires);
    }

    private static ProcessStartInfo Exit(int code) => OperatingSystem.IsWindows()
        ? new ProcessStartInfo { FileName = "cmd.exe", Arguments = $"/c exit {code}" }
        : new ProcessStartInfo { FileName = "/bin/sh", Arguments = $"-c \"exit {code}\"" };
}
