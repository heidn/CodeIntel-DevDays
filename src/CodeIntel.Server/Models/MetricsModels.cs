namespace CodeIntel.Server.Models;

/// <summary>
/// Per-method / per-routine structural metrics. Used for both C# (Roslyn-extracted)
/// and PL/SQL (ANTLR-token-stream extracted). Nulls indicate the metric doesn't
/// apply for that language (e.g., PL/SQL doesn't have async-over-sync).
/// </summary>
public record MethodMetric(
    string  Name,
    string  Container,
    int     StartLine,
    int     EndLine,
    int     LengthLines,
    int     CyclomaticComplexity,
    int     NestingDepth,
    int     ParameterCount,
    int     EmptyCatchCount,
    int     SyncOverAsyncCount,
    int?    CursorDeclarationCount,
    int?    ExceptionHandlerCount,
    int?    SwallowedWhenOthersCount,
    IReadOnlyList<string> Flags
);

public record FileMetricsResult(
    string  FilePath,
    string  RelativePath,
    Language Language,
    int     TotalLines,
    IReadOnlyList<MethodMetric> Methods,
    string? ErrorMessage = null
);

public record MetricsSummary(
    int FileCount,
    int MethodCount,
    int HighComplexityCount,
    int LongMethodCount,
    int EmptyCatchCount,
    int SyncOverAsyncCount,
    int CursorTotal,
    int SwallowedWhenOthersTotal
);

public record WorkspaceMetricsResult(
    string  WorkspaceId,
    DateTime ComputedAt,
    Language Language,
    string  ContentHash,
    MetricsSummary Summary,
    IReadOnlyList<FileMetricsResult> Files,
    // False when the workspace language has no metrics analyzer (TypeScript, Java).
    // Lets the UI distinguish "language unsupported" from "0 methods found" — both
    // otherwise present as an empty result. See MetricsService.IsMetricsSupported.
    bool Supported = true
);

public record MetricsComputeRequest(
    string WorkspaceId,
    List<string>? FilePaths
);
