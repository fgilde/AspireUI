using System.Reflection;
using AspireUI.Server.Services;

public class CatalogTests
{
    [Fact]
    public void Reflection_FindsAddRedis()
    {
        // Force-load the assembly that defines AddRedis.
        var asm = typeof(Aspire.Hosting.RedisBuilderExtensions).Assembly;
        var catalog = new CatalogService(asm).GetCatalog();
        Assert.Contains(catalog, r => r.AddMethod == "AddRedis");
    }

    [Fact]
    public void Container_HasImageParam_AndTypedWith()
    {
        // AddContainer lives in the core Aspire.Hosting assembly, force-loaded by LoadDefault().
        var catalog = new CatalogService().GetCatalog();
        var container = catalog.First(r => r.AddMethod == "AddContainer");
        Assert.Contains(container.AddParams, p => p.Name == "image" && p.Type == "string" && p.Required);
        var httpWith = container.Withs.First(w => w.Method == "WithHttpEndpoint");
        Assert.Contains(httpWith.Params, p => p.Name == "port" && p.Type == "int");
    }
}
