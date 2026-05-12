using System.Diagnostics;
using CodeIntel.Server.Hubs;
using CodeIntel.Server.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace CodeIntel.Server.Services;

public interface IAnalysisOrchestrator
{
    Task<Guid> StartAsync(AnalysisRequest request, CancellationToken ct = default);
}

public class AnalysisOrchestrator : IAnalysisOrchestrator
{
    private readonly IContextBuilder _contextBuilder;
    private readonly IPromptTemplateService _promptService;
    private readonly ILlmService _llm;
    private readonly IAnalysisResultStore _store;
    private readonly IHubContext<AnalysisHub> _hub;
    private readonly AnalysisOptions _analysisOptions;
    private readonly ILogger<AnalysisOrchestrator> _logger;

    public AnalysisOrchestrator(
        IContextBuilder contextBuilder,
        IPromptTemplateService promptService,
        ILlmService llm,
        IAnalysisResultStore store,
        IHubContext<AnalysisHub> hub,
        IOptions<AnalysisOptions> analysisOptions,
        ILogger<AnalysisOrchestrator> logger)
    {
        _contextBuilder = contextBuilder;
        _promptService = promptService;
        _llm = llm;
        _store = store;
        _hub = hub;
        _analysisOptions = analysisOptions.Value;
        _logger = logger;
    }

    public Task<Guid> StartAsync(AnalysisRequest request, CancellationToken ct = default)
    {
        var analysisId = Guid.NewGuid();
        // fire-and-forget; results stream via SignalR
        _ = Task.Run(() => RunAsync(analysisId, request, CancellationToken.None), CancellationToken.None);
        return Task.FromResult(analysisId);
    }

    private async Task RunAsync(Guid analysisId, AnalysisRequest request, CancellationToken ct)
    {
        var group = _hub.Clients.Group(analysisId.ToString());
        var sw = Stopwatch.StartNew();
        var startedAt = DateTime.UtcNow;
        var parser = new FindingStreamParser();

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

            var prompt = _promptService.BuildPrompt(request, context);
            _logger.LogDebug("Prompt length: {Length} chars", prompt.Length);

            await group.SendAsync("AnalysisEvent",
                AnalysisEvents.Status($"Running {_llm.ModelName}..."), ct);

            await foreach (var token in _llm.StreamAsync(prompt, ct))
            {
                await group.SendAsync("AnalysisEvent", AnalysisEvents.Token(token), ct);
                foreach (var finding in parser.Append(token))
                {
                    await group.SendAsync("AnalysisEvent", AnalysisEvents.Finding(finding), ct);
                }
                if (parser.IsDone) break;
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
                Findings: parser.Findings.ToList(),
                RawLlmOutput: parser.RawOutput,
                ContextTokens: context.EstimatedTokens,
                Duration: sw.Elapsed
            );
            _store.Save(result);

            await group.SendAsync("AnalysisEvent",
                AnalysisEvents.Completed(analysisId, sw.Elapsed.TotalSeconds, parser.Findings.Count), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Analysis {Id} failed", analysisId);
            await group.SendAsync("AnalysisEvent", AnalysisEvents.Error(ex.Message), ct);
        }
    }
}
