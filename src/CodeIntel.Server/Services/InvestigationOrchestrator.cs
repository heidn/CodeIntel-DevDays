using System.Diagnostics;
using System.Text;
using CodeIntel.Server.Hubs;
using CodeIntel.Server.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace CodeIntel.Server.Services;

public interface IAnalysisOrchestrator
{
    Task<Guid> StartAsync(AnalysisRequest request, CancellationToken ct = default);
}

/// <summary>
/// Agentic loop: streams the LLM, fulfills context requests, and iterates until
/// the model signals done or max iterations is reached.
/// </summary>
public class InvestigationOrchestrator : IAnalysisOrchestrator
{
    private readonly IContextBuilder _contextBuilder;
    private readonly IPromptTemplateService _promptService;
    private readonly IContextRequestHandler _contextHandler;
    private readonly IWorkspaceService _workspaceService;
    private readonly IResultCache _resultCache;
    private readonly ISkillRouter _skillRouter;
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
        IWorkspaceService workspaceService,
        IResultCache resultCache,
        ISkillRouter skillRouter,
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
        _workspaceService = workspaceService;
        _resultCache = resultCache;
        _skillRouter = skillRouter;
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

            // F2: cache lookup — short-circuit if the same preset has already run against
            // the same file contents with the same model.
            var precomputedHash = await ContentHasher.HashFilesAsync(
                _workspaceService, request.WorkspaceId, request.SelectedFilePaths, ct);
            var cached = await _resultCache.LookupAsync(request, precomputedHash, _llm.ModelName, ct);
            if (cached is not null)
            {
                await group.SendAsync("AnalysisEvent",
                    AnalysisEvents.Status($"Cache hit — reusing result from {cached.StartedAt:HH:mm} ({cached.Findings.Count} findings)."), ct);
                var replay = cached with { Id = analysisId, StartedAt = startedAt, CompletedAt = DateTime.UtcNow };
                _store.Save(replay);
                foreach (var f in cached.Findings)
                    await group.SendAsync("AnalysisEvent", AnalysisEvents.Finding(f), ct);
                await group.SendAsync("AnalysisEvent",
                    AnalysisEvents.Completed(analysisId, 0, cached.Findings.Count), CancellationToken.None);
                return;
            }

            await group.SendAsync("AnalysisEvent",
                AnalysisEvents.Status($"Building context from {request.SelectedFilePaths.Count} file(s)..."), ct);

            var context = await _contextBuilder.BuildAsync(
                request.WorkspaceId,
                request.SelectedFilePaths,
                _analysisOptions.MaxContextTokens,
                ct);

            analyzedRelativePaths = context.Files.Select(f => f.RelativePath).ToList();
            contextTokens = context.EstimatedTokens;

            await group.SendAsync("AnalysisEvent",
                AnalysisEvents.Started(context.EstimatedTokens, context.Files.Count), ct);

            var skills = _skillRouter.RouteSkills(context);
            if (skills.Count > 0)
            {
                await group.SendAsync("AnalysisEvent",
                    AnalysisEvents.Status($"Skills active: {string.Join(", ", skills)}"), ct);
                _logger.LogInformation("Skill router fired: {Skills}", string.Join(",", skills));
            }

            var history = new List<ConversationTurn>();
            string prompt = _promptService.BuildAgentPrompt(request, context);

            var maxIters = Math.Max(1, _analysisOptions.MaxAgenticIterations);

            for (int iteration = 0; iteration < maxIters; iteration++)
            {
                await group.SendAsync("AnalysisEvent",
                    AnalysisEvents.IterationStarted(iteration + 1, maxIters), ct);

                await group.SendAsync("AnalysisEvent",
                    AnalysisEvents.Status(iteration == 0
                        ? $"Streaming first pass through {_llm.ModelName} ({_llm.BackendName}, ~{contextTokens:N0} tokens)..."
                        : $"Pass {iteration + 1}/{maxIters} — incorporating new context and continuing analysis..."), ct);

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

                var requestSummary = string.Join(", ",
                    contextRequests.Take(3).Select(c => $"{c.Type}={c.Target}"));
                if (contextRequests.Count > 3) requestSummary += $", +{contextRequests.Count - 3} more";
                await group.SendAsync("AnalysisEvent",
                    AnalysisEvents.Status($"Fetching context: {requestSummary}"), ct);

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

            // We computed this above for the cache lookup; reuse it.
            var contentHash = precomputedHash;

            // F8: collapse near-duplicate findings the 7B model often emits across iterations.
            var aggregated = FindingsAggregator.Collapse(allFindings);
            if (aggregated.Count != allFindings.Count)
                _logger.LogInformation("Findings aggregator collapsed {From} → {To}", allFindings.Count, aggregated.Count);

            var result = new AnalysisResult(
                Id: analysisId,
                StartedAt: startedAt,
                CompletedAt: DateTime.UtcNow,
                Mode: request.Mode,
                PresetKey: request.PresetKey,
                FreeTextPrompt: request.FreeTextPrompt,
                AnalyzedFiles: analyzedRelativePaths,
                Findings: aggregated,
                RawLlmOutput: rawOutputBuilder.ToString(),
                ContextTokens: contextTokens,
                Duration: sw.Elapsed,
                WorkspaceId: request.WorkspaceId,
                WorkspaceRoot: ContentHasher.WorkspaceRoot(_workspaceService, request.WorkspaceId),
                ContentHash: contentHash
            );
            _store.Save(result);

            // Record the cache entry once persistence has the new analysis on file.
            var cacheKey = ContentHasher.BuildCacheKey(request.PresetKey, _llm.ModelName, contentHash, _analysisOptions.MaxContextTokens);
            if (cacheKey is not null)
            {
                try { await _resultCache.RememberAsync(cacheKey, analysisId, CancellationToken.None); }
                catch (Exception ex) { _logger.LogDebug(ex, "Cache write failed (non-fatal)"); }
            }

            await group.SendAsync("AnalysisEvent",
                AnalysisEvents.Completed(analysisId, sw.Elapsed.TotalSeconds, aggregated.Count), CancellationToken.None);
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
