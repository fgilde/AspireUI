using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;

namespace AspireUI.Server.Services;

public record CompletionItemDto(string Label, string Kind, string InsertText, string? Detail);
public record CodeDiagnostic(string Message, string Severity, int Start, int End);
public record SignatureInfo(string Label, string[] Parameters);

public class RoslynLspService
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> Refs = new(() =>
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(loc => { try { return (MetadataReference)MetadataReference.CreateFromFile(loc); } catch { return null; } })
            .Where(r => r is not null).Select(r => r!)
            .ToImmutableArray());

    private static readonly Lazy<MefHostServices> Host = new(() => MefHostServices.Create(MefHostServices.DefaultAssemblies));

    private static Document BuildDocument(string code)
    {
        var ws = new AdhocWorkspace(Host.Value);
        var proj = ws.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, "AppHost", "AppHost", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.ConsoleApplication,
                nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: Refs.Value));
        return ws.AddDocument(proj.Id, "Program.cs", SourceText.From(code));
    }

    public async Task<IReadOnlyList<CompletionItemDto>> CompleteAsync(string code, int offset)
    {
        try
        {
            var doc = BuildDocument(code);
            var svc = CompletionService.GetService(doc);
            if (svc is null) return [];
            var list = await svc.GetCompletionsAsync(doc, Math.Clamp(offset, 0, code.Length));
            if (list is null) return [];
            return list.ItemsList
                .Select(i => new CompletionItemDto(
                    i.DisplayText,
                    i.Tags.FirstOrDefault() ?? "Text",
                    i.DisplayText,
                    i.InlineDescription is { Length: > 0 } d ? d : null))
                .Take(200).ToList();
        }
        catch { return []; }
    }

    // The .NET SDK has these in NoWarn by default, so a real build never reports them: an assembly
    // version mismatch between a package and the newer Aspire assemblies loaded here would otherwise
    // show up for every resource that comes from a package built against an older Aspire.
    private static readonly HashSet<string> SdkSuppressed = ["CS1701", "CS1702"];

    public IReadOnlyList<CodeDiagnostic> Diagnostics(string code)
    {
        try
        {
            var doc = BuildDocument(code);
            var comp = doc.Project.GetCompilationAsync().GetAwaiter().GetResult();
            if (comp is null) return [];
            return comp.GetDiagnostics()
                .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                .Where(d => !SdkSuppressed.Contains(d.Id))
                .Select(d => new CodeDiagnostic(
                    d.GetMessage(),
                    d.Severity.ToString().ToLowerInvariant(),
                    d.Location.SourceSpan.Start, d.Location.SourceSpan.End))
                .Take(200).ToList();
        }
        catch { return []; }
    }

    public async Task<string?> HoverAsync(string code, int offset)
    {
        try
        {
            var doc = BuildDocument(code);
            var svc = QuickInfoService.GetService(doc);
            if (svc is null) return null;
            var info = await svc.GetQuickInfoAsync(doc, Math.Clamp(offset, 0, code.Length));
            if (info is null) return null;
            var text = string.Join("", info.Sections.SelectMany(s => s.TaggedParts).Select(p => p.Text));
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch { return null; }
    }

    public async Task<SignatureInfo?> SignatureAsync(string code, int offset)
    {
        try
        {
            if (code.Length == 0) return null;
            var doc = BuildDocument(code);
            var model = await doc.GetSemanticModelAsync();
            var root = await doc.GetSyntaxRootAsync();
            if (model is null || root is null) return null;
            var token = root.FindToken(Math.Clamp(offset == 0 ? 0 : offset - 1, 0, code.Length - 1));
            var invocation = token.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            if (invocation is null) return null;
            var symbolInfo = model.GetSymbolInfo(invocation);
            var method = (symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
            if (method is null) return null;
            return new SignatureInfo(
                method.ToDisplayString(),
                method.Parameters.Select(p => p.ToDisplayString()).ToArray());
        }
        catch { return null; }
    }
}
