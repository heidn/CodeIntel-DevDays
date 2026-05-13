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
        // `visited` is the single cycle-guard: a node is marked visited the moment it is first
        // discovered, before it is enqueued.  This means:
        //   (a) duplicate queue entries (diamond patterns) are impossible — once a node is in
        //       visited it can still receive incoming edges but will never be enqueued again.
        //   (b) true call-graph cycles (A→B→A) produce a back-edge in edgeTuples (which becomes
        //       a Mermaid cycle arrow) but do not re-expand A.
        var symbolByFqn = new Dictionary<string, IMethodSymbol>(StringComparer.Ordinal);
        var edgeTuples = new List<(string from, string to, EdgeKind kind)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;

        symbolByFqn[entryFqn] = entryMethod;
        visited.Add(entryFqn);

        var queue = new Queue<(IMethodSymbol sym, int curDepth)>();
        queue.Enqueue((entryMethod, 0));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (cur, curDepth) = queue.Dequeue();
            var curFqn = cur.ToDisplayString();

            // Already past the depth limit — record the node but don't expand it.
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
                    // Always record the edge (including back-edges from cycles).
                    edgeTuples.Add((callerFqn, curFqn, EdgeKind.CalledBy));

                    // Only enqueue if not yet visited — prevents cycles and diamond re-expansion.
                    if (visited.Add(callerFqn))
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
                    // Always record the edge (including back-edges from cycles).
                    edgeTuples.Add((curFqn, calleeFqn, EdgeKind.Calls));

                    // Only enqueue if not yet visited — prevents cycles and diamond re-expansion.
                    if (visited.Add(calleeFqn))
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
                Synopsis: null,
                Kind: InferNodeKind(sym)));
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

    /// <summary>
    /// Infers the visual category of a node from its type hierarchy and method name.
    /// Used exclusively for Mermaid rendering — does not affect graph structure.
    /// </summary>
    private static NodeKind InferNodeKind(IMethodSymbol m)
    {
        var containingType = m.ContainingType;
        if (containingType is null) return NodeKind.Normal;

        // Walk the inheritance chain to detect DbContext / DbSet<T>.
        var t = containingType;
        while (t is not null)
        {
            var name = t.Name;
            if (name is "DbContext" or "IdentityDbContext" or "DbSet") return NodeKind.DbAccess;
            t = t.BaseType;
        }

        // Interface-based: IQueryable<T>, DbSet<T> generic form.
        foreach (var iface in containingType.AllInterfaces)
        {
            if (iface.Name is "IQueryable" or "IAsyncQueryProvider") return NodeKind.DbAccess;
        }

        // Method-name heuristics for EF Core extension methods (ToListAsync, FromSqlRaw, etc.)
        if (m.Name is "SaveChanges" or "SaveChangesAsync"
                   or "FromSqlRaw" or "FromSqlInterpolated" or "FromSql"
                   or "ExecuteSqlRaw" or "ExecuteSqlInterpolated" or "ExecuteSqlRawAsync")
            return NodeKind.DbAccess;

        // HttpClient and IHttpClientFactory.
        if (containingType.Name is "HttpClient" or "HttpMessageHandler" or "IHttpClientFactory")
            return NodeKind.HttpCall;

        // Common HTTP method-name patterns on typed clients or wrappers.
        if (m.Name is "GetAsync" or "PostAsync" or "PutAsync" or "PatchAsync"
                   or "DeleteAsync" or "SendAsync" or "GetStringAsync"
                   or "GetByteArrayAsync" or "GetStreamAsync")
        {
            // Check if the containing type has HttpClient as a field/property via naming convention.
            // Fallback: flag any method with these names that isn't in source (likely a BCL call).
            if (!m.Locations.Any(l => l.IsInSource)) return NodeKind.HttpCall;
        }

        return NodeKind.Normal;
    }

    public string BuildMermaid(TraceGraph graph, TraceDirection direction)
    {
        if (graph.Nodes.Count == 0)
            return "flowchart TD\n  empty[\"(no trace data)\"]";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("flowchart TD");

        // Node declarations — shape varies by NodeKind:
        //   Normal   → ["Label"]   rectangle
        //   DbAccess → [("Label")] cylinder (database icon)
        //   HttpCall → {{"Label"}} hexagon  (external I/O)
        foreach (var node in graph.Nodes)
        {
            var safeLabel = node.DisplayName.Replace("\"", "'");
            var decl = node.Kind switch
            {
                NodeKind.DbAccess => $"[(\"{safeLabel}\")]",
                NodeKind.HttpCall => $"{{\"{safeLabel}\"}}",
                _                 => $"[\"{safeLabel}\"]",
            };
            sb.AppendLine($"  {node.Id}{decl}");
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

        // Colour-code DB and HTTP nodes so they stand out.
        var dbNodes   = graph.Nodes.Where(n => n.Kind == NodeKind.DbAccess).ToList();
        var httpNodes = graph.Nodes.Where(n => n.Kind == NodeKind.HttpCall).ToList();
        if (dbNodes.Count > 0)
        {
            sb.AppendLine("  classDef db fill:#1e3a5f,stroke:#3b82f6,color:#93c5fd");
            sb.AppendLine($"  class {string.Join(',', dbNodes.Select(n => n.Id))} db");
        }
        if (httpNodes.Count > 0)
        {
            sb.AppendLine("  classDef http fill:#1f2d1f,stroke:#22c55e,color:#86efac");
            sb.AppendLine($"  class {string.Join(',', httpNodes.Select(n => n.Id))} http");
        }

        if (graph.Truncated)
            sb.AppendLine("  classDef truncated stroke-dasharray: 5 5");

        return sb.ToString();
    }
}
