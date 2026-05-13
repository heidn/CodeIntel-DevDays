using System.Text;
using System.Text.RegularExpressions;
using CodeIntel.Server.Models;
using CodeIntel.Server.Services.LanguageBackends;

namespace CodeIntel.Server.Services;

public interface IContextRequestHandler
{
    Task<ContextFulfillment> FulfillAsync(string workspaceId, ContextRequest request, CancellationToken ct = default);
}

/// <summary>
/// Fulfils the LLM's <c>&lt;request_context&gt;</c> calls during the agentic loop.
/// All semantic lookups go through <see cref="ILanguageBackendRegistry"/> — this
/// handler is now language-agnostic. When the backend returns no result, we fall
/// back to a regex-driven text search over the workspace's file tree (same as
/// the pre-B1 behavior).
/// </summary>
public class ContextRequestHandler : IContextRequestHandler
{
    private readonly IWorkspaceService _workspace;
    private readonly ILanguageBackendRegistry _backends;
    private readonly PlSqlBackend _plSqlBackend; // for the OracleObject pathway
    private readonly ILogger<ContextRequestHandler> _logger;

    private const int MaxSearchResults = 5;
    private const int MaxSnippetLines = 80;

    public ContextRequestHandler(
        IWorkspaceService workspace,
        ILanguageBackendRegistry backends,
        PlSqlBackend plSqlBackend,
        ILogger<ContextRequestHandler> logger)
    {
        _workspace = workspace;
        _backends = backends;
        _plSqlBackend = plSqlBackend;
        _logger = logger;
    }

    public async Task<ContextFulfillment> FulfillAsync(
        string workspaceId, ContextRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation("Fulfilling context request: {Type} '{Target}'", request.Type, request.Target);

        try
        {
            var content = request.Type switch
            {
                ContextRequestType.File         => await FulfillFileAsync(workspaceId, request.Target, ct),
                ContextRequestType.Class        => await FulfillClassAsync(workspaceId, request.Target, ct),
                ContextRequestType.Method       => await FulfillMethodAsync(workspaceId, request.Target, ct),
                ContextRequestType.CallersOf    => await FulfillCallersAsync(workspaceId, request.Target, ct),
                ContextRequestType.CalleesOf    => await FulfillCalleesAsync(workspaceId, request.Target, ct),
                ContextRequestType.SearchCode   => await FulfillSearchAsync(workspaceId, request.Target, ct),
                ContextRequestType.OracleObject => await FulfillOracleObjectAsync(workspaceId, request.Target, ct),
                _ => null,
            };

            if (content == null)
                return new ContextFulfillment(request, $"// No results found for {request.Type} '{request.Target}'", false);

            return new ContextFulfillment(request, content, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fulfill {Type} request for '{Target}'", request.Type, request.Target);
            return new ContextFulfillment(request, $"// Error resolving {request.Type} '{request.Target}': {ex.Message}", false);
        }
    }

    private async Task<string?> FulfillFileAsync(string workspaceId, string target, CancellationToken ct)
    {
        try
        {
            var content = await _workspace.ReadFileAsync(workspaceId, target, ct);
            return FormatFileBlock(target, content);
        }
        catch (FileNotFoundException)
        {
            return await FindFileByNameAsync(workspaceId, target, ct);
        }
    }

    private async Task<string?> FindFileByNameAsync(string workspaceId, string fileName, CancellationToken ct)
    {
        var ws = _workspace.GetWorkspace(workspaceId);
        if (ws == null) return null;

        var name = Path.GetFileName(fileName);
        foreach (var project in ws.Projects)
        {
            var match = project.Files.FirstOrDefault(f =>
                f.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                var content = await _workspace.ReadFileAsync(workspaceId, match.AbsolutePath, ct);
                return FormatFileBlock(match.RelativePath, content);
            }
        }
        return null;
    }

    private async Task<string?> FulfillClassAsync(string workspaceId, string className, CancellationToken ct)
    {
        var backend = _backends.GetBackendForWorkspace(workspaceId);
        var semantic = await backend.FindClassAsync(workspaceId, className, ct);
        if (semantic is not null) return semantic.FormattedContent;

        // Backend couldn't find it (Roslyn returned empty, LSP unavailable, PL/SQL
        // didn't match) — fall back to a workspace-wide regex search.
        return await SearchByPatternAsync(workspaceId,
            new Regex($@"\bclass\s+{Regex.Escape(className)}\b", RegexOptions.Compiled),
            $"class {className}", ct);
    }

    private async Task<string?> FulfillMethodAsync(string workspaceId, string methodName, CancellationToken ct)
    {
        var backend = _backends.GetBackendForWorkspace(workspaceId);
        var semantic = await backend.FindMethodAsync(workspaceId, methodName, ct);
        if (semantic is not null) return semantic.FormattedContent;

        return await SearchByPatternAsync(workspaceId,
            new Regex($@"\b{Regex.Escape(methodName)}\s*[(<]", RegexOptions.Compiled),
            $"method {methodName}", ct);
    }

    private async Task<string?> FulfillCallersAsync(string workspaceId, string methodName, CancellationToken ct)
    {
        var backend = _backends.GetBackendForWorkspace(workspaceId);
        if (backend.Capabilities.SupportsCallers)
        {
            var callers = await backend.FindCallersAsync(workspaceId, methodName, ct);
            if (callers.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"// Callers of '{methodName}':");
                foreach (var c in callers)
                    sb.AppendLine($"// - {c.DisplayName}{(c.FilePath is null ? "" : $" in {Path.GetFileName(c.FilePath)}")}");
                return sb.ToString();
            }
        }

        return await SearchByPatternAsync(workspaceId,
            new Regex($@"\b{Regex.Escape(methodName)}\s*\(", RegexOptions.Compiled),
            $"callers of {methodName}", ct);
    }

    private async Task<string?> FulfillCalleesAsync(string workspaceId, string methodName, CancellationToken ct)
    {
        return await SearchByPatternAsync(workspaceId,
            new Regex($@"\b{Regex.Escape(methodName)}\s*[(<]", RegexOptions.Compiled),
            $"method body of {methodName}", ct);
    }

    private async Task<string?> FulfillSearchAsync(string workspaceId, string searchTerm, CancellationToken ct)
    {
        var pattern = new Regex(Regex.Escape(searchTerm), RegexOptions.Compiled | RegexOptions.IgnoreCase);
        return await SearchByPatternAsync(workspaceId, pattern, $"'{searchTerm}'", ct);
    }

    private async Task<string?> FulfillOracleObjectAsync(string workspaceId, string objectName, CancellationToken ct)
    {
        // OracleObject is the one type that bypasses the generic backend lookup —
        // it goes straight to the PL/SQL resolver. The backend interface deliberately
        // doesn't expose this since the operation is meaningless for other languages.
        var result = await _plSqlBackend.ResolveOracleObjectAsync(workspaceId, objectName, ct);
        return result?.FormattedContent;
    }

    private async Task<string?> SearchByPatternAsync(string workspaceId, Regex pattern, string label, CancellationToken ct)
    {
        var ws = _workspace.GetWorkspace(workspaceId);
        if (ws == null) return null;

        var fence = FenceFor(ws.Language);
        var sb = new StringBuilder();
        sb.AppendLine($"// Search results for {label}:");
        var resultCount = 0;

        foreach (var project in ws.Projects)
        {
            foreach (var file in project.Files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var content = await File.ReadAllTextAsync(file.AbsolutePath, ct);
                    if (!pattern.IsMatch(content)) continue;

                    var lines = content.Split('\n');
                    var matchingLineNums = new List<int>();
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (pattern.IsMatch(lines[i]))
                            matchingLineNums.Add(i);
                    }

                    if (matchingLineNums.Count == 0) continue;

                    sb.AppendLine();
                    sb.AppendLine($"// FILE: {file.RelativePath}");
                    sb.AppendLine($"```{fence}");

                    var firstMatch = matchingLineNums[0];
                    var start = Math.Max(0, firstMatch - 5);
                    var end = Math.Min(lines.Length - 1, firstMatch + MaxSnippetLines);
                    for (int i = start; i <= end; i++)
                        sb.AppendLine($"{i + 1}: {lines[i]}");

                    sb.AppendLine("```");
                    resultCount++;
                    if (resultCount >= MaxSearchResults) break;
                }
                catch { /* skip unreadable files */ }
            }
            if (resultCount >= MaxSearchResults) break;
        }

        return resultCount > 0 ? sb.ToString() : null;
    }

    private static string FormatFileBlock(string path, string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// FILE: {path}");
        sb.AppendLine("```csharp");
        sb.AppendLine(content);
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static string FenceFor(Language language) => language switch
    {
        Language.CSharp     => "csharp",
        Language.TypeScript => "typescript",
        Language.Java       => "java",
        Language.Sql        => "sql",
        _                   => "",
    };
}
