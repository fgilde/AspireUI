using System.Text;
using System.Text.Json;
using AspireUI.Server.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AspireUI.Server.Services;

public class CodeGenService
{
    public const string Begin = "// >>> aspireui:begin (nicht von Hand editieren)";
    public const string End = "// <<< aspireui:end";
    private const string AspireVersion = "13.4.6";

    // AddMethod -> extra NuGet package a generated stack needs beyond Aspire.Hosting.AppHost.
    // CodeGen doesn't have the catalog, so this is a small standalone map.
    private static readonly Dictionary<string, string> ResourcePackages = new()
    {
        ["AddRedis"] = "Aspire.Hosting.Redis",
        ["AddPostgres"] = "Aspire.Hosting.PostgreSQL",
    };

    public string GenerateProgram(StackModel s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using Aspire.Hosting;");
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
        foreach (var n in s.Nodes)
            foreach (var w in n.WithCalls)
                sb.AppendLine($"{n.VarName}.{w.Method}({string.Join(", ", w.Args)});");
        foreach (var e in s.Edges.Where(e => e.Kind == "reference"))
            sb.AppendLine($"{Var(s, e.FromNodeId)}.WithReference({Var(s, e.ToNodeId)});");
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
        var packages = s.Nodes.Select(n => n.AddMethod)
            .Distinct()
            .Where(ResourcePackages.ContainsKey)
            .Select(m => ResourcePackages[m])
            .Distinct();
        var refs = string.Join("\n", packages.Select(p =>
            $"""    <PackageReference Include="{p}" Version="{AspireVersion}" />"""));
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

    public void Materialize(StackModel s, string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), GenerateProgram(s));
        var safeName = string.Concat(s.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        File.WriteAllText(Path.Combine(dir, $"{safeName}.csproj"), GenerateCsproj(s));
        var positions = s.Nodes.ToDictionary(n => n.Id, n => new[] { n.X, n.Y });
        File.WriteAllText(Path.Combine(dir, "aspireui.json"), JsonSerializer.Serialize(positions));
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
