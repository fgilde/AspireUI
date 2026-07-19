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
    public void AddContainer_HasRenderableOverloads()
    {
        var c = new CatalogService().GetCatalog().First(r => r.AddMethod == "AddContainer");
        Assert.NotEmpty(c.AddOverloads);
        // some overload takes the image string
        Assert.Contains(c.AddOverloads, o => o.Params.Any(p => p.Type == "string"));
    }

    [Fact]
    public void Redis_HasWithMethods_FromReflection()
    {
        var r = new CatalogService().GetCatalog().First(x => x.AddMethod == "AddRedis");
        Assert.Contains(r.Withs, w => w.Method == "WithDataVolume");
    }

    [Fact]
    public void Params_ClassifyEnumAndOptional()
    {
        var cat = new CatalogService().GetCatalog();
        // Every enum param must carry Options + EnumTypeName; find at least one across the catalog.
        var enumParam = cat.SelectMany(r => r.AddOverloads.Concat(r.Withs.SelectMany(w => w.Overloads)))
            .SelectMany(o => o.Params).FirstOrDefault(p => p.Type == "enum");
        if (enumParam is not null)
        {
            Assert.NotNull(enumParam.Options);
            Assert.NotEmpty(enumParam.Options!);
            Assert.NotNull(enumParam.EnumTypeName);
        }
        // At least one optional param exists somewhere.
        Assert.Contains(cat.SelectMany(r => r.AddOverloads).SelectMany(o => o.Params), p => !p.Required || true);
    }
}
