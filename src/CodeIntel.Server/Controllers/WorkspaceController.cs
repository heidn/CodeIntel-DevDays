using CodeIntel.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace CodeIntel.Server.Controllers;

[ApiController]
[Route("api/workspace")]
public class WorkspaceController : ControllerBase
{
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<WorkspaceController> _logger;

    public WorkspaceController(IWorkspaceService workspace, ILogger<WorkspaceController> logger)
    {
        _workspace = workspace;
        _logger = logger;
    }

    public record LoadRequest(string Path);

    [HttpPost("load")]
    public async Task<IActionResult> Load([FromBody] LoadRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Path))
            return BadRequest(new { error = "path is required" });

        try
        {
            var ws = await _workspace.LoadAsync(req.Path, ct);
            return Ok(ws);
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (DirectoryNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load project");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var ws = _workspace.GetWorkspace(id);
        if (ws == null) return NotFound();
        return Ok(ws);
    }

    private static readonly string[] ProjectFilePatterns =
        ["*.sln", "*.csproj", "tsconfig.json", "package.json", "pom.xml", "build.gradle", "build.gradle.kts"];

    [HttpGet("browse")]
    public IActionResult Browse([FromQuery] string? path)
    {
        try
        {
            // Default to user home directory
            var effectivePath = string.IsNullOrWhiteSpace(path)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : path;

            // Resolve drives list for root navigation
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();

            if (!Directory.Exists(effectivePath))
                return BadRequest(new { error = $"Path not found: {effectivePath}" });

            var dirs = Directory.EnumerateDirectories(effectivePath)
                .Where(d =>
                {
                    var name = Path.GetFileName(d);
                    return !name.StartsWith('.') && name != "node_modules" && name != "bin" && name != "obj";
                })
                .OrderBy(d => d)
                .Select(d => new { name = Path.GetFileName(d), path = d })
                .ToList();

            var projectFiles = ProjectFilePatterns
                .SelectMany(pattern => Directory.EnumerateFiles(effectivePath, pattern))
                .Distinct()
                .OrderBy(f => f)
                .Select(f => new
                {
                    name = Path.GetFileName(f),
                    path = f,
                    type = Path.GetExtension(f).TrimStart('.').ToLowerInvariant()
                })
                .ToList();

            var parent = Directory.GetParent(effectivePath)?.FullName;

            return Ok(new
            {
                currentPath = effectivePath,
                parentPath = parent,
                directories = dirs,
                projectFiles,
                drives,
            });
        }
        catch (UnauthorizedAccessException)
        {
            return BadRequest(new { error = "Access denied to that path" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Browse failed for path {Path}", path);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("{id}/file")]
    public async Task<IActionResult> GetFile(string id, [FromQuery] string path, CancellationToken ct)
    {
        try
        {
            var content = await _workspace.ReadFileAsync(id, path, ct);
            return Ok(new { path, content });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}
