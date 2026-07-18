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

    public string GenerateProgram(StackModel s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("var builder = DistributedApplication.CreateBuilder(args);");
        sb.AppendLine();
        sb.AppendLine(Begin);
        foreach (var n in s.Nodes)
            sb.AppendLine($"var {n.VarName} = builder.{n.AddMethod}(\"{n.ResourceName}\");");
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

    public string GenerateCsproj(StackModel s) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <Sdk Name="Aspire.AppHost.Sdk" Version="9.0.0" />
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>{s.TargetFramework}</TargetFramework>
            <IsAspireHost>true</IsAspireHost>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Aspire.Hosting.AppHost" Version="9.0.0" />
          </ItemGroup>
        </Project>
        """;

    public void Materialize(StackModel s, string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), GenerateProgram(s));
        File.WriteAllText(Path.Combine(dir, $"{s.Name}.csproj"), GenerateCsproj(s));
        var positions = s.Nodes.ToDictionary(n => n.Id, n => new[] { n.X, n.Y });
        File.WriteAllText(Path.Combine(dir, "aspireui.json"), JsonSerializer.Serialize(positions));
    }

    public IReadOnlyList<string> CompileErrors(string programCs)
    {
        var tree = CSharpSyntaxTree.ParseText(programCs);
        var comp = CSharpCompilation.Create("check")
            .AddSyntaxTrees(tree)
            .WithOptions(new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        // Only surface syntax errors here; full semantic check needs Aspire refs (later slice).
        return tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
    }
}
