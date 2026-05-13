using CodeIntel.Server.Models;
using CodeIntel.Server.Services.LanguageBackends;

namespace CodeIntel.Server.Services;

public interface ITraceWalker
{
    Task<TraceGraph?> BuildGraphAsync(
        string workspaceId,
        TraceEntryPoint entryPoint,
        TraceDirection direction,
        int depth,
        CancellationToken ct,
        string? preferredFqn = null);

    Task<List<EntryPointCandidate>> ResolveCandidatesAsync(
        string workspaceId,
        TraceEntryPoint entryPoint,
        CancellationToken ct);

    string BuildMermaid(TraceGraph graph, TraceDirection direction);
}

/// <summary>
/// Language-agnostic call-graph walker. All semantic operations
/// (resolve entry point, find callers, find callees, get body, classify node) go
/// through <see cref="ILanguageBackend"/> via <see cref="ILanguageBackendRegistry"/>.
/// The BFS, cycle detection, fan-out cap, total-node ceiling, and Mermaid
/// rendering live here and are identical across all languages.
/// </summary>
public class TraceWalker : ITraceWalker
{
    private const int MaxBranchWidth = 8;
    private const int MaxTotalNodes = 100;

    private readonly ILanguageBackendRegistry _backends;
    private readonly ILogger<TraceWalker> _logger;

    public TraceWalker(ILanguageBackendRegistry backends, ILogger<TraceWalker> logger)
    {
        _backends = backends;
        _logger = logger;
    }

    public async Task<TraceGraph?> BuildGraphAsync(
        string workspaceId,
        TraceEntryPoint entryPoint,
        TraceDirection direction,
        int depth,
        CancellationToken ct,
        string? preferredFqn = null)
    {
        var backend = _backends.GetBackendForWorkspace(workspaceId);
        if (!backend.Capabilities.SupportsTrace)
        {
            _logger.LogWarning("Trace requested for workspace {Ws} but backend {Backend} does not support trace",
                workspaceId, backend.Language);
            return null;
        }

        var candidates = await backend.ResolveEntryPointCandidatesAsync(workspaceId, entryPoint, ct);
        var entry = ResolveEntry(candidates, preferredFqn);
        if (entry is null)
        {
            _logger.LogWarning("Could not resolve entry point: {Name} / {File}:{Line}",
                entryPoint.MethodName, entryPoint.FilePath, entryPoint.Line);
            return null;
        }

        var entryFqn = entry.Fqn;

        // Phase A: BFS in handle space. `visited` is the single cycle-guard:
        // a node is marked visited the moment it is first discovered, so duplicate
        // queue entries are impossible and true cycles produce a dashed back-edge
        // in edgeTuples without re-expanding the target.
        var callersCache = new CallersCache(backend);
        var handleByFqn = new Dictionary<string, MethodHandle>(StringComparer.Ordinal) { [entryFqn] = entry };
        var edgeTuples = new List<(string from, string to, EdgeKind kind, bool isBackEdge)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { entryFqn };
        var truncated = false;

        var queue = new Queue<(MethodHandle handle, int curDepth)>();
        queue.Enqueue((entry, 0));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (cur, curDepth) = queue.Dequeue();
            var curFqn = cur.Fqn;

            if (handleByFqn.Count >= MaxTotalNodes)
            {
                truncated = true;
                continue;
            }

            if (curDepth >= depth) continue;

            if (direction is TraceDirection.Callers or TraceDirection.Both)
            {
                var callers = await callersCache.GetOrAddAsync(workspaceId, cur, ct);
                var added = 0;
                foreach (var caller in callers)
                {
                    if (added >= MaxBranchWidth) { truncated = true; break; }
                    added++;

                    var callerFqn = caller.Fqn;
                    var isBackEdge = visited.Contains(callerFqn);
                    edgeTuples.Add((callerFqn, curFqn, EdgeKind.CalledBy, isBackEdge));

                    if (visited.Add(callerFqn))
                    {
                        handleByFqn[callerFqn] = caller;
                        queue.Enqueue((caller, curDepth + 1));
                    }
                }
            }

            if (direction is TraceDirection.Callees or TraceDirection.Both)
            {
                var callees = await backend.FindCalleesOfAsync(workspaceId, cur, ct);
                var added = 0;
                foreach (var callee in callees)
                {
                    if (added >= MaxBranchWidth) { truncated = true; break; }
                    added++;

                    var calleeFqn = callee.Fqn;
                    var isBackEdge = visited.Contains(calleeFqn);
                    edgeTuples.Add((curFqn, calleeFqn, EdgeKind.Calls, isBackEdge));

                    if (visited.Add(calleeFqn))
                    {
                        handleByFqn[calleeFqn] = callee;
                        queue.Enqueue((callee, curDepth + 1));
                    }
                }
            }
        }

        // Phase B: build TraceNodes (assign stable ids, pull file/line + body).
        var nodeIdByFqn = new Dictionary<string, string>(StringComparer.Ordinal);
        var nodes = new List<TraceNode>(handleByFqn.Count);

        var orderedFqns = new List<string> { entryFqn };
        orderedFqns.AddRange(handleByFqn.Keys.Where(k => k != entryFqn).OrderBy(k => k, StringComparer.Ordinal));

        foreach (var fqn in orderedFqns)
        {
            ct.ThrowIfCancellationRequested();
            var handle = handleByFqn[fqn];
            var id = $"n{nodes.Count}";
            nodeIdByFqn[fqn] = id;

            var body = await backend.GetMethodBodyAsync(workspaceId, handle, ct);
            nodes.Add(new TraceNode(
                Id: id,
                SymbolFqn: fqn,
                DisplayName: handle.DisplayName,
                FilePath: handle.FilePath,
                Line: handle.Line,
                BodySnippet: body,
                Synopsis: null,
                Kind: backend.ClassifyNode(handle)));
        }

        // Phase C: rewrite edges with node ids, drop duplicates.
        var edges = new List<TraceEdge>();
        var seenEdge = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (fromFqn, toFqn, kind, isBackEdge) in edgeTuples)
        {
            if (!nodeIdByFqn.TryGetValue(fromFqn, out var fromId)) continue;
            if (!nodeIdByFqn.TryGetValue(toFqn, out var toId)) continue;
            var key = $"{fromId}->{toId}:{kind}";
            if (!seenEdge.Add(key)) continue;
            edges.Add(new TraceEdge(fromId, toId, kind, isBackEdge));
        }

        _logger.LogInformation("Trace graph: entry={Entry} nodes={Nodes} edges={Edges} truncated={Truncated}",
            entryFqn, nodes.Count, edges.Count, truncated);

        return new TraceGraph(entryFqn, nodes, edges, truncated);
    }

    public async Task<List<EntryPointCandidate>> ResolveCandidatesAsync(
        string workspaceId, TraceEntryPoint entryPoint, CancellationToken ct)
    {
        var backend = _backends.GetBackendForWorkspace(workspaceId);
        var handles = await backend.ResolveEntryPointCandidatesAsync(workspaceId, entryPoint, ct);

        return handles.Select(h => new EntryPointCandidate(
            Fqn: h.Fqn,
            DisplayName: h.DisplayName,
            FilePath: h.FilePath ?? "",
            Line: h.Line ?? 0,
            Signature: h.Fqn)).ToList();
    }

    private static MethodHandle? ResolveEntry(
        IReadOnlyList<MethodHandle> candidates, string? preferredFqn)
    {
        if (!string.IsNullOrWhiteSpace(preferredFqn))
        {
            var exact = candidates.FirstOrDefault(c =>
                string.Equals(c.Fqn, preferredFqn, StringComparison.Ordinal));
            if (exact is not null) return exact;
        }
        return candidates.FirstOrDefault();
    }

    /// <summary>
    /// Per-run memo for FindCallersOf. Same method can be visited multiple times
    /// during a BFS (different branches converge on it), and on large workspaces
    /// callers-lookups are the dominant cost. Cache is per-walker-call.
    /// </summary>
    private sealed class CallersCache
    {
        private readonly ILanguageBackend _backend;
        private readonly Dictionary<string, IReadOnlyList<MethodHandle>> _byFqn =
            new(StringComparer.Ordinal);

        public CallersCache(ILanguageBackend backend) => _backend = backend;

        public async ValueTask<IReadOnlyList<MethodHandle>> GetOrAddAsync(
            string workspaceId, MethodHandle method, CancellationToken ct)
        {
            if (_byFqn.TryGetValue(method.Fqn, out var cached)) return cached;
            var list = await _backend.FindCallersOfAsync(workspaceId, method, ct);
            _byFqn[method.Fqn] = list;
            return list;
        }
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

        // Back-edges (cycles) render dashed so they're visually distinct from
        // ordinary forward edges in diamond-pattern call graphs.
        foreach (var edge in graph.Edges)
        {
            var arrow = edge.IsBackEdge ? "-.->" : "-->";
            sb.AppendLine($"  {edge.FromId} {arrow} {edge.ToId}");
        }

        var entry = graph.Nodes.FirstOrDefault(n => n.SymbolFqn == graph.EntryPointSymbolFqn);
        if (entry is not null)
            sb.AppendLine($"  style {entry.Id} fill:#7c3aed,stroke:#4f46e5,color:#fff");

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
