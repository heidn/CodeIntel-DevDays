using System.Diagnostics;
using System.Text;
using CodeIntel.Server.Hubs;
using CodeIntel.Server.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace CodeIntel.Server.Services;

public interface ITraceOrchestrator
{
    Task<Guid> StartAsync(TraceRequest request, CancellationToken ct = default);
}

/// <summary>
/// Drives the call-trail trace pipeline: walk the graph (Roslyn), build Mermaid (programmatic),
/// synopsize each node (one LLM call per node, sequential), save to store. Emits incremental
/// SignalR events: traceGraphReady → traceNodeSynopsis (one per node) → completed.
/// </summary>
public class TraceOrchestrator : ITraceOrchestrator
{
    private readonly ITraceWalker _walker;
    private readonly ITraceResultStore _store;
    private readonly ILlmService _llm;
    private readonly IHubContext<AnalysisHub> _hub;
    private readonly IAnalysisCancellationRegistry _cancelRegistry;
    private readonly AnalysisOptions _options;
    private readonly ILogger<TraceOrchestrator> _logger;

    public TraceOrchestrator(
        ITraceWalker walker,
        ITraceResultStore store,
        ILlmService llm,
        IHubContext<AnalysisHub> hub,
        IAnalysisCancellationRegistry cancelRegistry,
        IOptions<AnalysisOptions> options,
        ILogger<TraceOrchestrator> logger)
    {
        _walker = walker;
        _store = store;
        _llm = llm;
        _hub = hub;
        _cancelRegistry = cancelRegistry;
        _options = options.Value;
        _logger = logger;
    }

    public Task<Guid> StartAsync(TraceRequest request, CancellationToken ct = default)
    {
        var traceId = request.TraceId ?? Guid.NewGuid();
        var userCts = new CancellationTokenSource();
        _cancelRegistry.Register(traceId, userCts);
        _ = Task.Run(() => RunAsync(traceId, request, userCts), CancellationToken.None);
        return Task.FromResult(traceId);
    }

    private async Task RunAsync(Guid traceId, TraceRequest request, CancellationTokenSource userCts)
    {
        var group = _hub.Clients.Group(traceId.ToString());
        var sw = Stopwatch.StartNew();
        var startedAt = DateTime.UtcNow;

        using var overallCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(_options.OverallTimeoutSeconds));
        using var idleCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(_options.IdleTokenTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            userCts.Token, overallCts.Token, idleCts.Token);
        var ct = linkedCts.Token;

        // Partial-save state — populated as we go so cancellation still yields a useful result.
        TraceGraph? graph = null;
        List<TraceNode> nodes = new();
        string mermaid = "";

        try
        {
            if (!_llm.IsReady)
            {
                await group.SendAsync("AnalysisEvent", AnalysisEvents.Error(
                    "LLM is not initialized. Synopsis pass requires the model to be loaded."), CancellationToken.None);
                return;
            }

            await group.SendAsync("AnalysisEvent",
                AnalysisEvents.Status("Resolving entry point and walking call graph..."), ct);

            graph = await _walker.BuildGraphAsync(
                request.WorkspaceId, request.EntryPoint, request.Direction, request.Depth, ct);

            if (graph is null)
            {
                await group.SendAsync("AnalysisEvent",
                    AnalysisEvents.Error("Could not resolve entry point. Check the method name and that the workspace is loaded."),
                    CancellationToken.None);
                return;
            }

            mermaid = _walker.BuildMermaid(graph, request.Direction);
            nodes = graph.Nodes.ToList();

            await group.SendAsync("AnalysisEvent",
                AnalysisEvents.TraceGraphReady(traceId, graph.EntryPointSymbolFqn, nodes.Count, graph.Edges.Count, graph.Truncated),
                ct);

            // Sequential synopsis pass — one LLM call per node.
            for (int i = 0; i < nodes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var node = nodes[i];

                if (string.IsNullOrWhiteSpace(node.BodySnippet))
                {
                    nodes[i] = node with { Synopsis = "(no source body available)" };
                    await group.SendAsync("AnalysisEvent",
                        AnalysisEvents.TraceNodeSynopsis(node.Id, nodes[i].Synopsis!), ct);
                    continue;
                }

                await group.SendAsync("AnalysisEvent",
                    AnalysisEvents.Status($"Synopsizing {i + 1}/{nodes.Count}: {node.DisplayName}..."), ct);

                idleCts.CancelAfter(TimeSpan.FromSeconds(_options.IdleTokenTimeoutSeconds));

                var prompt = BuildSynopsisPrompt(node);
                var buf = new StringBuilder();
                await foreach (var token in _llm.StreamAsync(prompt, ct))
                {
                    idleCts.CancelAfter(TimeSpan.FromSeconds(_options.IdleTokenTimeoutSeconds));
                    buf.Append(token);
                }

                var synopsis = CleanSynopsis(buf.ToString());
                nodes[i] = node with { Synopsis = synopsis };

                await group.SendAsync("AnalysisEvent",
                    AnalysisEvents.TraceNodeSynopsis(node.Id, synopsis), ct);
            }

            sw.Stop();
            var result = new TraceResult(
                Id: traceId,
                StartedAt: startedAt,
                CompletedAt: DateTime.UtcNow,
                WorkspaceId: request.WorkspaceId,
                EntryPoint: request.EntryPoint,
                EntryPointSymbolFqn: graph.EntryPointSymbolFqn,
                Direction: request.Direction,
                Depth: request.Depth,
                Nodes: nodes,
                Edges: graph.Edges,
                Mermaid: mermaid,
                Truncated: graph.Truncated,
                Duration: sw.Elapsed
            );
            _store.Save(result);

            await group.SendAsync("AnalysisEvent",
                AnalysisEvents.Completed(traceId, sw.Elapsed.TotalSeconds, nodes.Count),
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            var (reason, message) = DescribeCancellation(userCts, overallCts, idleCts);
            _logger.LogWarning("Trace {Id} cancelled: {Reason}", traceId, reason);

            // Save whatever we got — the graph + Mermaid + any synopses that completed.
            if (graph is not null)
            {
                var partial = new TraceResult(
                    Id: traceId,
                    StartedAt: startedAt,
                    CompletedAt: DateTime.UtcNow,
                    WorkspaceId: request.WorkspaceId,
                    EntryPoint: request.EntryPoint,
                    EntryPointSymbolFqn: graph.EntryPointSymbolFqn,
                    Direction: request.Direction,
                    Depth: request.Depth,
                    Nodes: nodes,
                    Edges: graph.Edges,
                    Mermaid: mermaid,
                    Truncated: graph.Truncated,
                    Duration: sw.Elapsed
                );
                _store.Save(partial);
            }

            await group.SendAsync("AnalysisEvent",
                AnalysisEvents.Cancelled(reason, message), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trace {Id} failed", traceId);
            await group.SendAsync("AnalysisEvent", AnalysisEvents.Error(ex.Message), CancellationToken.None);
        }
        finally
        {
            _cancelRegistry.Remove(traceId);
            userCts.Dispose();
        }
    }

    private (string reason, string message) DescribeCancellation(
        CancellationTokenSource userCts,
        CancellationTokenSource overallCts,
        CancellationTokenSource idleCts)
    {
        if (userCts.IsCancellationRequested)
            return ("user", "Trace cancelled.");
        if (overallCts.IsCancellationRequested)
            return ("timeout",
                $"Trace exceeded the time limit ({_options.OverallTimeoutSeconds}s). " +
                $"Try a smaller depth or fewer nodes.");
        if (idleCts.IsCancellationRequested)
            return ("idle",
                $"Model produced no output for {_options.IdleTokenTimeoutSeconds}s — likely stuck on a synopsis.");
        return ("unknown", "Trace cancelled.");
    }

    private static string BuildSynopsisPrompt(TraceNode node)
    {
        const string system = """
            You are a senior C# code reviewer. Read one method body and describe what it does
            in 1-2 sentences. Be specific. Name the operations, not generic words like "processes"
            or "handles". If the method touches a database, file, network, or external service,
            name what. No hedging — no "potential", "could", or "might". 2 sentences maximum.
            """;

        var sb = new StringBuilder();
        sb.Append("<|im_start|>system\n");
        sb.Append(system);
        sb.Append("\n<|im_end|>\n");
        sb.Append("<|im_start|>user\n");
        sb.Append($"Method: {node.DisplayName}\n");
        if (!string.IsNullOrEmpty(node.FilePath))
            sb.Append($"File: {Path.GetFileName(node.FilePath)}\n");
        sb.Append("\n```csharp\n");
        sb.Append(node.BodySnippet);
        sb.Append("\n```\n\nDescribe in 1-2 sentences what this method does.\n");
        sb.Append("<|im_end|>\n");
        sb.Append("<|im_start|>assistant\n");
        return sb.ToString();
    }

    private static string CleanSynopsis(string raw)
    {
        var text = raw.Trim();
        // Trim trailing ChatML if the model emitted it.
        var imEnd = text.IndexOf("<|im_end|>", StringComparison.Ordinal);
        if (imEnd >= 0) text = text.Substring(0, imEnd).TrimEnd();
        return text;
    }
}
