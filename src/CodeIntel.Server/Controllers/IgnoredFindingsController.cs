using CodeIntel.Server.Models;
using CodeIntel.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace CodeIntel.Server.Controllers;

[ApiController]
[Route("api/ignored-findings")]
public class IgnoredFindingsController : ControllerBase
{
    private readonly IIgnoredFindingsStore _store;
    private readonly IWorkspaceService _workspace;

    public IgnoredFindingsController(IIgnoredFindingsStore store, IWorkspaceService workspace)
    {
        _store = store;
        _workspace = workspace;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string workspaceId, CancellationToken ct)
    {
        var root = ContentHasher.WorkspaceRoot(_workspace, workspaceId);
        if (root is null) return BadRequest(new { error = "Workspace not loaded." });
        var items = await _store.ListAsync(root, ct);
        return Ok(items);
    }

    public record IgnoreRequest(string WorkspaceId, Finding Finding, string? Note);

    [HttpPost]
    public async Task<IActionResult> Ignore([FromBody] IgnoreRequest req, CancellationToken ct)
    {
        var root = ContentHasher.WorkspaceRoot(_workspace, req.WorkspaceId);
        if (root is null) return BadRequest(new { error = "Workspace not loaded." });
        await _store.IgnoreAsync(root, req.Finding, req.Note, ct);
        return Ok(new { signature = _store.SignatureFor(req.Finding) });
    }

    [HttpDelete("{signature}")]
    public async Task<IActionResult> Unignore(string signature, [FromQuery] string workspaceId, CancellationToken ct)
    {
        var root = ContentHasher.WorkspaceRoot(_workspace, workspaceId);
        if (root is null) return BadRequest(new { error = "Workspace not loaded." });
        await _store.UnignoreAsync(root, signature, ct);
        return Ok(new { unignored = true });
    }
}
