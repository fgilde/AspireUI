using AspireUI.Server.Services;

public class RoslynLspTests
{
    // Program.cs carries `using Aspire.Hosting;` so the AddRedis extension (in that namespace,
    // referenced by the server) is in scope for completion after `builder.`.
    private const string Head = "using Aspire.Hosting;\nvar builder = DistributedApplication.CreateBuilder(args);\n";

    [Fact]
    public async Task Complete_AfterBuilderDot_OffersAddRedis()
    {
        var code = Head + "builder.";
        var items = await new RoslynLspService().CompleteAsync(code, code.Length);
        Assert.Contains(items, c => c.Label.Contains("AddRedis"));
    }

    [Fact]
    public void Diagnostics_FlagsBrokenCode_AndClearsForValid()
    {
        var svc = new RoslynLspService();
        Assert.Contains(svc.Diagnostics("var x = ;"), d => d.Severity == "error");
        Assert.DoesNotContain(svc.Diagnostics(Head + "var x = 1;"), d => d.Severity == "error");
    }
}
