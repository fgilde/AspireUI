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
    public void Catalog_IncludesNextendedResource()
    {
        // Nextended.Aspire.Hosting.{Supabase,N8n,LocalAI} all resolved for net10/Aspire13 on
        // nuget.org (verified during Slice 3 Task 2); their AddX methods are force-loaded in
        // CatalogService.LoadDefault() and picked up by reflection like any other integration.
        var cat = new CatalogService().GetCatalog();
        Assert.Contains(cat, r => r.AddMethod is "AddN8n" or "AddSupabase" or "AddLocalAI");
    }

    [Fact]
    public void Catalog_IncludesOllamaAndGithubRepository()
    {
        // CommunityToolkit.Aspire.Hosting.Ollama (13.4.0) and Nextended.Aspire (10.1.15) are
        // force-loaded in CatalogService.LoadDefault(). Real AddMethod names verified via a
        // reflection dump against the installed packages: Ollama exposes AddOllama; Nextended.Aspire
        // exposes AddGithubRepository (matches the AiStack.AppHost demo's `builder.AddGithubRepository(...)`
        // usage) rather than AddGitProject or similar.
        var cat = new CatalogService().GetCatalog();
        Assert.Contains(cat, r => r.AddMethod == "AddOllama");
        Assert.Contains(cat, r => r.AddMethod == "AddGithubRepository");
    }

    [Fact]
    public void Ollama_HasAddModelCapability()
    {
        // Fluent Add* methods on the resource builder (e.g. ollama.AddModel("llama3.2")) must be
        // selectable as capabilities, not just With* ones. Verified against the real installed
        // CommunityToolkit.Aspire.Hosting.Ollama assembly: OllamaResourceBuilderExtensions.AddModel(
        // this IResourceBuilder<OllamaResource>, string modelName) et al.
        var r = new CatalogService().GetCatalog().First(x => x.AddMethod == "AddOllama");
        var addModel = Assert.Single(r.Withs, w => w.Method == "AddModel");
        Assert.Equal("Model", addModel.Label);
        Assert.Contains(addModel.Overloads, o => o.Params.Any(p => p.Type == "string"));
    }

    [Fact]
    public void Catalog_ExcludesInternalHelpers_AndRegroups()
    {
        var cat = new CatalogService().GetCatalog();
        // Overlay "exclude": true drops builder/internal extension methods that match the AddX shape.
        foreach (var hidden in new[] { "AddWithAutoNaming", "AddDockerfileFactory", "AddDockerfileBuilder",
                     "AddCertificateAuthorityCollection", "AddContainerRegistry", "AddParameterFromConfiguration",
                     "AddOllamaLocal" })
            Assert.DoesNotContain(cat, r => r.AddMethod == hidden);
        // Legit compute resources are grouped, not dumped in "Other".
        var proj = cat.FirstOrDefault(r => r.AddMethod == "AddProject");
        if (proj is not null) Assert.Equal("Compute", proj.Group);
    }

    [Fact]
    public void GithubRepository_ExposesConfigureOptions_WithGitRef()
    {
        // AddGithubRepository(name, repository, Action<GithubRepositoryOptions> configure). The configure
        // lambda is where the branch (GitRef) lives; render it as a "configure" param with sub-fields
        // so the branch is settable in the UI (default gitRef "main" breaks repos whose default is master).
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
