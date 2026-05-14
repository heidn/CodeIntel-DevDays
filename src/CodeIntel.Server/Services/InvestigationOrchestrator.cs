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

            await group.SendAsync("AnalysisEvent",
                AnalysisEvents.Status($"Building context from {request.SelectedFilePaths.Count} file(s)..."), ct);

            // Pre-flight: figure out whether any selected file needs chunking. Returns
            // a 1-element list (no chunking) for the common case. Done BEFORE the cache
            // lookup so the lookup key matches what the write key will be — chunked and
            // non-chunked runs of identical content keep distinct cache entries.
            var chunkPlan = await PlanChunksAsync(request, ct);

            // F2: cache lookup — short-circuit if the same preset has already run against
            // the same file contents with the same model AND the same chunking decision.
            var precomputedHash = await ContentHasher.HashFilesAsync(
                _workspaceService, request.WorkspaceId, request.SelectedFilePaths, ct);
            var cached = await _resultCache.LookupAsync(
                request, precomputedHash, _llm.ModelName, ct,
                chunkVersion: chunkPlan.IsChunked ? FileChunker.Version : null);
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
            if (chunkPlan.IsChunked)
            {
                await group.SendAsync("AnalysisEvent",
                    AnalysisEvents.Status(
                        $"{Path.GetFileName(chunkPlan.ChunkedFilePath)} is too large for one pass; " +
                        $"splitting into {chunkPlan.Chunks.Count} chunks. Expect roughly {chunkPlan.Chunks.Count}× normal duration."),
                    ct);
            }

            // Track parser anomalies across ALL chunks/iterations so we can refuse to
            // cache runs that produced incomplete/malformed findings or never reached
            // <done/>. Without this, a truncated run overwrites a healthy cached
            // result and future cache hits silently serve the broken output.
            var totalIncompleteFindings = 0;
            var totalMalformedFindings = 0;
            var lastIterationReachedDone = false;
            var chunkSummaries = new List<string>();   // one line per completed chunk, fed forward
            var firstChunkEmittedStarted = false;

            for (var chunkIdx = 0; chunkIdx < chunkPlan.Chunks.Count; chunkIdx++)
            {
                ct.ThrowIfCancellationRequested();

                CodeContext context;
                ChunkContext? chunkInfo = null;
                if (chunkPlan.IsChunked)
                {
                    var range = chunkPlan.Chunks[chunkIdx];
                    chunkInfo = new ChunkContext(
                        Index: chunkIdx + 1,
                        Total: chunkPlan.Chunks.Count,
                        FilePath: chunkPlan.ChunkedFilePath!,
                        StartLine: range.StartLine,
                        EndLine: range.EndLine,
                        TotalLines: chunkPlan.TotalLines,
                        CarryOverNotes: chunkSummaries.Count == 0 ? null : string.Join("\n", chunkSummaries));

                    await group.SendAsync("AnalysisEvent",
                        AnalysisEvents.Status(
                            $"Analysing part {chunkInfo.Index} of {chunkInfo.Total}: " +
                            $"lines {range.StartLine}–{range.EndLine} of {Path.GetFileName(chunkPlan.ChunkedFilePath)}"),
                        ct);

                    context = await _contextBuilder.BuildChunkAsync(
                        request.WorkspaceId,
                        request.SelectedFilePaths,
                        chunkPlan.ChunkedFilePath!,
                        range,
                        chunkPlan.TotalLines,
                        _analysisOptions.MaxContextTokens,
                        ct);
                }
                else
                {
                    context = await _contextBuilder.BuildAsync(
                        request.WorkspaceId,
                        request.SelectedFilePaths,
                        _analysisOptions.MaxContextTokens,
                        ct);
                }

                if (!firstChunkEmittedStarted)
                {
                    analyzedRelativePaths = context.Files.Select(f => f.RelativePath).ToList();
                    contextTokens = context.EstimatedTokens;
                    await group.SendAsync("AnalysisEvent",
                        AnalysisEvents.Started(context.EstimatedTokens, context.Files.Count), ct);
                    firstChunkEmittedStarted = true;
                }

                var skills = _skillRouter.RouteSkills(context);
                if (skills.Count > 0)
                {
                    await group.SendAsync("AnalysisEvent",
                        AnalysisEvents.Status($"Skills active: {string.Join(", ", skills)}"), ct);
                    _logger.LogInformation("Skill router fired: {Skills}", string.Join(",", skills));
                }

                var history = new List<ConversationTurn>();
                string prompt = _promptService.BuildAgentPrompt(request, context, chunkInfo);

                // Chunked runs run a single pass per chunk: the carry-over notes already
                // give the model cross-chunk awareness, and multiplying agentic iterations
                // by chunk count blows up wall-clock without proportional finding gains
                // (3 chunks × 3 iters = 9 LLM calls vs 3 originally for the same file).
                var maxIters = chunkPlan.IsChunked ? 1 : Math.Max(1, _analysisOptions.MaxAgenticIterations);
                var findingsBeforeChunk = allFindings.Count;

                for (int iteration = 0; iteration < maxIters; iteration++)
                {
                    await group.SendAsync("AnalysisEvent",
                        AnalysisEvents.IterationStarted(iteration + 1, maxIters), ct);

                    var passLabel = chunkPlan.IsChunked
                        ? $"part {chunkIdx + 1}/{chunkPlan.Chunks.Count}, pass {iteration + 1}/{maxIters}"
                        : (iteration == 0 ? "first pass" : $"pass {iteration + 1}/{maxIters}");

                    await group.SendAsync("AnalysisEvent",
                        AnalysisEvents.Status(iteration == 0 && !chunkPlan.IsChunked
                            ? $"Streaming first pass through {_llm.ModelName} ({_llm.BackendName}, ~{context.EstimatedTokens:N0} tokens)..."
                            : $"Streaming {passLabel} through {_llm.ModelName} (~{context.EstimatedTokens:N0} tokens)..."), ct);

                    var parser = new FindingStreamParser();
                    var iterationOutput = new StringBuilder();

                    idleCts.CancelAfter(TimeSpan.FromSeconds(_analysisOptions.IdleTokenTimeoutSeconds));

                    await foreach (var token in _llm.StreamAsync(prompt, ct))
                    {
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
                    _logger.LogInformation(
                        "Chunk {C}/{Tot} iter {N}: {Findings} findings, {Requests} context requests",
                        chunkIdx + 1, chunkPlan.Chunks.Count,
                        iteration + 1, parser.Findings.Count, contextRequests.Count);

                    if (parser.MalformedFindings.Count > 0)
                    {
                        _logger.LogWarning(
                            "Chunk {C}/{Tot} iter {N}: parser dropped {Count} malformed <finding> JSON block(s). First error: {Error}",
                            chunkIdx + 1, chunkPlan.Chunks.Count, iteration + 1,
                            parser.MalformedFindings.Count, parser.MalformedFindings[0].Error);
                    }
                    if (parser.IncompleteFindingCount > 0)
                    {
                        _logger.LogWarning(
                            "Chunk {C}/{Tot} iter {N}: parser dropped {Count} incomplete <finding> tag(s).",
                            chunkIdx + 1, chunkPlan.Chunks.Count, iteration + 1, parser.IncompleteFindingCount);
                    }

                    totalIncompleteFindings += parser.IncompleteFindingCount;
                    totalMalformedFindings  += parser.MalformedFindings.Count;
                    lastIterationReachedDone = parser.IsDone;

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
                    prompt = _promptService.BuildContinuationPrompt(request, context, history, fulfillments, chunkInfo);
                    _logger.LogDebug("Continuation prompt length: {Len} chars", prompt.Length);
                }

                if (chunkPlan.IsChunked && chunkInfo is not null)
                {
                    var emittedHere = allFindings.Count - findingsBeforeChunk;
                    chunkSummaries.Add(
                        $"- Lines {chunkInfo.StartLine}–{chunkInfo.EndLine} reviewed " +
                        $"({emittedHere} finding{(emittedHere == 1 ? "" : "s")} emitted).");
                }
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

            // Record the cache entry once persistence has the new analysis on file —
            // but ONLY if the run looks healthy. A run that experienced parser drops
            // or never reached <done/> may have been truncated by the response-token
            // budget mid-finding; caching it would let future identical-content runs
            // serve the broken output instantly and overwrite a previously healthy
            // cache entry.
            var runLooksHealthy = totalIncompleteFindings == 0
                                  && totalMalformedFindings == 0
                                  && lastIterationReachedDone;
            var cacheKey = ContentHasher.BuildCacheKey(
                request.PresetKey,
                _llm.ModelName,
                contentHash,
                _analysisOptions.MaxContextTokens,
                chunkPlan.IsChunked ? FileChunker.Version : null);
            if (cacheKey is not null && runLooksHealthy)
            {
                try { await _resultCache.RememberAsync(cacheKey, analysisId, CancellationToken.None); }
                catch (Exception ex) { _logger.LogDebug(ex, "Cache write failed (non-fatal)"); }
            }
            else if (cacheKey is not null)
            {
                _logger.LogWarning(
                    "Skipping result cache write for {Id}: incomplete={Incomplete} malformed={Malformed} reachedDone={Done}. " +
                    "Any prior healthy cache entry for this content is preserved.",
                    analysisId, totalIncompleteFindings, totalMalformedFindings, lastIterationReachedDone);
            }

            await group.SendAsync("AnalysisEvent",
                AnalysisEvents.Completed(
                    analysisId,
                    sw.Elapsed.TotalSeconds,
                    aggregated.Count,
                    incompleteFindings: totalIncompleteFindings,
                    malformedFindings: totalMalformedFindings,
                    reachedDone: lastIterationReachedDone),
                CancellationToken.None);
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

    /// <summary>
    /// Inputs into the chunking decision. <see cref="Chunks"/> is always non-empty; if
    /// <see cref="IsChunked"/> is false it contains a single sentinel range covering
    /// the whole file set and the caller should use the non-chunked build path.
    /// </summary>
    private record ChunkPlan(
        bool IsChunked,
        string? ChunkedFilePath,
        int TotalLines,
        IReadOnlyList<ChunkRange> Chunks);

    private static readonly ChunkPlan SingleNonChunkedPass =
        new(false, null, 0, new[] { new ChunkRange(0, 0, "") });

    private async Task<ChunkPlan> PlanChunksAsync(AnalysisRequest request, CancellationToken ct)
    {
        if (!_analysisOptions.EnableAutoChunking) return SingleNonChunkedPass;
        if (request.SelectedFilePaths.Count == 0)   return SingleNonChunkedPass;

        // Read every selected file once, find the largest oversize candidate.
        // Reserve a portion of the budget for the carry-over notes block + a small
        // companion-file allowance so chunked builds still have room for siblings.
        var perChunkBudget = Math.Max(
            500,
            _analysisOptions.MaxContextTokens - _analysisOptions.ChunkCarryOverReserveTokens);

        string? oversizedPath = null;
        string? oversizedContent = null;
        var biggestOver = 0;

        foreach (var path in request.SelectedFilePaths)
        {
            ct.ThrowIfCancellationRequested();
            string content;
            try { content = await _workspaceService.ReadFileAsync(request.WorkspaceId, path, ct); }
            catch { continue; }

            var tokens = (int)Math.Ceiling(content.Length * _analysisOptions.TokensPerCharEstimate);
            if (tokens > perChunkBudget && tokens > biggestOver)
            {
                biggestOver = tokens;
                oversizedPath = path;
                oversizedContent = content;
            }
        }

        if (oversizedPath is null || oversizedContent is null)
            return SingleNonChunkedPass;

        var chunks = FileChunker.ComputeChunks(
            oversizedContent,
            Path.GetExtension(oversizedPath),
            perChunkBudget,
            _analysisOptions.TokensPerCharEstimate,
            _analysisOptions.MaxChunksPerFile);

        if (chunks is null || chunks.Count <= 1)
            return SingleNonChunkedPass;

        var totalLines = oversizedContent.Count(c => c == '\n') + 1;
        _logger.LogInformation(
            "Chunking {Path}: {TotalTokens} tokens > budget {Budget} → {Chunks} chunks (algorithm {Ver})",
            oversizedPath, biggestOver, perChunkBudget, chunks.Count, FileChunker.Version);

        return new ChunkPlan(true, oversizedPath, totalLines, chunks);
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
