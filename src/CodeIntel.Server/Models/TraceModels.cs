namespace CodeIntel.Server.Models;

public enum TraceDirection
{
    Callers,
    Callees,
    Both,
}

public enum EdgeKind
{
    Calls,
    CalledBy,
}

/// <summary>
/// Visual category for a node — controls Mermaid shape and color.
/// </summary>
public enum NodeKind
{
    Normal,
    /// <summary>EF Core / raw SQL database access (DbContext, DbSet, FromSql, SaveChanges).</summary>
    DbAccess,
    /// <summary>Outbound HTTP call (HttpClient, IHttpClientFactory, RestSharp, etc.).</summary>
    HttpCall,
}

/// <summary>
/// Identifies the function the user wants to trace from.
/// Exactly one of MethodName or (FilePath + Line) must be provided.
/// </summary>
public record TraceEntryPoint(
    string? MethodName,
    string? FilePath,
    int? Line,
    int? Character
);

public record TraceRequest(
    string WorkspaceId,
    TraceEntryPoint EntryPoint,
    TraceDirection Direction,
    int Depth,
    Guid? TraceId = null,
    string? PreferredFqn = null
);

/// <summary>
/// A single resolved entry-point candidate returned by the disambiguation endpoint.
/// </summary>
public record EntryPointCandidate(
    string Fqn,
    string DisplayName,
    string FilePath,
    int Line,
    string Signature
);

public record TraceNode(
    string Id,
    string SymbolFqn,
    string DisplayName,
    string? FilePath,
    int? Line,
    string? BodySnippet,
    string? Synopsis,
    NodeKind Kind = NodeKind.Normal
);

public record TraceEdge(string FromId, string ToId, EdgeKind Kind, bool IsBackEdge = false);

public record TraceResult(
    Guid Id,
    DateTime StartedAt,
    DateTime CompletedAt,
    string WorkspaceId,
    TraceEntryPoint EntryPoint,
    string EntryPointSymbolFqn,
    TraceDirection Direction,
    int Depth,
    List<TraceNode> Nodes,
    List<TraceEdge> Edges,
    string Mermaid,
    bool Truncated,
    TimeSpan Duration,
    string? ReportPath = null
);

/// <summary>
/// Output of the pure-graph walk, before LLM synopsis or Mermaid generation.
/// </summary>
public record TraceGraph(
    string EntryPointSymbolFqn,
    List<TraceNode> Nodes,
    List<TraceEdge> Edges,
    bool Truncated
);
