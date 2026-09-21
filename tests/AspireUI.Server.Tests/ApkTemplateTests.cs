using AspireUI.Server.Models;
using AspireUI.Server.Services;

// An Android app as a stack: the emulator alone is a device nobody put an app on, so the template is
// only useful if the installer sidecar survives the trip into generated C# intact - it is a whole
// shell script carried as one string argument.
public class ApkTemplateTests
{
    private static StackModel Stack() => ApkTemplate.Create("s1");

    [Fact]
    public void The_template_is_offered_and_builds()
    {
        var service = new TemplateService();
        Assert.Contains(service.List(), t => t.Id == ApkTemplate.Id);
        Assert.NotNull(service.Create(ApkTemplate.Id));
    }

    [Fact]
    public void It_is_a_device_an_installer_and_the_apk_to_install()
    {
        var stack = Stack();

        var device = stack.Nodes.Single(n => n.ResourceName == "device");
        var installer = stack.Nodes.Single(n => n.ResourceName == "installer");
        var apk = stack.Nodes.Single(n => n.AddMethod == "AddParameter");

        Assert.Equal("AddContainer", device.AddMethod);
        Assert.Equal("AddContainer", installer.AddMethod);
        Assert.Contains("budtmo/docker-android", device.AddArgs[0]);
        Assert.Contains(ApkTemplate.SampleApk, apk.AddArgs[0]);

        // Installing before the device finished booting reports success and installs nothing, so the
        // installer has to start after the device rather than beside it.
        Assert.Contains(stack.Edges, e => e.Kind == "waitFor" && e.FromNodeId == installer.Id && e.ToNodeId == device.Id);
    }

    [Fact]
    public void The_apk_is_a_parameter_the_installer_reads()
    {
        var stack = Stack();
        var apk = stack.Nodes.Single(n => n.AddMethod == "AddParameter");
        var installer = stack.Nodes.Single(n => n.ResourceName == "installer");

        // Not the literal URL: swapping the app is editing one parameter, not the sidecar.
        var urls = installer.WithCalls.Single(w => w.Method == "WithEnvironment" && w.Args[0] == "\"APK_URLS\"");
        Assert.Equal(apk.VarName, urls.Args[1]);
    }

    [Fact]
    public void The_device_is_reachable_from_a_browser_and_over_adb()
    {
        var device = Stack().Nodes.Single(n => n.ResourceName == "device");

        Assert.Contains(device.WithCalls, w => w.Method == "WithHttpEndpoint" && w.Args[0].Contains("6080"));
        Assert.Contains(device.WithCalls, w => w.Method == "WithEndpoint" && w.Args.Any(a => a.Contains("5555")));
        // The image starts its web view only when asked to.
        Assert.Contains(device.WithCalls, w => w.Method == "WithEnvironment" && w.Args[0] == "\"WEB_VNC\"");
        Assert.Contains(device.WithCalls, w => w.Method == "WithContainerRuntimeArgs" && w.Args.Contains("\"/dev/kvm\""));
    }

    [Fact]
    public void The_script_reaches_the_generated_code_as_one_compilable_literal()
    {
        var code = new CodeGenService().GenerateProgram(Stack());

        var line = code.Split('\n').Single(l => l.Contains("WithArgs(\"-c\""));
        Assert.DoesNotContain('\r', line.TrimEnd('\r'));
        Assert.Contains(@"\n", line);              // the two characters, as a C# literal carries a newline
        Assert.Contains("sys.boot_completed", line);
        Assert.Contains("adb install", line);
    }

    [Fact]
    public void The_generated_code_waits_before_it_installs()
    {
        var code = new CodeGenService().GenerateProgram(Stack());

        Assert.Contains("installer.WaitFor(device)", code.Replace(" ", ""));
        Assert.Contains("builder.AddParameter(\"apk-url\"", code);
    }
}
