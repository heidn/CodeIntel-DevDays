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

    public ReportsController(IAnalysisResultStore store, IReportGenerator generator)
    {
        _store = store;
        _generator = generator;
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
}
