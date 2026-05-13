using CodeIntel.Server.Models;
using CodeIntel.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CodeIntel.Server.Controllers;

[ApiController]
[Route("api/analysis")]
public class AnalysisController : ControllerBase
{
    private readonly IAnalysisOrchestrator _orchestrator;
    private readonly IAnalysisResultStore _store;
    private readonly IPromptTemplateService _prompts;
    private readonly ILlmService _llm;
    private readonly IAnalysisCancellationRegistry _cancel;
    private readonly IAnalysisEstimator _estimator;

    public AnalysisController(
        IAnalysisOrchestrator orchestrator,
        IAnalysisResultStore store,
        IPromptTemplateService prompts,
        ILlmService llm,
        IAnalysisCancellationRegistry cancel,
        IAnalysisEstimator estimator)
    {
        _orchestrator = orchestrator;
        _store = store;
        _prompts = prompts;
        _llm = llm;
        _cancel = cancel;
        _estimator = estimator;
    }

    public record EstimateRequestBody(string WorkspaceId, List<string> SelectedFilePaths);

    [HttpPost("estimate")]
    public async Task<IActionResult> Estimate([FromBody] EstimateRequestBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.WorkspaceId))
            return BadRequest(new { error = "workspaceId is required" });
        if (body.SelectedFilePaths.Count == 0)
            return Ok(new EstimateResult(0, 0, 0, "no files selected"));

        var estimate = await _estimator.EstimateAsync(body.WorkspaceId, body.SelectedFilePaths, ct);
        return Ok(estimate);
    }

    [HttpGet("presets")]
    public IActionResult GetPresets() => Ok(_prompts.GetPresets());

    [HttpGet("status")]
    public IActionResult GetStatus() => Ok(new
    {
        llmReady    = _llm.IsReady,
        modelName   = _llm.ModelName,
        backendName = _llm.BackendName
    });

    [HttpPost("run")]
    [EnableRateLimiting("analysis-run")]
    public async Task<IActionResult> Run([FromBody] AnalysisRequest req, CancellationToken ct)
    {
        if (req.SelectedFilePaths.Count == 0)
            return BadRequest(new { error = "Select at least one file to analyze." });
        if (req.Mode == AnalysisMode.Preset && string.IsNullOrWhiteSpace(req.PresetKey))
            return BadRequest(new { error = "PresetKey is required for preset mode." });
        if (req.Mode == AnalysisMode.FreeText && string.IsNullOrWhiteSpace(req.FreeTextPrompt))
            return BadRequest(new { error = "FreeTextPrompt is required for free-text mode." });

        var id = await _orchestrator.StartAsync(req, ct);
        return Ok(new { analysisId = id });
    }

    [HttpGet("{id}")]
    public IActionResult Get(Guid id)
    {
        var result = _store.Get(id);
        if (result == null) return NotFound();
        return Ok(result);
    }

    [HttpGet("recent")]
    public IActionResult GetRecent([FromQuery] int count = 20, [FromQuery] string? workspaceId = null) =>
        Ok(_store.Recent(count, workspaceId));

    [HttpPost("{id}/cancel")]
    public IActionResult Cancel(Guid id)
    {
        var ok = _cancel.Cancel(id);
        return ok
            ? Ok(new { cancelled = true })
            : NotFound(new { error = "Analysis is not currently running." });
    }

    /// <summary>
    /// Diffs the findings between two analyses. Useful for "what changed since last run?".
    /// </summary>
    [HttpGet("{id}/diff/{previousId}")]
    public IActionResult Diff(Guid id, Guid previousId)
    {
        var after  = _store.Get(id);
        var before = _store.Get(previousId);
        if (after is null || before is null)
            return NotFound(new { error = "One or both analyses were not found." });
        var diff = FindingsComparer.Compare(before, after);
        return Ok(new
        {
            beforeId = previousId, afterId = id,
            added = diff.Added, resolved = diff.Resolved, persisted = diff.Persisted,
            counts = new { added = diff.Added.Count, resolved = diff.Resolved.Count, persisted = diff.Persisted.Count },
        });
    }
}
