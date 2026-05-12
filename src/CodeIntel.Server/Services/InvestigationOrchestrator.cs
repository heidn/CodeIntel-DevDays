using System.Diagnostics;
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
    private const int MaxIterations = 5;

    private readonly IContextBuilder _contextBuilder;
    private readonly IPromptTemplateService _promptService;
    private readonly IContextRequestHandler _contextHandler;
    private readonly ILlmService _llm;
    private readonly IAnalysisResultStore _store;
    private readonly IHubContext<AnalysisHub> _hub;
    private readonly AnalysisOptions _analysisOptions;
    private readonly ILogger<InvestigationOrchestrator> _logger;

    public InvestigationOrchestrator(
        IContextBuilder contextBuilder,
        IPromptTemplateService promptService,
        IContextRequestHandler contextHandler,
        ILlmService llm,
        IAnalysisResultStore store,
        IHubContext<AnalysisHub> hub,
        IOptions<AnalysisOptions> analysisOptions,
        ILogger<InvestigationOrchestrator> logger)
    {
        _contextBuilder = contextBuilder;
        _promptService = promptService;
        _contextHandler = contextHandler;
        _llm = llm;
        _store = store;
        _hub = hub;
        _analysisOptions = analysisOptions.Value;
        _logger = logger;
    }

    public Task<Guid> StartAsync(AnalysisRequest request, CancellationToken ct = default)
    {
        var analysisId = request.AnalysisId ?? Guid.NewGuid();
        _ = Task.Run(() => RunAsync(analysisId, request, CancellationToken.None), CancellationToken.None);
        return Task.FromResult(analysisId);
    }

    private async Task RunAsync(Guid analysisId, AnalysisRequest request, CancellationToken ct)
    {
        var group = _hub.Clients.Group(analysisId.ToString());
        var sw = Stopwatch.StartNew();
        var startedAt = DateTime.UtcNow;
        var allFindings = new List<Finding>();
        var rawOutputBuilder = new System.Text.StringBuilder();

        try
        {
            if (!_llm.IsReady)
            {
                await group.SendAsync("AnalysisEvent", AnalysisEvents.Error(
                    "LLM is not initialized. Ensure a GGUF model is in the configured path and the server has had a moment to load it on startup."), ct);
                return;
            }

            await group.SendAsync("AnalysisEvent", AnalysisEvents.Status("Building context..."), ct);

            var context = await _contextBuilder.BuildAsync(
                request.WorkspaceId,
                request.SelectedFilePaths,
                _analysisOptions.MaxContextTokens,
                ct);

            await group.SendAsync("AnalysisEvent",
                AnalysisEvents.Started(context.EstimatedTokens, context.Files.Count), ct);

            var history = new List<ConversationTurn>();
            string prompt = _promptService.BuildAgentPrompt(request, context);

            for (int iteration = 0; iteration < MaxIterations; iteration++)
            {
                await group.SendAsync("AnalysisEvent",
                    AnalysisEvents.IterationStarted(iteration + 1, MaxIterations), ct);

                await group.SendAsync("AnalysisEvent",
                    AnalysisEvents.Status(iteration == 0
                        ? $"Running {_llm.ModelName}..."
                        : $"Investigating (pass {iteration + 1}/{MaxIterations})..."), ct);

                var parser = new FindingStreamParser();
                var iterationOutput = new System.Text.StringBuilder();

                await foreach (var token in _llm.StreamAsync(prompt, ct))
                {
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

                // no context requests (or done) — we're finished
                if (contextRequests.Count == 0 || parser.IsDone)
                    break;

                // emit context request notifications
                foreach (var cr in contextRequests)
                    await group.SendAsync("AnalysisEvent",
                        AnalysisEvents.ContextRequested(cr.Type.ToString(), cr.Target), ct);

                await group.SendAsync("AnalysisEvent",
                    AnalysisEvents.Status($"Fetching {contextRequests.Count} requested context item(s)..."), ct);

                // fulfill all requests
                var fulfillments = new List<ContextFulfillment>();
                foreach (var cr in contextRequests)
                {
                    var fulfillment = await _contextHandler.FulfillAsync(request.WorkspaceId, cr, ct);
                    fulfillments.Add(fulfillment);
                    await group.SendAsync("AnalysisEvent",
                        AnalysisEvents.ContextFulfilled(cr.Type.ToString(), cr.Target, fulfillment.Found), ct);
                }

                // build next prompt (append assistant turn + new user message with context)
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
                AnalyzedFiles: context.Files.Select(f => f.RelativePath).ToList(),
                Findings: allFindings,
                RawLlmOutput: rawOutputBuilder.ToString(),
                ContextTokens: context.EstimatedTokens,
                Duration: sw.Elapsed
            );
            _store.Save(result);

            await group.SendAsync("AnalysisEvent",
                AnalysisEvents.Completed(analysisId, sw.Elapsed.TotalSeconds, allFindings.Count), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Investigation {Id} failed", analysisId);
            await group.SendAsync("AnalysisEvent", AnalysisEvents.Error(ex.Message), ct);
        }
    }
}
