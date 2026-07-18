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
        var varToNodeId = new Dictionary<string, string>();
        int nId = 0, eId = 0;

        // Pass 1: declarations "var X = builder.AddM("name");"
        foreach (var st in statements.OfType<LocalDeclarationStatementSyntax>())
        {
            var decl = st.Declaration.Variables[0];
            var varName = decl.Identifier.Text;
            if (decl.Initializer?.Value is not InvocationExpressionSyntax inv) continue;
            if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;      // builder.AddM
            var addMethod = ma.Name.Identifier.Text;
            var resourceName = (inv.ArgumentList.Arguments.FirstOrDefault()?.Expression
                as LiteralExpressionSyntax)?.Token.ValueText ?? varName;
            var addArgs = inv.ArgumentList.Arguments.Skip(1).Select(a => a.Expression.ToString()).ToList();
            var nodeId = "n" + (++nId);
            varToNodeId[varName] = nodeId;
            nodes.Add(new NodeModel(nodeId, varName, addMethod, resourceName, [], 0, 0, addArgs));
        }

        // Pass 2: modifications "X.WithY(...);" and "X.WithReference(Y);"
        foreach (var st in statements.OfType<ExpressionStatementSyntax>())
        {
            if (st.Expression is not InvocationExpressionSyntax inv) continue;
            if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
            if (ma.Expression is not IdentifierNameSyntax target) continue;
            if (!varToNodeId.TryGetValue(target.Identifier.Text, out var srcNodeId)) continue;
            var method = ma.Name.Identifier.Text;

            if (method == "WithReference"
                && inv.ArgumentList.Arguments.FirstOrDefault()?.Expression is IdentifierNameSyntax refId
                && varToNodeId.TryGetValue(refId.Identifier.Text, out var toNodeId))
            {
                edges.Add(new EdgeModel("e" + (++eId), srcNodeId, toNodeId, "reference"));
            }
            else
            {
                var args = inv.ArgumentList.Arguments.Select(a => a.Expression.ToString()).ToList();
                var idx = nodes.FindIndex(x => x.Id == srcNodeId);
                nodes[idx] = nodes[idx] with { WithCalls = [.. nodes[idx].WithCalls, new WithCall(method, args)] };
            }
        }

        // Restore positions from sidecar (keyed by node id).
        for (int i = 0; i < nodes.Count; i++)
            if (positions.TryGetValue(nodes[i].Id, out var xy) && xy.Length == 2)
                nodes[i] = nodes[i] with { X = xy[0], Y = xy[1] };

        return new StackModel(id, name, "net9.0", nodes, edges);
    }

    private static (int from, int to) MarkerSpan(string src)
    {
        var b = src.IndexOf(CodeGenService.Begin, StringComparison.Ordinal);
        var e = src.IndexOf(CodeGenService.End, StringComparison.Ordinal);
        return b < 0 || e < 0 ? (0, src.Length) : (b, e);
    }
}
