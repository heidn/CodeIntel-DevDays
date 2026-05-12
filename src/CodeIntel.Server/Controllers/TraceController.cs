using CodeIntel.Server.Models;
using CodeIntel.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace CodeIntel.Server.Controllers;

[ApiController]
[Route("api/trace")]
public class TraceController : ControllerBase
{
    private readonly ITraceOrchestrator _orchestrator;
    private readonly ITraceResultStore _store;
    private readonly IReportWriter _writer;
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<TraceController> _logger;

    // Cancellation reuses POST /api/analysis/{id}/cancel — the registry is keyed by Guid
    // and accepts trace ids transparently.

    public TraceController(
        ITraceOrchestrator orchestrator,
        ITraceResultStore store,
        IReportWriter writer,
        IWorkspaceService workspace,
        ILogger<TraceController> logger)
    {
        _orchestrator = orchestrator;
        _store = store;
        _writer = writer;
        _workspace = workspace;
        _logger = logger;
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run([FromBody] TraceRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.WorkspaceId))
            return BadRequest(new { error = "workspaceId is required" });
        if (string.IsNullOrWhiteSpace(req.EntryPoint.MethodName) && string.IsNullOrWhiteSpace(req.EntryPoint.FilePath))
            return BadRequest(new { error = "entryPoint must include methodName or filePath+line" });
        if (req.Depth < 1 || req.Depth > 5)
            return BadRequest(new { error = "depth must be between 1 and 5" });

        var id = await _orchestrator.StartAsync(req, ct);
        return Ok(new { traceId = id });
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

    public record SaveTraceRequest(string? OutputPath);

    [HttpPost("{id}/save")]
    public async Task<IActionResult> Save(Guid id, [FromBody] SaveTraceRequest? body, CancellationToken ct)
    {
        var result = _store.Get(id);
        if (result == null) return NotFound(new { error = "Trace not found." });

        var workspace = _workspace.GetWorkspace(result.WorkspaceId);
        if (workspace == null)
            return BadRequest(new { error = "Workspace is no longer loaded. Reload and re-run the trace to save." });

        var writeResult = await _writer.WriteTraceAsync(result, workspace, body?.OutputPath, ct);
        if (writeResult == null)
            return StatusCode(500, new { error = "Failed to write trace report. See server logs." });

        var updated = result with { ReportPath = writeResult.AbsolutePath };
        _store.Save(updated);

        _logger.LogInformation("Saved trace report for {TraceId} to {Path}", id, writeResult.AbsolutePath);

        return Ok(new
        {
            traceId = id,
            absolutePath = writeResult.AbsolutePath,
            relativePath = writeResult.RelativePath,
            copilotReference = $"#file:{writeResult.RelativePath}"
        });
    }
}
