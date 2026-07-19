using System.Text;
using System.Text.Json;
using AspireUI.Server.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AspireUI.Server.Services;

public record PackageInfo(string Id, string Version, List<string> Resources);

public class CodeGenService
{
    public const string Begin = "// >>> aspireui:begin (nicht von Hand editieren)";
    public const string End = "// <<< aspireui:end";
    // internal (not private): BundleImporter reuses this as the fallback version for
    // ProjectReference->package resolution, so it doesn't drift from the csproj-generation version.
    internal const string AspireVersion = "13.4.6";

    // AddMethod -> extra NuGet package a generated stack needs beyond Aspire.Hosting.AppHost.
    // Single source of truth is the catalog overlay's "package"/"packageVersion" fields
    // (see CatalogService.ResourcePackages) so this never drifts from what the catalog reports.
    private readonly IReadOnlyDictionary<string, (string Id, string? Version)> _resourcePackages;

    // AddMethod -> extra `using` namespaces beyond the base set. Same overlay-driven source
    // (see CatalogService.ResourceUsings) so generated Program.cs actually compiles against
    // the real Add/With extension methods and enum args (e.g. LocalAI's Known* enums).
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _resourceUsings;

    private static readonly string[] BaseUsings = { "Aspire.Hosting", "Aspire.Hosting.ApplicationModel" };

    public CodeGenService(
        IReadOnlyDictionary<string, (string Id, string? Version)>? resourcePackages = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? resourceUsings = null)
    {
        _resourcePackages = resourcePackages ?? CatalogService.ResourcePackages();
        _resourceUsings = resourceUsings ?? CatalogService.ResourceUsings();
    }

    public string GenerateProgram(StackModel s)
    {
        var sb = new StringBuilder();
        var usings = BaseUsings
            .Concat(s.Nodes.Select(n => n.AddMethod).Distinct()
                .Where(_resourceUsings.ContainsKey)
                .SelectMany(m => _resourceUsings[m]))
            .Distinct()
            .OrderBy(u => u, StringComparer.Ordinal);
        foreach (var u in usings)
            sb.AppendLine($"using {u};");
        sb.AppendLine();
        sb.AppendLine("var builder = DistributedApplication.CreateBuilder(args);");
        sb.AppendLine();
        sb.AppendLine(Begin);
        foreach (var n in s.Nodes)
        {
            var args = new List<string> { $"\"{Escape(n.ResourceName)}\"" };
            args.AddRange(n.AddArgs);
            sb.AppendLine($"var {n.VarName} = builder.{n.AddMethod}({string.Join(", ", args)});");
        }
        foreach (var raw in s.RawStatements)
            sb.AppendLine(raw);
        foreach (var n in s.Nodes)
            foreach (var w in n.WithCalls)
                sb.AppendLine($"{n.VarName}.{w.Method}({string.Join(", ", w.Args)});");
        foreach (var e in s.Edges)
        {
            var method = e.Kind == "waitFor" ? "WaitFor" : "WithReference";
            sb.AppendLine($"{Var(s, e.FromNodeId)}.{method}({Var(s, e.ToNodeId)});");
        }
        sb.AppendLine(End);
        sb.AppendLine();
        sb.AppendLine("builder.Build().Run();");
        return sb.ToString();
    }

    private static string Var(StackModel s, string nodeId) =>
        s.Nodes.First(n => n.Id == nodeId).VarName;

    private static string Escape(string name) =>
        name.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public string GenerateCsproj(StackModel s)
    {
        var resourcePackageIds = new HashSet<string>(StringComparer.Ordinal) { "Aspire.Hosting.AppHost" };
        var packages = s.Nodes.Select(n => n.AddMethod)
            .Distinct()
            .Where(_resourcePackages.ContainsKey)
            .Select(m => _resourcePackages[m])
            .DistinctBy(p => p.Id)
            .Select(p => { resourcePackageIds.Add(p.Id); return (p.Id, Version: p.Version ?? AspireVersion); })
            .Concat(s.ExtraPackages
                .Where(p => resourcePackageIds.Add(p.Id))
                .Select(p => (p.Id, p.Version)));
        var refs = string.Join("\n", packages.Select(p =>
            $"""    <PackageReference Include="{p.Id}" Version="{p.Version}" />"""));
        return $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <Sdk Name="Aspire.AppHost.Sdk" Version="{AspireVersion}" />
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>{s.TargetFramework}</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <IsAspireHost>true</IsAspireHost>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Aspire.Hosting.AppHost" Version="{AspireVersion}" />
        {refs}
          </ItemGroup>
        </Project>
        """;
    }

    // Packages endpoint data: AppHost always first (no resources), then one entry per distinct
    // overlay-mapped package used by the stack's nodes, grouping the resourceNames that use it.
    public IReadOnlyList<PackageInfo> GetPackages(StackModel s)
    {
        var result = new List<PackageInfo> { new("Aspire.Hosting.AppHost", AspireVersion, new()) };
        result.AddRange(s.Nodes
            .Where(n => _resourcePackages.ContainsKey(n.AddMethod))
            .GroupBy(n => _resourcePackages[n.AddMethod])
            .Select(g => new PackageInfo(g.Key.Id, g.Key.Version ?? AspireVersion, g.Select(n => n.ResourceName).ToList())));
        return result;
    }

    public void Materialize(StackModel s, string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), GenerateProgram(s));
        var safeName = string.Concat(s.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        File.WriteAllText(Path.Combine(dir, $"{safeName}.csproj"), GenerateCsproj(s));
        var positions = s.Nodes.ToDictionary(n => n.Id, n => new[] { n.X, n.Y });
        File.WriteAllText(Path.Combine(dir, "aspireui.json"), JsonSerializer.Serialize(positions));

        var root = Path.GetFullPath(dir);
        // Reserved root-level filenames: an ExtraFile with one of these names would otherwise
        // clobber the generated source-of-truth file written above.
        var reservedRootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Program.cs", "aspireui.json", $"{safeName}.csproj"
        };
        foreach (var f in s.ExtraFiles)
        {
            // Guard against ".." path traversal: resolve and reject anything landing outside dir.
            var fullPath = Path.GetFullPath(Path.Combine(root, f.Name));
            if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            if (string.Equals(Path.GetDirectoryName(fullPath), root, StringComparison.OrdinalIgnoreCase)
                && reservedRootNames.Contains(Path.GetFileName(fullPath)))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, f.Content);
        }
    }

    public IReadOnlyList<string> CompileErrors(string programCs)
    {
        var tree = CSharpSyntaxTree.ParseText(programCs);
        // Only surface syntax errors here; full semantic check needs Aspire refs (later slice).
        return tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
    }
}
