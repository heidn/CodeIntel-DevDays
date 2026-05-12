using System.Diagnostics;
using System.Text;
using CodeIntel.Server.Hubs;
using CodeIntel.Server.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace CodeIntel.Server.Services;

/// <summary>
/// Agentic loop: streams the LLM, fulfills context requests, and iterates until
/// the model signals done or max iterations is reached.
/// </summary>
public class InvestigationOrchestrator : IAnalysisOrchestrator
{
    private readonly IContextBuilder _contextBuilder;
    private readonly IPromptTemplateService _promptService;
    private readonly IContextRequestHandler _contextHandler;
    private readonly ILlmService _llm;
    private readonly IAnalysisResultStore _store;
    private readonly IHubContext<AnalysisHub> _hub;
    private readonly IAnalysisCancellationRegistry _cancelRegistry;
    private readonly AnalysisOptions _analysisOptions;
    private readonly ILogger<InvestigationOrchestrator> _logger;

    public InvestigationOrchestrator(
        IContextBuilder contextBuilder,
        IPromptTemplateService promptService,
        IContextRequestHandler contextHandler,
        ILlmService llm,
        IAnalysisResultStore store,
        IHubContext<AnalysisHub> hub,
        IAnalysisCancellationRegistry cancelRegistry,
        IOptions<AnalysisOptions> analysisOptions,
        ILogger<InvestigationOrchestrator> logger)
    {
        _contextBuilder = contextBuilder;
        _promptService = promptService;
        _contextHandler = contextHandler;
        _llm = llm;
        _store = store;
        _hub = hub;
        _cancelRegistry = cancelRegistry;
        _analysisOptions = analysisOptions.Value;
        _logger = logger;
    }

    public Task<Guid> StartAsync(AnalysisRequest request, CancellationToken ct = default)
    {
        var analysisId = request.AnalysisId ?? Guid.NewGuid();
        var userCts = new CancellationTokenSource();
        _cancelRegistry.Register(analysisId, userCts);
        _ = Task.Run(() => RunAsync(analysisId, request, userCts), CancellationToken.None);
        return Task.FromResult(analysisId);
    }

    private async Task RunAsync(Guid analysisId, AnalysisRequest request, CancellationTokenSource userCts)
    {
        var group = _hub.Clients.Group(analysisId.ToString());
        var sw = Stopwatch.StartNew();
        var startedAt = DateTime.UtcNow;
        var allFindings = new List<Finding>();
        var rawOutputBuilder = new StringBuilder();
        var analyzedRelativePaths = new List<string>();
        var contextTokens = 0;

        // Overall hard ceiling + idle-token watchdog (reset on every token).
        using var overallCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(_analysisOptions.OverallTimeoutSeconds));
        using var idleCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(_analysisOptions.IdleTokenTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            userCts.Token, overallCts.Token, idleCts.Token);
        var ct = linkedCts.Token;

        try
        {
            if (!_llm.IsReady)
            {
                await group.SendAsync("AnalysisEvent", AnalysisEvents.Error(
                    "LLM is not initialized. Ensure a GGUF model is in the configured path and the server has had a moment to load it on startup."), CancellationToken.None);
                return;
            }

            await group.SendAsync("AnalysisEvent", AnalysisEvents.Status("Building context..."), ct);

            var context = await _contextBuilder.BuildAsync(
                request.WorkspaceId,
                request.SelectedFilePaths,
                _analysisOptions.MaxContextTokens,
                ct);

            analyzedRelativePaths = context.Files.Select(f => f.RelativePath).ToList();
            contextTokens = context.EstimatedTokens;

            await group.SendAsync("AnalysisEvent",
                AnalysisEvents.Started(context.EstimatedTokens, context.Files.Count), ct);

            var history = new List<ConversationTurn>();
            string prompt = _promptService.BuildAgentPrompt(request, context);

            var maxIters = Math.Max(1, _analysisOptions.MaxAgenticIterations);

            for (int iteration = 0; iteration < maxIters; iteration++)
            {
                await group.SendAsync("AnalysisEvent",
                    AnalysisEvents.IterationStarted(iteration + 1, maxIters), ct);

                await group.SendAsync("AnalysisEvent",
                    AnalysisEvents.Status(iteration == 0
                        ? $"Running {_llm.ModelName}..."
                        : $"Investigating (pass {iteration + 1}/{maxIters})..."), ct);

                var parser = new FindingStreamParser();
                var iterationOutput = new StringBuilder();

                // Reset idle watchdog at the start of each iteration.
                idleCts.CancelAfter(TimeSpan.FromSeconds(_analysisOptions.IdleTokenTimeoutSeconds));

                await foreach (var token in _llm.StreamAsync(prompt, ct))
                {
                    // Token arrived — reset idle watchdog.
                    idleCts.CancelAfter(TimeSpan.FromSeconds(_analysisOptions.IdleTokenTimeoutSeconds));

                    iterationOutput.Append(token);
                    await group.SendAsync("AnalysisEvent", AnalysisEvents.Token(token), ct);
                    foreach (var finding in parser.Append(token))
                    {
                        allFindings.Add(finding);
                        await group.SendAsync("AnalysisEvent", AnalysisEvents.Finding(finding), ct);
                    }
                    if (parser.IsDone) break;
                }

                var iterationText = iterationOutput.ToString();
                rawOutputBuilder.Append(iterationText);

                var contextRequests = parser.ContextRequests;
                _logger.LogInformation("Iteration {N}: {Findings} findings, {Requests} context requests",
                    iteration + 1, parser.Findings.Count, contextRequests.Count);

                if (parser.MalformedFindings.Count > 0)
                {
                    var firstError = parser.MalformedFindings[0].Error;
                    _logger.LogWarning(
                        "Iteration {N}: parser dropped {Count} malformed <finding> JSON block(s). First error: {Error}",
                        iteration + 1, parser.MalformedFindings.Count, firstError);
                }
                if (parser.IncompleteFindingCount > 0)
                {
                    _logger.LogWarning(
                        "Iteration {N}: parser dropped {Count} incomplete <finding> tag(s) (no closing tag in stream). Model likely truncated mid-finding.",
                        iteration + 1, parser.IncompleteFindingCount);
                }

                if (contextRequests.Count == 0 || parser.IsDone)
                    break;

                foreach (var cr in contextRequests)
                    await group.SendAsync("AnalysisEvent",
                        AnalysisEvents.ContextRequested(cr.Type.ToString(), cr.Target), ct);

                await group.SendAsync("AnalysisEvent",
                    AnalysisEvents.Status($"Fetching {contextRequests.Count} requested context item(s)..."), ct);

                var fulfillments = new List<ContextFulfillment>();
                foreach (var cr in contextRequests)
                {
                    var fulfillment = await _contextHandler.FulfillAsync(request.WorkspaceId, cr, ct);
                    fulfillments.Add(fulfillment);
                    await group.SendAsync("AnalysisEvent",
                        AnalysisEvents.ContextFulfilled(cr.Type.ToString(), cr.Target, fulfillment.Found), ct);
                }

                history.Add(new ConversationTurn("assistant", iterationText));
                prompt = _promptService.BuildContinuationPrompt(request, context, history, fulfillments);
                _logger.LogDebug("Continuation prompt length: {Len} chars", prompt.Length);
            }

            sw.Stop();

            var result = new AnalysisResult(
                Id: analysisId,
                StartedAt: startedAt,
                CompletedAt: DateTime.UtcNow,
                Mode: request.Mode,
                PresetKey: request.PresetKey,
                FreeTextPrompt: request.FreeTextPrompt,
                AnalyzedFiles: analyzedRelativePaths,
                Findings: allFindings,
                RawLlmOutput: rawOutputBuilder.ToString(),
                ContextTokens: contextTokens,
                Duration: sw.Elapsed,
                WorkspaceId: request.WorkspaceId
            );
            _store.Save(result);

            await group.SendAsync("AnalysisEvent",
                AnalysisEvents.Completed(analysisId, sw.Elapsed.TotalSeconds, allFindings.Count), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            var (reason, message) = DescribeCancellation(userCts, overallCts, idleCts);
            _logger.LogWarning("Investigation {Id} cancelled: {Reason}", analysisId, reason);

            // Save whatever we got so the user can still see partial findings and Save to repo.
            if (allFindings.Count > 0 || rawOutputBuilder.Length > 0)
            {
                var partial = new AnalysisResult(
                    Id: analysisId,
                    StartedAt: startedAt,
                    CompletedAt: DateTime.UtcNow,
                    Mode: request.Mode,
                    PresetKey: request.PresetKey,
                    FreeTextPrompt: request.FreeTextPrompt,
                    AnalyzedFiles: analyzedRelativePaths,
                    Findings: allFindings,
                    RawLlmOutput: rawOutputBuilder.ToString(),
                    ContextTokens: contextTokens,
                    Duration: sw.Elapsed,
                    WorkspaceId: request.WorkspaceId
                );
                _store.Save(partial);
            }

            await group.SendAsync("AnalysisEvent", AnalysisEvents.Cancelled(reason, message), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Investigation {Id} failed", analysisId);
            await group.SendAsync("AnalysisEvent", AnalysisEvents.Error(ex.Message), CancellationToken.None);
        }
        finally
        {
            _cancelRegistry.Remove(analysisId);
            userCts.Dispose();
        }
    }

    private (string reason, string message) DescribeCancellation(
        CancellationTokenSource userCts,
        CancellationTokenSource overallCts,
        CancellationTokenSource idleCts)
    {
        if (userCts.IsCancellationRequested)
            return ("user", "Analysis cancelled.");

        if (overallCts.IsCancellationRequested)
            return ("timeout",
                $"Analysis exceeded the time limit ({_analysisOptions.OverallTimeoutSeconds}s). " +
                $"Try a smaller scope, or raise Analysis.OverallTimeoutSeconds in appsettings.");

        if (idleCts.IsCancellationRequested)
            return ("idle",
                $"Model produced no output for {_analysisOptions.IdleTokenTimeoutSeconds}s — it appears stuck. " +
                $"Try a smaller scope or a different preset.");

        return ("unknown", "Analysis cancelled.");
    }
}
