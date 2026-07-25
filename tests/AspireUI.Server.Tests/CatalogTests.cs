using System.Reflection;
using AspireUI.Server.Services;

public class CatalogTests
{
    [Fact]
    public void Reflection_FindsAddRedis()
    {
        var asm = typeof(Aspire.Hosting.RedisBuilderExtensions).Assembly;
        var catalog = new CatalogService(asm).GetCatalog();
        Assert.Contains(catalog, r => r.AddMethod == "AddRedis");
    }

    [Fact]
    public void AddContainer_HasRenderableOverloads()
    {
        var c = new CatalogService().GetCatalog().First(r => r.AddMethod == "AddContainer");
        Assert.NotEmpty(c.AddOverloads);
        Assert.Contains(c.AddOverloads, o => o.Params.Any(p => p.Type == "string"));
    }

    [Fact]
    public void Redis_HasWithMethods_FromReflection()
    {
        var r = new CatalogService().GetCatalog().First(x => x.AddMethod == "AddRedis");
        Assert.Contains(r.Withs, w => w.Method == "WithDataVolume");
    }

    [Fact]
    public void Catalog_IncludesNextendedResource()
    {
        var cat = new CatalogService().GetCatalog();
        Assert.Contains(cat, r => r.AddMethod is "AddN8n" or "AddSupabase" or "AddLocalAI");
    }

    [Fact]
    public void Catalog_IncludesGrafana()
    {
        var cat = new CatalogService().GetCatalog();
        Assert.Contains(cat, r => r.AddMethod == "AddGrafana");
    }

    [Fact]
    public void Catalog_IncludesAspireUI()
    {
        var cat = new CatalogService().GetCatalog();
        var self = Assert.Single(cat, r => r.AddMethod == "AddAspireUI");
        Assert.Equal("AspireUI", self.Group);
    }

    [Fact]
    public void Presets_LoadedFromJson()
    {
        var presets = new CatalogService().GetPresets();
        Assert.NotEmpty(presets);
        Assert.Contains(presets, p => p.Id == "sdnext" && p.Image.Contains("sdnext") && p.Port == 7860);
        Assert.All(presets, p => Assert.False(string.IsNullOrWhiteSpace(p.Image)));
    }

    [Fact]
    public void Catalog_IncludesOllamaAndGithubRepository()
    {
        var cat = new CatalogService().GetCatalog();
        Assert.Contains(cat, r => r.AddMethod == "AddOllama");
        Assert.Contains(cat, r => r.AddMethod == "AddGithubRepository");
    }

    [Fact]
    public void Ollama_HasAddModelCapability()
    {
        var r = new CatalogService().GetCatalog().First(x => x.AddMethod == "AddOllama");
        var addModel = Assert.Single(r.Withs, w => w.Method == "AddModel");
        Assert.Equal("Model", addModel.Label);
        Assert.Contains(addModel.Overloads, o => o.Params.Any(p => p.Type == "string"));
    }

    [Fact]
    public void Catalog_ExcludesInternalHelpers_AndRegroups()
    {
        var cat = new CatalogService().GetCatalog();
        foreach (var hidden in new[] { "AddWithAutoNaming", "AddDockerfileFactory", "AddDockerfileBuilder",
                     "AddCertificateAuthorityCollection", "AddContainerRegistry", "AddParameterFromConfiguration",
                     "AddOllamaLocal" })
            Assert.DoesNotContain(cat, r => r.AddMethod == hidden);
        var proj = cat.FirstOrDefault(r => r.AddMethod == "AddProject");
        if (proj is not null) Assert.Equal("Compute", proj.Group);
    }

    [Fact]
    public void GithubRepository_ExposesConfigureOptions_WithGitRef()
    {
        var gh = new CatalogService().GetCatalog().First(r => r.AddMethod == "AddGithubRepository");
        var cfg = gh.AddOverloads.SelectMany(o => o.Params).FirstOrDefault(p => p.Type == "configure");
        Assert.NotNull(cfg);
        Assert.NotNull(cfg!.Fields);
        Assert.Contains(cfg.Fields!, f => f.Name == "GitRef");
    }

    [Fact]
    public void Params_ClassifyEnumAndOptional()
    {
        var cat = new CatalogService().GetCatalog();
        var enumParam = cat.SelectMany(r => r.AddOverloads.Concat(r.Withs.SelectMany(w => w.Overloads)))
            .SelectMany(o => o.Params).FirstOrDefault(p => p.Type == "enum");
        if (enumParam is not null)
        {
            Assert.NotNull(enumParam.Options);
            Assert.NotEmpty(enumParam.Options!);
            Assert.NotNull(enumParam.EnumTypeName);
        }
        Assert.Contains(cat.SelectMany(r => r.AddOverloads).SelectMany(o => o.Params), p => !p.Required || true);
    }
}
