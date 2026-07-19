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

        // Top-level (global) statements only — if markers are present, restrict to that span;
        // otherwise the whole program's top-level statements (markerless real-world Program.cs).
        var statements = root.Members.OfType<GlobalStatementSyntax>()
            .Select(g => g.Statement)
            .Where(s => s.SpanStart >= from && s.Span.End <= to)
            .ToList();

        var nodes = new List<NodeModel>();
        var edges = new List<EdgeModel>();
        var raws = new List<string>();
        var varToNodeId = new Dictionary<string, string>();
        int nId = 0, eId = 0;

        // Pass 0: pre-scan every `var X = builder.AddX(...)` declaration in the span so a
        // synthesized name (below) never collides with a name the user actually declared —
        // declared vars are referenced elsewhere by that exact identifier and must not be renamed.
        var declaredVarNames = DeclaredVarNames(statements);

        // Single pass, in source order, so raw statements interleaved between declarations,
        // withCalls and edges are preserved in their original relative order.
        foreach (var st in statements)
        {
            // Node declaration, possibly a fluent chain: "var v = builder.AddX(name, args).WithY(...)...;"
            if (st is LocalDeclarationStatementSyntax lds
                && lds.Declaration.Variables.Count == 1
                && lds.Declaration.Variables[0].Initializer?.Value is InvocationExpressionSyntax initInv)
            {
                var (calls, chainRoot) = WalkChain(initInv);

                // `var builder = DistributedApplication.CreateBuilder(args);` — CodeGenService
                // always emits this exact line itself; drop it rather than duplicating as a raw.
                // Must check the chain root is actually `DistributedApplication` — otherwise some
                // unrelated `var builder = Something.CreateBuilder(...)` would be silently eaten.
                if (lds.Declaration.Variables[0].Identifier.Text == "builder"
                    && calls is [("CreateBuilder", _)]
                    && chainRoot is IdentifierNameSyntax cbRoot && cbRoot.Identifier.Text == "DistributedApplication")
                {
                    continue;
                }

                if (calls.Count > 0 && chainRoot is IdentifierNameSyntax rootId && rootId.Identifier.Text == "builder")
                {
                    var varName = lds.Declaration.Variables[0].Identifier.Text;
                    var nodeId = "n" + (++nId);
                    varToNodeId[varName] = nodeId;
                    nodes.Add(MakeNode(varName, calls[0], nodeId));

                    // Chain modifiers after the AddX root: .WithReference/.WaitFor(known var) -> edges,
                    // any other .WithY(args) -> WithCalls on this node.
                    ApplyChainModifiers(calls.Skip(1).ToList(), nodeId, nodes, edges, varToNodeId, ref eId);
                    continue;
                }

                // Chain root is not `builder` (e.g. `pg.AddDatabase(...)` — a child resource on another
                // var) or the initializer isn't an attributable chain at all: keep the whole statement.
                raws.Add(st.ToString().Trim());
                continue;
            }

            // Standalone chain: "v.WithY(...).WithReference(w)...;" on a known var, or an unassigned
            // "builder.AddX(name, args)...;" (very common in real Program.cs — no local needed if the
            // resource is never referenced again), or the trailing "builder.Build().Run/RunAsync();".
            if (st is ExpressionStatementSyntax es && es.Expression is InvocationExpressionSyntax einv)
            {
                var (calls, chainRoot) = WalkChain(einv);
                if (chainRoot is IdentifierNameSyntax builderId && builderId.Identifier.Text == "builder")
                {
                    // "builder.Build().Run();" / "...RunAsync();" — synthesized by CodeGenService; skip.
                    // Must match that exact two-call shape — a bare `.Run()`/`.RunAsync()` anywhere in
                    // the chain used to match too, silently dropping unrelated `builder.Whatever().Run()`
                    // statements.
                    if (calls is [("Build", _), (var lastMethod, _)] && lastMethod is "Run" or "RunAsync")
                    {
                        continue;
                    }

                    if (calls.Count > 0)
                    {
                        var literalName = (calls[0].Args.Arguments.FirstOrDefault()?.Expression
                            as LiteralExpressionSyntax)?.Token.ValueText;
                        var varName = UniqueVarName(SanitizeIdentifier(literalName ?? "res" + (nId + 1)), varToNodeId, declaredVarNames);
                        var nodeId = "n" + (++nId);
                        varToNodeId[varName] = nodeId;
                        nodes.Add(MakeNode(varName, calls[0], nodeId));
                        ApplyChainModifiers(calls.Skip(1).ToList(), nodeId, nodes, edges, varToNodeId, ref eId);
                        continue;
                    }
                }

                if (calls.Count > 0 && chainRoot is IdentifierNameSyntax recv
                    && varToNodeId.TryGetValue(recv.Identifier.Text, out var srcNodeId))
                {
                    ApplyChainModifiers(calls, srcNodeId, nodes, edges, varToNodeId, ref eId);
                    continue;
                }

                raws.Add(st.ToString().Trim());
                continue;
            }

            // Anything else in the parsed span is preserved verbatim.
            raws.Add(st.ToString().Trim());
        }

        // Restore positions from sidecar (keyed by node id).
        for (int i = 0; i < nodes.Count; i++)
            if (positions.TryGetValue(nodes[i].Id, out var xy) && xy.Length == 2)
                nodes[i] = nodes[i] with { X = xy[0], Y = xy[1] };

        return new StackModel(id, name, "net9.0", nodes, edges, raws, [], []);
    }

    // Turns an arbitrary resource-name literal into a valid, non-empty C# identifier: any character
    // that isn't a letter/digit/underscore becomes '_', and a leading digit gets an underscore prefix
    // (real Aspire resource names commonly contain hyphens, e.g. "redis-cache").
    private static string SanitizeIdentifier(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "res";
        var s = new string(raw.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        if (char.IsDigit(s[0])) s = "_" + s;
        return s;
    }

    // Ensures a candidate var name doesn't collide with one already assigned to another node
    // (varToNodeId doubles as the used-names set) NOR with a name the user actually declared
    // elsewhere in the span (reservedDeclaredNames) — appending a numeric suffix until it's unique.
    private static string UniqueVarName(string candidate, Dictionary<string, string> varToNodeId, ISet<string> reservedDeclaredNames)
    {
        bool Taken(string c) => varToNodeId.ContainsKey(c) || reservedDeclaredNames.Contains(c);
        if (!Taken(candidate)) return candidate;
        var i = 2;
        while (Taken(candidate + i)) i++;
        return candidate + i;
    }

    // Pass 0: collects the identifier of every `var X = builder.AddX(...)` declaration in the
    // span (mirrors the condition in the main loop's declaration branch, minus the synthesized
    // `var builder = DistributedApplication.CreateBuilder(args);` line, which isn't a resource).
    private static HashSet<string> DeclaredVarNames(List<StatementSyntax> statements)
    {
        var declared = new HashSet<string>();
        foreach (var st in statements)
        {
            if (st is LocalDeclarationStatementSyntax lds
                && lds.Declaration.Variables.Count == 1
                && lds.Declaration.Variables[0].Initializer?.Value is InvocationExpressionSyntax initInv)
            {
                var (calls, chainRoot) = WalkChain(initInv);
                var varName = lds.Declaration.Variables[0].Identifier.Text;

                if (varName == "builder" && calls is [("CreateBuilder", _)]
                    && chainRoot is IdentifierNameSyntax cbRoot && cbRoot.Identifier.Text == "DistributedApplication")
                {
                    continue;
                }

                if (calls.Count > 0 && chainRoot is IdentifierNameSyntax rootId && rootId.Identifier.Text == "builder")
                {
                    declared.Add(varName);
                }
            }
        }
        return declared;
    }

    // Builds a fresh node from the AddX root of a chain (root-to-outer's first entry), shared by
    // the declaration branch (`var v = builder.AddX(...)`) and the standalone-statement branch
    // (`builder.AddX(...);` with no local var).
    private static NodeModel MakeNode(string varName, (string Method, ArgumentListSyntax Args) root, string nodeId)
    {
        var (addMethod, addArgList) = root;
        var resourceName = (addArgList.Arguments.FirstOrDefault()?.Expression
            as LiteralExpressionSyntax)?.Token.ValueText ?? varName;
        var addArgs = addArgList.Arguments.Skip(1).Select(a => a.Expression.ToString()).ToList();
        return new NodeModel(nodeId, varName, addMethod, resourceName, [], 0, 0, addArgs);
    }

    // Walks an invocation chain from the outermost call down to its root, e.g. for
    // `builder.AddX(a).WithY(b).WithZ(c)` returns calls = [(AddX,[a]), (WithY,[b]), (WithZ,[c])]
    // (root-to-outer order) and root = the `builder` identifier. Stops as soon as the expression
    // is no longer `<invocation>.Member(...)`; whatever is left (an identifier, or something else
    // entirely) is returned as the root for the caller to judge.
    private static (List<(string Method, ArgumentListSyntax Args)> Calls, ExpressionSyntax Root) WalkChain(ExpressionSyntax expr)
    {
        var calls = new List<(string, ArgumentListSyntax)>();
        var cur = expr;
        while (cur is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma)
        {
            calls.Insert(0, (ma.Name.Identifier.Text, inv.ArgumentList));
            cur = ma.Expression;
        }
        return (calls, cur);
    }

    // Applies a sequence of chain calls (already past the AddX root) to an existing node:
    // .WithReference(id)/.WaitFor(id) where id is a known var -> edge; anything else -> WithCall.
    private static void ApplyChainModifiers(
        List<(string Method, ArgumentListSyntax Args)> calls,
        string nodeId,
        List<NodeModel> nodes,
        List<EdgeModel> edges,
        Dictionary<string, string> varToNodeId,
        ref int eId)
    {
        foreach (var (method, argList) in calls)
        {
            var firstArg = argList.Arguments.FirstOrDefault()?.Expression as IdentifierNameSyntax;
            var edgeKind = method switch
            {
                "WithReference" => "reference",
                "WaitFor" => "waitFor",
                _ => null
            };

            if (edgeKind is not null && firstArg is not null
                && varToNodeId.TryGetValue(firstArg.Identifier.Text, out var toNodeId))
            {
                edges.Add(new EdgeModel("e" + (++eId), nodeId, toNodeId, edgeKind));
            }
            else
            {
                var args = argList.Arguments.Select(a => a.Expression.ToString()).ToList();
                var idx = nodes.FindIndex(x => x.Id == nodeId);
                nodes[idx] = nodes[idx] with { WithCalls = [.. nodes[idx].WithCalls, new WithCall(method, args)] };
            }
        }
    }

    private static (int from, int to) MarkerSpan(string src)
    {
        var b = src.IndexOf(CodeGenService.Begin, StringComparison.Ordinal);
        var e = src.IndexOf(CodeGenService.End, StringComparison.Ordinal);
        return b < 0 || e < 0 ? (0, src.Length) : (b, e);
    }
}
