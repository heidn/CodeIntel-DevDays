using CodeIntel.Server.Models;
using CodeIntel.Server.Services;
using Microsoft.AspNetCore.Mvc;

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

    public AnalysisController(
        IAnalysisOrchestrator orchestrator,
        IAnalysisResultStore store,
        IPromptTemplateService prompts,
        ILlmService llm,
        IAnalysisCancellationRegistry cancel)
    {
        _orchestrator = orchestrator;
        _store = store;
        _prompts = prompts;
        _llm = llm;
        _cancel = cancel;
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
    public IActionResult GetRecent([FromQuery] int count = 20) =>
        Ok(_store.Recent(count));

    [HttpPost("{id}/cancel")]
    public IActionResult Cancel(Guid id)
    {
        var ok = _cancel.Cancel(id);
        return ok
            ? Ok(new { cancelled = true })
            : NotFound(new { error = "Analysis is not currently running." });
    }
}
