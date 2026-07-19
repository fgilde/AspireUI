using System.Text.Json;
using AspireUI.Server.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AspireUI.Server.Services;

public class ImportService
{
    public StackModel Import(string id, string name, string programCs, string sidecarJson)
    {
        var positions = string.IsNullOrWhiteSpace(sidecarJson)
            ? new Dictionary<string, double[]>()
            : JsonSerializer.Deserialize<Dictionary<string, double[]>>(sidecarJson)!;

        var root = CSharpSyntaxTree.ParseText(programCs).GetCompilationUnitRoot();
        var (from, to) = MarkerSpan(programCs);

        var statements = root.DescendantNodes()
            .OfType<StatementSyntax>()
            .Where(s => s.SpanStart >= from && s.Span.End <= to)
            .ToList();

        var nodes = new List<NodeModel>();
        var edges = new List<EdgeModel>();
        var raws = new List<string>();
        var varToNodeId = new Dictionary<string, string>();
        int nId = 0, eId = 0;

        // Single pass, in source order, so raw statements interleaved between declarations,
        // withCalls and edges are preserved in their original relative order.
        foreach (var st in statements)
        {
            // Node declaration: "var X = builder.AddM("name", ...);"
            if (st is LocalDeclarationStatementSyntax lds
                && lds.Declaration.Variables.Count == 1
                && lds.Declaration.Variables[0].Initializer?.Value is InvocationExpressionSyntax inv
                && inv.Expression is MemberAccessExpressionSyntax ma
                && ma.Expression is IdentifierNameSyntax recv
                && recv.Identifier.Text == "builder")
            {
                var varName = lds.Declaration.Variables[0].Identifier.Text;
                var addMethod = ma.Name.Identifier.Text;
                var resourceName = (inv.ArgumentList.Arguments.FirstOrDefault()?.Expression
                    as LiteralExpressionSyntax)?.Token.ValueText ?? varName;
                var addArgs = inv.ArgumentList.Arguments.Skip(1).Select(a => a.Expression.ToString()).ToList();
                var nodeId = "n" + (++nId);
                varToNodeId[varName] = nodeId;
                nodes.Add(new NodeModel(nodeId, varName, addMethod, resourceName, [], 0, 0, addArgs));
                continue;
            }

            // Modification: "X.WithY(...);", "X.WithReference(Y);", "X.WaitFor(Y);"
            if (st is ExpressionStatementSyntax es
                && es.Expression is InvocationExpressionSyntax einv
                && einv.Expression is MemberAccessExpressionSyntax ema
                && ema.Expression is IdentifierNameSyntax target
                && varToNodeId.TryGetValue(target.Identifier.Text, out var srcNodeId))
            {
                var method = ema.Name.Identifier.Text;
                var firstArg = einv.ArgumentList.Arguments.FirstOrDefault()?.Expression as IdentifierNameSyntax;
                var edgeKind = method switch
                {
                    "WithReference" => "reference",
                    "WaitFor" => "waitFor",
                    _ => null
                };

                if (edgeKind is not null && firstArg is not null
                    && varToNodeId.TryGetValue(firstArg.Identifier.Text, out var toNodeId))
                {
                    edges.Add(new EdgeModel("e" + (++eId), srcNodeId, toNodeId, edgeKind));
                }
                else
                {
                    var args = einv.ArgumentList.Arguments.Select(a => a.Expression.ToString()).ToList();
                    var idx = nodes.FindIndex(x => x.Id == srcNodeId);
                    nodes[idx] = nodes[idx] with { WithCalls = [.. nodes[idx].WithCalls, new WithCall(method, args)] };
                }
                continue;
            }

            // Anything else in the marker block is preserved verbatim.
            raws.Add(st.ToString().Trim());
        }

        // Restore positions from sidecar (keyed by node id).
        for (int i = 0; i < nodes.Count; i++)
            if (positions.TryGetValue(nodes[i].Id, out var xy) && xy.Length == 2)
                nodes[i] = nodes[i] with { X = xy[0], Y = xy[1] };

        return new StackModel(id, name, "net9.0", nodes, edges, raws);
    }

    private static (int from, int to) MarkerSpan(string src)
    {
        var b = src.IndexOf(CodeGenService.Begin, StringComparison.Ordinal);
        var e = src.IndexOf(CodeGenService.End, StringComparison.Ordinal);
        return b < 0 || e < 0 ? (0, src.Length) : (b, e);
    }
}
