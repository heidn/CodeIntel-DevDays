using System.Text;
using CodeIntel.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace CodeIntel.Server.Controllers;

[ApiController]
[Route("api/reports")]
public class ReportsController : ControllerBase
{
    private readonly IAnalysisResultStore _store;
    private readonly IReportGenerator _generator;
    private readonly IReportWriter _writer;
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<ReportsController> _logger;

    public ReportsController(
        IAnalysisResultStore store,
        IReportGenerator generator,
        IReportWriter writer,
        IWorkspaceService workspace,
        ILogger<ReportsController> logger)
    {
        _store = store;
        _generator = generator;
        _writer = writer;
        _workspace = workspace;
        _logger = logger;
    }

    [HttpGet("{analysisId}")]
    public IActionResult Get(Guid analysisId)
    {
        var result = _store.Get(analysisId);
        if (result == null) return NotFound();
        var md = _generator.GenerateMarkdown(result);
        return Ok(new { analysisId, markdown = md });
    }

    [HttpGet("{analysisId}/download")]
    public IActionResult Download(Guid analysisId)
    {
        var result = _store.Get(analysisId);
        if (result == null) return NotFound();
        var md = _generator.GenerateMarkdown(result);
        var bytes = Encoding.UTF8.GetBytes(md);
        var fileName = $"code-intel-{result.StartedAt:yyyyMMdd-HHmmss}.md";
        return File(bytes, "text/markdown", fileName);
    }

    public record SaveRequest(string? OutputPath);

    [HttpPost("{analysisId}/save")]
    public async Task<IActionResult> Save(Guid analysisId, [FromBody] SaveRequest? body, CancellationToken ct)
    {
        var result = _store.Get(analysisId);
        if (result == null) return NotFound(new { error = "Analysis not found." });

        if (string.IsNullOrWhiteSpace(result.WorkspaceId))
            return BadRequest(new { error = "Analysis has no associated workspace." });

        var workspace = _workspace.GetWorkspace(result.WorkspaceId);
        if (workspace == null)
            return BadRequest(new { error = "Workspace is no longer loaded. Reload the project and re-run the analysis to save." });

        var writeResult = await _writer.WriteAsync(result, workspace, body?.OutputPath, ct);
        if (writeResult == null)
            return StatusCode(500, new { error = "Failed to write report. See server logs." });

        var updated = result with { ReportPath = writeResult.AbsolutePath };
        _store.Save(updated);

        _logger.LogInformation("Saved report for {AnalysisId} to {Path}", analysisId, writeResult.AbsolutePath);

        return Ok(new
        {
            analysisId,
            absolutePath = writeResult.AbsolutePath,
            relativePath = writeResult.RelativePath,
            copilotReference = $"#file:{writeResult.RelativePath}"
        });
    }
}
