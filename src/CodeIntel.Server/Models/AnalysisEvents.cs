namespace CodeIntel.Server.Models;

/// <summary>
/// Events streamed to the client during an analysis run.
/// Single channel "AnalysisEvent" - client switches on Type.
/// </summary>
public record AnalysisEvent(string Type, object? Payload = null);

public static class AnalysisEvents
{
    public static AnalysisEvent Started(int contextTokens, int fileCount) =>
        new("started", new { contextTokens, fileCount });

    public static AnalysisEvent Token(string text) =>
        new("token", new { text });

    public static AnalysisEvent Finding(Finding finding) =>
        new("finding", finding);

    public static AnalysisEvent Status(string message) =>
        new("status", new { message });

    public static AnalysisEvent Completed(Guid analysisId, double durationSeconds, int findingCount) =>
        new("completed", new { analysisId, durationSeconds, findingCount });

    public static AnalysisEvent Error(string message) =>
        new("error", new { message });

    public static AnalysisEvent Cancelled(string reason, string message) =>
        new("cancelled", new { reason, message });

    public static AnalysisEvent IterationStarted(int iteration, int maxIterations) =>
        new("iterationStarted", new { iteration, maxIterations });

    public static AnalysisEvent ContextRequested(string type, string target) =>
        new("contextRequested", new { type, target });

    public static AnalysisEvent ContextFulfilled(string type, string target, bool found) =>
        new("contextFulfilled", new { type, target, found });

    public static AnalysisEvent TraceGraphReady(Guid traceId, string entryPointFqn, int nodeCount, int edgeCount, bool truncated) =>
        new("traceGraphReady", new { traceId, entryPointFqn, nodeCount, edgeCount, truncated });

    public static AnalysisEvent TraceNodeSynopsis(string nodeId, string synopsis) =>
        new("traceNodeSynopsis", new { nodeId, synopsis });
}
