using CodeIntel.Server.Data;
using CodeIntel.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace CodeIntel.Server.Controllers;

/// <summary>
/// OpenShift / Kubernetes-friendly probes.
///   GET /healthz   → liveness: process is alive (always 200 unless we're crashing)
///   GET /readyz    → readiness: LLM is loaded AND SQLite responds (gate traffic on this)
/// Designed to live outside the /api prefix so probes don't get scraped along with normal traffic.
/// </summary>
[ApiController]
public class HealthController : ControllerBase
{
    private readonly ILlmService _llm;
    private readonly CodeIntelDb _db;

    public HealthController(ILlmService llm, CodeIntelDb db)
    {
        _llm = llm;
        _db = db;
    }

    [HttpGet("/healthz")]
    public IActionResult Liveness() => Ok(new { status = "ok" });

    [HttpGet("/readyz")]
    public async Task<IActionResult> Readiness(CancellationToken ct)
    {
        var llmReady = _llm.IsReady;
        var dbReady = false;
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            dbReady = true;
        }
        catch { /* dbReady stays false */ }

        var status = llmReady && dbReady ? "ready" : "not-ready";
        var code = llmReady && dbReady ? 200 : 503;
        return StatusCode(code, new
        {
            status,
            llm = new { ready = llmReady, model = _llm.ModelName, backend = _llm.BackendName },
            db = new { ready = dbReady },
        });
    }
}
