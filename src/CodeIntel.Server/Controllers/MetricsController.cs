using CodeIntel.Server.Models;
using CodeIntel.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace CodeIntel.Server.Controllers;

[ApiController]
[Route("api/metrics")]
public class MetricsController : ControllerBase
{
    private readonly IMetricsService _metrics;
    private readonly ILogger<MetricsController> _logger;

    public MetricsController(IMetricsService metrics, ILogger<MetricsController> logger)
    {
        _metrics = metrics;
        _logger = logger;
    }

    [HttpPost("compute")]
    public async Task<IActionResult> Compute([FromBody] MetricsComputeRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.WorkspaceId))
            return BadRequest(new { error = "workspaceId is required." });

        try
        {
            var result = await _metrics.ComputeAsync(req.WorkspaceId, req.FilePaths, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, new { error = "Cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metrics compute failed for {WorkspaceId}", req.WorkspaceId);
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
