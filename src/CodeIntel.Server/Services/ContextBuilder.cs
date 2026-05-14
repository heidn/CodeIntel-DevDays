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

    /// <summary>
    /// Builds a context where <paramref name="chunkedFilePath"/> contributes only the
    /// content in <paramref name="chunk"/> (lines &amp; text already pre-sliced by the
    /// caller). Other selected files are included normally, subject to the same token
    /// budget. PL/SQL dependency resolution is skipped on chunked builds to keep the
    /// per-chunk budget predictable.
    /// </summary>
    Task<CodeContext> BuildChunkAsync(
        string workspaceId,
        IEnumerable<string> selectedFilePaths,
        string chunkedFilePath,
        ChunkRange chunk,
        int chunkTotalLines,
        int maxTokenBudget,
        CancellationToken ct = default);
}

public class ContextBuilder : IContextBuilder
{
    private readonly IWorkspaceService _workspace;
    private readonly IPlSqlObjectParser _plSqlParser;
    private readonly IPlSqlRepoResolver _plSqlResolver;
    private readonly AnalysisOptions _analysisOptions;
    private readonly ILogger<ContextBuilder> _logger;

    public ContextBuilder(
        IWorkspaceService workspace,
        IPlSqlObjectParser plSqlParser,
        IPlSqlRepoResolver plSqlResolver,
        IOptions<AnalysisOptions> analysisOptions,
        ILogger<ContextBuilder> logger)
    {
        _workspace = workspace;
        _plSqlParser = plSqlParser;
        _plSqlResolver = plSqlResolver;
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

        var rootDir = ws.RootFolder
            ?? (File.Exists(ws.ProjectPath) ? Path.GetDirectoryName(ws.ProjectPath)! : ws.ProjectPath);

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

        // PL/SQL dependency resolution — if any seed file is SQL, parse it for object refs
        // and append their definitions from the same workspace as resolved-dependency files,
        // subject to the same token budget.
        var sqlSeeds = files.Where(f => PlSqlFileExtensions.Matches(f.RelativePath)).ToList();
        if (sqlSeeds.Count > 0 && !truncated)
        {
            var seedPaths = new HashSet<string>(
                files.Select(f => f.FilePath), StringComparer.OrdinalIgnoreCase);
            var resolvedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var depsAdded = 0;
            var depsDropped = 0;

            foreach (var seed in sqlSeeds)
            {
                ct.ThrowIfCancellationRequested();

                var refs = _plSqlParser.Parse(seed.Content);
                _logger.LogInformation(
                    "Parsed PL/SQL refs in {Path}: {Tables} tables, {Routines} routines, {Packages} packages",
                    seed.RelativePath, refs.Tables.Count, refs.Routines.Count, refs.Packages.Count);

                foreach (var objRef in refs.All())
                {
                    if (totalTokens >= maxTokenBudget) { depsDropped++; continue; }
                    if (!resolvedNames.Add(objRef.Name)) continue;

                    PlSqlResolution? resolution;
                    try
                    {
                        resolution = await _plSqlResolver.ResolveAsync(workspaceId, objRef.Name, objRef.Kind, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to resolve PL/SQL object '{Name}'", objRef.Name);
                        continue;
                    }

                    if (resolution == null) continue;
                    if (seedPaths.Contains(resolution.AbsolutePath)) continue; // skip self-references

                    var depContent = resolution.Content;
                    var depTokens = EstimateTokens(depContent);

                    if (totalTokens + depTokens > maxTokenBudget)
                    {
                        var remaining = maxTokenBudget - totalTokens;
                        if (remaining < 200)
                        {
                            depsDropped++;
                            continue;
                        }
                        depContent = TruncateToTokens(depContent, remaining);
                        depTokens = EstimateTokens(depContent);
                        truncated = true;
                    }

                    files.Add(new FileContext(
                        FilePath: resolution.AbsolutePath,
                        RelativePath: resolution.RelativePath,
                        Content: depContent,
                        IsExtractedSummary: false,
                        IsResolvedDependency: true
                    ));
                    seedPaths.Add(resolution.AbsolutePath);
                    totalTokens += depTokens;
                    depsAdded++;
                }
            }

            if (depsAdded > 0 || depsDropped > 0)
                _logger.LogInformation(
                    "PL/SQL deps: {Added} appended ({Dropped} dropped for budget). Total context tokens: {Tokens}",
                    depsAdded, depsDropped, totalTokens);
        }

        if (truncated)
            _logger.LogInformation("Context truncated to fit budget. Files included: {Count}", files.Count);

        return new CodeContext(files, totalTokens, ws.Language);
    }

    public async Task<CodeContext> BuildChunkAsync(
        string workspaceId,
        IEnumerable<string> selectedFilePaths,
        string chunkedFilePath,
        ChunkRange chunk,
        int chunkTotalLines,
        int maxTokenBudget,
        CancellationToken ct = default)
    {
        var ws = _workspace.GetWorkspace(workspaceId)
            ?? throw new InvalidOperationException($"Workspace {workspaceId} not loaded");

        var paths = selectedFilePaths.ToList();
        if (paths.Count == 0)
            throw new InvalidOperationException("No files selected for analysis");

        var rootDir = ws.RootFolder
            ?? (File.Exists(ws.ProjectPath) ? Path.GetDirectoryName(ws.ProjectPath)! : ws.ProjectPath);

        var files = new List<FileContext>();
        var totalTokens = 0;

        // Emit the chunked file FIRST so it always fits — other companion files
        // give up budget rather than the primary subject of the chunk.
        var chunkRelative = Path.IsPathRooted(chunkedFilePath)
            ? Path.GetRelativePath(rootDir, chunkedFilePath)
            : chunkedFilePath;
        var chunkTokens = EstimateTokens(chunk.Content);
        files.Add(new FileContext(
            FilePath: chunkedFilePath,
            RelativePath: chunkRelative,
            Content: chunk.Content,
            IsExtractedSummary: false,
            IsResolvedDependency: false,
            ChunkStartLine: chunk.StartLine,
            ChunkEndLine: chunk.EndLine,
            ChunkTotalLines: chunkTotalLines));
        totalTokens += chunkTokens;

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(path, chunkedFilePath, StringComparison.OrdinalIgnoreCase)) continue;

            string content;
            try
            {
                content = await _workspace.ReadFileAsync(workspaceId, path, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read companion file {Path}", path);
                continue;
            }

            var tokens = EstimateTokens(content);
            if (totalTokens + tokens > maxTokenBudget)
            {
                var remaining = maxTokenBudget - totalTokens;
                if (remaining < 200)
                {
                    _logger.LogInformation(
                        "Chunked build: skipping companion {Path}, no budget remaining ({Total}/{Budget})",
                        path, totalTokens, maxTokenBudget);
                    break;
                }
                content = TruncateToTokens(content, remaining);
                tokens = EstimateTokens(content);
            }

            var relative = Path.IsPathRooted(path)
                ? Path.GetRelativePath(rootDir, path)
                : path;
            files.Add(new FileContext(
                FilePath: path,
                RelativePath: relative,
                Content: content,
                IsExtractedSummary: false));
            totalTokens += tokens;
        }

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
