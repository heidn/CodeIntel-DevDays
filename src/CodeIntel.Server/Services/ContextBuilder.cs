using CodeIntel.Server.Models;
using Microsoft.Extensions.Options;

namespace CodeIntel.Server.Services;

public interface IContextBuilder
{
    Task<CodeContext> BuildAsync(
        string workspaceId,
        IEnumerable<string> selectedFilePaths,
        int maxTokenBudget,
        CancellationToken ct = default);
}

public class ContextBuilder : IContextBuilder
{
    private readonly IWorkspaceService _workspace;
    private readonly AnalysisOptions _analysisOptions;
    private readonly ILogger<ContextBuilder> _logger;

    public ContextBuilder(
        IWorkspaceService workspace,
        IOptions<AnalysisOptions> analysisOptions,
        ILogger<ContextBuilder> logger)
    {
        _workspace = workspace;
        _analysisOptions = analysisOptions.Value;
        _logger = logger;
    }

    public async Task<CodeContext> BuildAsync(
        string workspaceId,
        IEnumerable<string> selectedFilePaths,
        int maxTokenBudget,
        CancellationToken ct = default)
    {
        var ws = _workspace.GetWorkspace(workspaceId)
            ?? throw new InvalidOperationException($"Workspace {workspaceId} not loaded");

        var paths = selectedFilePaths.ToList();
        if (paths.Count == 0)
            throw new InvalidOperationException("No files selected for analysis");

        var rootDir = File.Exists(ws.ProjectPath) ? Path.GetDirectoryName(ws.ProjectPath)! : ws.ProjectPath;

        var files = new List<FileContext>();
        var totalTokens = 0;
        var truncated = false;

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();

            string content;
            try
            {
                content = await _workspace.ReadFileAsync(workspaceId, path, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read file {Path}", path);
                continue;
            }

            var tokens = EstimateTokens(content);

            if (totalTokens + tokens > maxTokenBudget)
            {
                var remaining = maxTokenBudget - totalTokens;
                if (remaining < 200)
                {
                    truncated = true;
                    _logger.LogInformation(
                        "Skipping {Path} - token budget exhausted ({Total}/{Budget})",
                        path, totalTokens, maxTokenBudget);
                    break;
                }
                content = TruncateToTokens(content, remaining);
                tokens = EstimateTokens(content);
                truncated = true;
            }

            var relative = Path.IsPathRooted(path)
                ? Path.GetRelativePath(rootDir, path)
                : path;

            files.Add(new FileContext(
                FilePath: path,
                RelativePath: relative,
                Content: content,
                IsExtractedSummary: false
            ));
            totalTokens += tokens;
        }

        if (truncated)
            _logger.LogInformation("Context truncated to fit budget. Files included: {Count}", files.Count);

        return new CodeContext(files, totalTokens, ws.Language);
    }

    private int EstimateTokens(string text) =>
        (int)Math.Ceiling(text.Length * _analysisOptions.TokensPerCharEstimate);

    private string TruncateToTokens(string content, int targetTokens)
    {
        var targetChars = (int)(targetTokens / _analysisOptions.TokensPerCharEstimate) - 80;
        if (targetChars <= 0 || content.Length <= targetChars) return content;
        return content[..targetChars] + "\n\n// ... [truncated for context budget] ...\n";
    }
}
