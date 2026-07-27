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
    internal static readonly string AspireVersion =
        CatalogService.PackageVersions().TryGetValue("Aspire.Hosting.AppHost", out var v) ? v : "13.4.6";

    private readonly IReadOnlyDictionary<string, (string Id, string? Version)> _resourcePackages;

    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _resourceUsings;

    private static readonly string[] BaseUsings = { "Aspire.Hosting", "Aspire.Hosting.ApplicationModel" };

    public CodeGenService(
        IReadOnlyDictionary<string, (string Id, string? Version)>? resourcePackages = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? resourceUsings = null)
    {
        _resourcePackages = resourcePackages ?? CatalogService.ResourcePackages();
        _resourceUsings = resourceUsings ?? CatalogService.ResourceUsings();
    }

    // Deployment-environment resource (Docker Compose/Kubernetes/Azure ACA) for publish; null for run/preview/export.
    public record PublishEnv(string Statement, string PackageId, string PackageVersion);

    public string GenerateProgram(StackModel s, PublishEnv? env = null)
    {
        s = s with
        {
            Nodes = (s.Nodes ?? []).Select(n => n with
            {
                AddArgs = n.AddArgs ?? [],
                WithCalls = (n.WithCalls ?? []).Select(w => w with { Args = w.Args ?? [] }).ToList(),
            }).ToList(),
            Edges = s.Edges ?? [],
            RawStatements = s.RawStatements ?? [],
            ExtraFiles = s.ExtraFiles ?? [],
            ExtraPackages = s.ExtraPackages ?? [],
        };

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
        if (env is not null)
            sb.AppendLine(env.Statement);
        sb.AppendLine();
        sb.AppendLine(Begin);
        foreach (var n in OrderByDependencies(s.Nodes.Where(n => !n.Composite).ToList()))
        {
            var args = new List<string> { $"\"{Escape(n.ResourceName)}\"" };
            args.AddRange(n.AddArgs);
            sb.AppendLine($"var {n.VarName} = builder.{n.AddMethod}({string.Join(", ", args)});");
        }
        foreach (var n in s.Nodes.Where(n => n.Composite))
            sb.AppendLine($"builder.{n.AddMethod}({string.Join(", ", n.AddArgs)});");
        foreach (var raw in s.RawStatements)
            sb.AppendLine(raw);
        foreach (var n in s.Nodes.Where(n => !n.Composite))
            foreach (var w in n.WithCalls)
                sb.AppendLine($"{n.VarName}.{w.Method}({string.Join(", ", w.Args)});");
        foreach (var e in s.Edges)
        {
            if (e.Kind == "env") continue;
            var method = e.Kind == "waitFor" ? "WaitFor" : "WithReference";
            sb.AppendLine($"{Var(s, e.FromNodeId)}.{method}({Var(s, e.ToNodeId)});");
        }
        sb.AppendLine(End);
        sb.AppendLine();
        sb.AppendLine("builder.Build().Run();");
        return sb.ToString();
    }

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
            <EnableDefaultItems>false</EnableDefaultItems>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="Program.cs" />
            <PackageReference Include="Aspire.Hosting.AppHost" Version="{AspireVersion}" />
        {refs}
          </ItemGroup>
        </Project>
        """;
    }

    // Packages endpoint: AppHost first, then one entry per distinct overlay-mapped package.
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
        if (s.RunAsIs)
        {
            MaterializeRaw(s, dir);
            return;
        }
        File.WriteAllText(Path.Combine(dir, "Program.cs"), GenerateProgram(s, env));
        var safeName = string.Concat(s.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (!s.FromGit && !s.HasSource)
            foreach (var old in Directory.GetFiles(dir, "*.csproj"))
                try { File.Delete(old); } catch { }
        File.WriteAllText(Path.Combine(dir, $"{safeName}.csproj"), GenerateCsproj(s, env));
        var positions = s.Nodes.ToDictionary(n => n.Id, n => new[] { n.X, n.Y });
        File.WriteAllText(Path.Combine(dir, "aspireui.json"), JsonSerializer.Serialize(positions));

        var root = Path.GetFullPath(dir);
        var reservedRootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Program.cs", "aspireui.json", $"{safeName}.csproj"
        };
        foreach (var f in s.ExtraFiles)
        {
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

    private static void MaterializeRaw(StackModel s, string dir)
    {
        var root = Path.GetFullPath(dir);
        foreach (var f in s.ExtraFiles)
        {
            var fullPath = Path.GetFullPath(Path.Combine(root, f.Name));
            if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, f.Content);
        }
    }

    public IReadOnlyList<string> CompileErrors(string programCs)
    {
        var tree = CSharpSyntaxTree.ParseText(programCs);
        return tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
    }
}
