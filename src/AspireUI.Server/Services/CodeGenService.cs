using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AspireUI.Server.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AspireUI.Server.Services;

public record PackageInfo(string Id, string Version, List<string> Resources);

public class CodeGenService
{
    public const string Begin = "// >>> aspireui:begin (edit carefully — this block round-trips back into the visual graph)";
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

    // env: when set (publish only), inject a deployment-environment resource (Docker Compose /
    // Kubernetes / Azure ACA) + its package so `aspire publish` emits that target's artifacts.
    // Null for run/preview/export (output unchanged).
    public record PublishEnv(string Statement, string PackageId, string PackageVersion);

    public string GenerateProgram(StackModel s, PublishEnv? env = null)
    {
        var sb = new StringBuilder();
        var usings = BaseUsings
            .Concat(s.Nodes.Select(n => n.AddMethod).Distinct()
                .Where(_resourceUsings.ContainsKey)
                .SelectMany(m => _resourceUsings[m]))
            .Concat(s.Nodes.SelectMany(n => n.Usings ?? (IEnumerable<string>)[])) // composite/macro node usings
            .Distinct()
            .OrderBy(u => u, StringComparer.Ordinal);
        foreach (var u in usings)
            sb.AppendLine($"using {u};");
        sb.AppendLine();
        sb.AppendLine("var builder = DistributedApplication.CreateBuilder(args);");
        // Sits outside the aspireui marker block so round-trip import (which parses only inside it)
        // is unaffected.
        if (env is not null)
            sb.AppendLine(env.Statement);
        sb.AppendLine();
        sb.AppendLine(Begin);
        // Declare each resource var AFTER the resources it references in its Add args (e.g. a node
        // created to fill another node's IResourceBuilder<T> arg must appear first), regardless of
        // the order nodes were added to the canvas.
        foreach (var n in OrderByDependencies(s.Nodes.Where(n => !n.Composite).ToList()))
        {
            var args = new List<string> { $"\"{Escape(n.ResourceName)}\"" };
            args.AddRange(n.AddArgs);
            sb.AppendLine($"var {n.VarName} = builder.{n.AddMethod}({string.Join(", ", args)});");
        }
        // Composite/macro statements after all resource vars exist (they reference those varNames).
        foreach (var n in s.Nodes.Where(n => n.Composite))
            sb.AppendLine($"builder.{n.AddMethod}({string.Join(", ", n.AddArgs)});");
        foreach (var raw in s.RawStatements)
            sb.AppendLine(raw);
        foreach (var n in s.Nodes.Where(n => !n.Composite))
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

    // Topologically order node var-declarations by their Add-arg references: a node whose AddArgs
    // mention another node's varName is emitted after it. Stable (keeps canvas order for independents)
    // and cycle-safe (a reference cycle just falls back to insertion order for the nodes involved).
    private static List<NodeModel> OrderByDependencies(IReadOnlyList<NodeModel> nodes)
    {
        var byVar = nodes.Where(n => !string.IsNullOrEmpty(n.VarName))
            .GroupBy(n => n.VarName).ToDictionary(g => g.Key, g => g.First());
        var result = new List<NodeModel>();
        var done = new HashSet<string>();     // node Ids emitted
        var onStack = new HashSet<string>();  // cycle guard

        IEnumerable<NodeModel> Deps(NodeModel n) => n.AddArgs
            .SelectMany(a => byVar.Values.Where(o => o.VarName != n.VarName
                && Regex.IsMatch(a, $@"\b{Regex.Escape(o.VarName)}\b")))
            .Distinct();

        void Visit(NodeModel n)
        {
            if (done.Contains(n.Id) || !onStack.Add(n.Id)) return;
            foreach (var dep in Deps(n)) Visit(dep);
            onStack.Remove(n.Id);
            if (done.Add(n.Id)) result.Add(n);
        }

        foreach (var n in nodes) Visit(n);
        return result;
    }

    private static string Var(StackModel s, string nodeId) =>
        s.Nodes.First(n => n.Id == nodeId).VarName;

    private static string Escape(string name) =>
        name.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // A valid .NET assembly name (no spaces/commas/etc.): Aspire parses the app's ApplicationName —
    // which defaults to the assembly name — as an AssemblyName, so a stack named "Me, Myself and I"
    // would otherwise crash the AppHost at startup ("The given assembly name was invalid").
    private static string SafeAssemblyName(string name)
    {
        var cleaned = new string((name ?? "").Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray())
            .Trim('_');
        if (cleaned.Length == 0) return "AppHost";
        return char.IsDigit(cleaned[0]) ? "_" + cleaned : cleaned;
    }

    public string GenerateCsproj(StackModel s, PublishEnv? env = null)
    {
        var resourcePackageIds = new HashSet<string>(StringComparer.Ordinal) { "Aspire.Hosting.AppHost" };
        var envPkg = env is not null && resourcePackageIds.Add(env.PackageId)
            ? new[] { (Id: env.PackageId, Version: env.PackageVersion) }
            : Array.Empty<(string Id, string Version)>();
        var packages = envPkg.Concat(s.Nodes.Select(n => n.AddMethod)
            .Distinct()
            .Where(_resourcePackages.ContainsKey)
            .Select(m => _resourcePackages[m])
            .DistinctBy(p => p.Id)
            .Select(p => { resourcePackageIds.Add(p.Id); return (p.Id, Version: p.Version ?? AspireVersion); })
            .Concat(s.ExtraPackages
                .Where(p => resourcePackageIds.Add(p.Id))
                .Select(p => (p.Id, p.Version))));
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
            <AssemblyName>{SafeAssemblyName(s.Name)}</AssemblyName>
            <RootNamespace>{SafeAssemblyName(s.Name)}</RootNamespace>
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

    public void Materialize(StackModel s, string dir, PublishEnv? env = null)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), GenerateProgram(s, env));
        var safeName = string.Concat(s.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        // Renaming a stack changes the csproj filename; the workspace dir is reused per stack id, so an
        // old-name .csproj would linger and make `dotnet run` ambiguous ("multiple project files").
        // Drop any stale top-level csproj before writing the current one.
        foreach (var old in Directory.GetFiles(dir, "*.csproj"))
            try { File.Delete(old); } catch { /* in use / gone — overwritten below anyway */ }
        File.WriteAllText(Path.Combine(dir, $"{safeName}.csproj"), GenerateCsproj(s, env));
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
