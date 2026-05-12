using CodeIntel.Server.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CodeIntel.Server.Services;

public interface ITraceWalker
{
    Task<TraceGraph?> BuildGraphAsync(
        string workspaceId,
        TraceEntryPoint entryPoint,
        TraceDirection direction,
        int depth,
        CancellationToken ct);

    string BuildMermaid(TraceGraph graph, TraceDirection direction);
}

public class TraceWalker : ITraceWalker
{
    // Per-node fan-out cap to keep the graph readable and the synopsis cost bounded.
    private const int MaxBranchWidth = 8;
    private const int MaxBodyChars = 2000;

    private readonly IWorkspaceService _workspace;
    private readonly ILogger<TraceWalker> _logger;

    public TraceWalker(IWorkspaceService workspace, ILogger<TraceWalker> logger)
    {
        _workspace = workspace;
        _logger = logger;
    }

    public async Task<TraceGraph?> BuildGraphAsync(
        string workspaceId,
        TraceEntryPoint entryPoint,
        TraceDirection direction,
        int depth,
        CancellationToken ct)
    {
        var solution = _workspace.GetRoslynSolution(workspaceId);
        if (solution == null)
        {
            _logger.LogWarning("Trace requested but no Roslyn solution for workspace {Ws}", workspaceId);
            return null;
        }

        var entrySymbol = await ResolveEntryPointAsync(solution, entryPoint, ct);
        if (entrySymbol is not IMethodSymbol entryMethod)
        {
            _logger.LogWarning("Could not resolve entry point: {Name} / {File}:{Line}",
                entryPoint.MethodName, entryPoint.FilePath, entryPoint.Line);
            return null;
        }

        entryMethod = (entryMethod.OriginalDefinition as IMethodSymbol) ?? entryMethod;
        var entryFqn = entryMethod.ToDisplayString();

        // Phase A: BFS in symbol space — collect symbols by FQN + edges as (fromFqn,toFqn,kind).
        var symbolByFqn = new Dictionary<string, IMethodSymbol>(StringComparer.Ordinal);
        var edgeTuples = new List<(string from, string to, EdgeKind kind)>();
        var visitedForExpand = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;

        symbolByFqn[entryFqn] = entryMethod;

        var queue = new Queue<(IMethodSymbol sym, int curDepth)>();
        queue.Enqueue((entryMethod, 0));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (cur, curDepth) = queue.Dequeue();
            var curFqn = cur.ToDisplayString();

            if (!visitedForExpand.Add(curFqn)) continue;
            if (curDepth >= depth) continue;

            if (direction is TraceDirection.Callers or TraceDirection.Both)
            {
                var added = 0;
                var callers = await SymbolFinder.FindCallersAsync(cur, solution, ct);
                foreach (var caller in callers)
                {
                    if (caller.CallingSymbol is not IMethodSymbol m) continue;
                    if (!IsTraceable(m)) continue;
                    if (added >= MaxBranchWidth) { truncated = true; break; }
                    added++;

                    var callerFqn = m.ToDisplayString();
                    edgeTuples.Add((callerFqn, curFqn, EdgeKind.CalledBy));

                    if (!symbolByFqn.ContainsKey(callerFqn))
                    {
                        symbolByFqn[callerFqn] = m;
                        queue.Enqueue((m, curDepth + 1));
                    }
                }
            }

            if (direction is TraceDirection.Callees or TraceDirection.Both)
            {
                var callees = await FindCalleesAsync(cur, solution, ct);
                var added = 0;
                foreach (var callee in callees)
                {
                    if (!IsTraceable(callee)) continue;
                    if (added >= MaxBranchWidth) { truncated = true; break; }
                    added++;

                    var calleeFqn = callee.ToDisplayString();
                    edgeTuples.Add((curFqn, calleeFqn, EdgeKind.Calls));

                    if (!symbolByFqn.ContainsKey(calleeFqn))
                    {
                        symbolByFqn[calleeFqn] = callee;
                        queue.Enqueue((callee, curDepth + 1));
                    }
                }
            }
        }

        // Phase B: build TraceNodes (assign stable ids, pull file/line + body snippet).
        var nodeIdByFqn = new Dictionary<string, string>(StringComparer.Ordinal);
        var nodes = new List<TraceNode>(symbolByFqn.Count);

        // Stable order: entry first, then alphabetical by FQN.
        var orderedFqns = new List<string> { entryFqn };
        orderedFqns.AddRange(symbolByFqn.Keys.Where(k => k != entryFqn).OrderBy(k => k, StringComparer.Ordinal));

        foreach (var fqn in orderedFqns)
        {
            ct.ThrowIfCancellationRequested();
            var sym = symbolByFqn[fqn];
            var id = $"n{nodes.Count}";
            nodeIdByFqn[fqn] = id;

            var loc = sym.Locations.FirstOrDefault(l => l.IsInSource);
            string? filePath = loc?.SourceTree?.FilePath;
            int? line = loc is not null
                ? loc.GetLineSpan().StartLinePosition.Line + 1
                : null;

            var body = await GetBodySnippetAsync(sym, ct);
            nodes.Add(new TraceNode(
                Id: id,
                SymbolFqn: fqn,
                DisplayName: BuildDisplayName(sym),
                FilePath: filePath,
                Line: line,
                BodySnippet: body,
                Synopsis: null));
        }

        // Phase C: rewrite edges with node ids, drop duplicates.
        var edges = new List<TraceEdge>();
        var seenEdge = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (fromFqn, toFqn, kind) in edgeTuples)
        {
            if (!nodeIdByFqn.TryGetValue(fromFqn, out var fromId)) continue;
            if (!nodeIdByFqn.TryGetValue(toFqn, out var toId)) continue;
            var key = $"{fromId}->{toId}:{kind}";
            if (!seenEdge.Add(key)) continue;
            edges.Add(new TraceEdge(fromId, toId, kind));
        }

        _logger.LogInformation("Trace graph: entry={Entry} nodes={Nodes} edges={Edges} truncated={Truncated}",
            entryFqn, nodes.Count, edges.Count, truncated);

        return new TraceGraph(entryFqn, nodes, edges, truncated);
    }

    private static async Task<ISymbol?> ResolveEntryPointAsync(
        Solution solution, TraceEntryPoint ep, CancellationToken ct)
    {
        // Preferred path: method name like "Class.Method" or "Namespace.Class.Method"
        if (!string.IsNullOrWhiteSpace(ep.MethodName))
        {
            var parts = ep.MethodName.Trim().Split('.');
            var methodPart = parts[^1];
            var typeHint = parts.Length > 1 ? parts[^2] : null;
            var nsHint = parts.Length > 2 ? string.Join('.', parts[..^2]) : null;

            var candidates = new List<IMethodSymbol>();
            foreach (var project in solution.Projects)
            {
                ct.ThrowIfCancellationRequested();
                var found = await SymbolFinder.FindDeclarationsAsync(
                    project, methodPart, ignoreCase: false,
                    filter: SymbolFilter.Member, cancellationToken: ct);
                candidates.AddRange(found.OfType<IMethodSymbol>());
            }

            if (typeHint is not null)
                candidates = candidates.Where(m => m.ContainingType?.Name == typeHint).ToList();
            if (nsHint is not null)
                candidates = candidates
                    .Where(m => m.ContainingNamespace?.ToDisplayString().EndsWith(nsHint, StringComparison.Ordinal) ?? false)
                    .ToList();

            return candidates.FirstOrDefault();
        }

        // Future path: file + line + character (for Alt+Click in the preview).
        if (!string.IsNullOrWhiteSpace(ep.FilePath) && ep.Line is int line && ep.Character is int chr)
        {
            var document = solution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => string.Equals(d.FilePath, ep.FilePath, StringComparison.OrdinalIgnoreCase));
            if (document == null) return null;

            var sourceText = await document.GetTextAsync(ct);
            var lineIndex = line - 1;
            if (lineIndex < 0 || lineIndex >= sourceText.Lines.Count) return null;
            var position = sourceText.Lines[lineIndex].Start
                + Math.Min(chr, sourceText.Lines[lineIndex].Span.Length);

            var root = await document.GetSyntaxRootAsync(ct);
            var model = await document.GetSemanticModelAsync(ct);
            if (root == null || model == null) return null;

            var token = root.FindToken(position);
            var node = token.Parent;
            if (node == null) return null;

            return model.GetSymbolInfo(node).Symbol
                ?? model.GetSymbolInfo(node).CandidateSymbols.FirstOrDefault()
                ?? model.GetDeclaredSymbol(node);
        }

        return null;
    }

    private static async Task<List<IMethodSymbol>> FindCalleesAsync(
        IMethodSymbol method, Solution solution, CancellationToken ct)
    {
        var results = new List<IMethodSymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declRef in method.DeclaringSyntaxReferences)
        {
            ct.ThrowIfCancellationRequested();
            var syntaxNode = await declRef.GetSyntaxAsync(ct);
            var doc = solution.GetDocument(syntaxNode.SyntaxTree);
            if (doc is null) continue;

            var model = await doc.GetSemanticModelAsync(ct);
            if (model is null) continue;

            foreach (var node in syntaxNode.DescendantNodes())
            {
                ct.ThrowIfCancellationRequested();
                IMethodSymbol? called = node switch
                {
                    InvocationExpressionSyntax inv =>
                        model.GetSymbolInfo(inv).Symbol as IMethodSymbol,
                    ObjectCreationExpressionSyntax oce =>
                        model.GetSymbolInfo(oce).Symbol as IMethodSymbol,
                    _ => null
                };
                if (called is null) continue;

                var orig = (called.OriginalDefinition as IMethodSymbol) ?? called;
                if (!orig.Locations.Any(l => l.IsInSource)) continue;

                var fqn = orig.ToDisplayString();
                if (seen.Add(fqn)) results.Add(orig);
            }
        }

        return results;
    }

    private static async Task<string?> GetBodySnippetAsync(IMethodSymbol method, CancellationToken ct)
    {
        foreach (var declRef in method.DeclaringSyntaxReferences)
        {
            ct.ThrowIfCancellationRequested();
            var syntax = await declRef.GetSyntaxAsync(ct);
            var text = syntax.ToFullString();
            return text.Length <= MaxBodyChars
                ? text
                : text.Substring(0, MaxBodyChars) + "\n// ... [truncated]";
        }
        return null;
    }

    private static string BuildDisplayName(IMethodSymbol m)
    {
        var typeName = m.ContainingType?.Name ?? "?";
        return m.MethodKind switch
        {
            MethodKind.Constructor => $"new {typeName}",
            MethodKind.StaticConstructor => $"{typeName}.cctor",
            _ => $"{typeName}.{m.Name}",
        };
    }

    private static bool IsTraceable(IMethodSymbol m) =>
        m.MethodKind is MethodKind.Ordinary
            or MethodKind.Constructor
            or MethodKind.LocalFunction
        && m.Locations.Any(l => l.IsInSource);

    public string BuildMermaid(TraceGraph graph, TraceDirection direction)
    {
        if (graph.Nodes.Count == 0)
            return "flowchart TD\n  empty[\"(no trace data)\"]";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("flowchart TD");

        // Node declarations — quote the label so Mermaid handles dots/spaces.
        foreach (var node in graph.Nodes)
        {
            var safeLabel = node.DisplayName.Replace("\"", "'");
            sb.AppendLine($"  {node.Id}[\"{safeLabel}\"]");
        }

        // Edge declarations — direction-aware arrows.
        // CalledBy: caller -> target (the caller invokes the target).
        // Calls:    source  -> callee  (the source invokes the callee).
        // Both render as `from --> to`; the semantics are baked into which fqn is from/to.
        foreach (var edge in graph.Edges)
            sb.AppendLine($"  {edge.FromId} --> {edge.ToId}");

        // Highlight the entry point so it's visually distinct.
        var entry = graph.Nodes.FirstOrDefault(n => n.SymbolFqn == graph.EntryPointSymbolFqn);
        if (entry is not null)
            sb.AppendLine($"  style {entry.Id} fill:#7c3aed,stroke:#4f46e5,color:#fff");

        if (graph.Truncated)
            sb.AppendLine("  classDef truncated stroke-dasharray: 5 5");

        return sb.ToString();
    }
}
