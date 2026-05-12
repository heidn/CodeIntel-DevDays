using System.Text;
using System.Text.RegularExpressions;
using CodeIntel.Server.Models;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CodeIntel.Server.Services;

public interface IContextRequestHandler
{
    Task<ContextFulfillment> FulfillAsync(string workspaceId, ContextRequest request, CancellationToken ct = default);
}

public class ContextRequestHandler : IContextRequestHandler
{
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<ContextRequestHandler> _logger;

    private const int MaxSearchResults = 5;
    private const int MaxSnippetLines = 80;

    public ContextRequestHandler(IWorkspaceService workspace, ILogger<ContextRequestHandler> logger)
    {
        _workspace = workspace;
        _logger = logger;
    }

    public async Task<ContextFulfillment> FulfillAsync(string workspaceId, ContextRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation("Fulfilling context request: {Type} '{Target}'", request.Type, request.Target);

        try
        {
            var content = request.Type switch
            {
                ContextRequestType.File => await FulfillFileAsync(workspaceId, request.Target, ct),
                ContextRequestType.Class => await FulfillClassAsync(workspaceId, request.Target, ct),
                ContextRequestType.Method => await FulfillMethodAsync(workspaceId, request.Target, ct),
                ContextRequestType.CallersOf => await FulfillCallersAsync(workspaceId, request.Target, ct),
                ContextRequestType.CalleesOf => await FulfillCalleesAsync(workspaceId, request.Target, ct),
                ContextRequestType.SearchCode => await FulfillSearchAsync(workspaceId, request.Target, ct),
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
            // try finding the file by name across the workspace
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
        // Try Roslyn semantic lookup first
        var solution = _workspace.GetRoslynSolution(workspaceId);
        if (solution != null)
        {
            foreach (var project in solution.Projects)
            {
                var symbols = await SymbolFinder.FindDeclarationsAsync(
                    project, className, ignoreCase: false,
                    filter: Microsoft.CodeAnalysis.SymbolFilter.Type, cancellationToken: ct);

                foreach (var sym in symbols.Take(1))
                {
                    var location = sym.Locations.FirstOrDefault(l => l.IsInSource);
                    if (location?.SourceTree == null) continue;
                    var filePath = location.SourceTree.FilePath;
                    if (!File.Exists(filePath)) continue;
                    var content = await File.ReadAllTextAsync(filePath, ct);
                    return FormatFileBlock(filePath, content);
                }
            }
        }

        // Fallback: text search
        return await SearchByPatternAsync(workspaceId,
            new Regex($@"\bclass\s+{Regex.Escape(className)}\b", RegexOptions.Compiled),
            $"class {className}", ct);
    }

    private async Task<string?> FulfillMethodAsync(string workspaceId, string methodName, CancellationToken ct)
    {
        // Try Roslyn semantic lookup
        var solution = _workspace.GetRoslynSolution(workspaceId);
        if (solution != null)
        {
            foreach (var project in solution.Projects)
            {
                var symbols = await SymbolFinder.FindDeclarationsAsync(
                    project, methodName, ignoreCase: false,
                    filter: Microsoft.CodeAnalysis.SymbolFilter.Member, cancellationToken: ct);

                foreach (var sym in symbols.Take(1))
                {
                    var location = sym.Locations.FirstOrDefault(l => l.IsInSource);
                    if (location?.SourceTree == null) continue;
                    var filePath = location.SourceTree.FilePath;
                    if (!File.Exists(filePath)) continue;
                    var content = await File.ReadAllTextAsync(filePath, ct);
                    var snippet = ExtractMethodSnippet(content, methodName, location.GetLineSpan().StartLinePosition.Line);
                    return FormatSnippetBlock(filePath, snippet ?? content);
                }
            }
        }

        // Fallback: text search
        return await SearchByPatternAsync(workspaceId,
            new Regex($@"\b{Regex.Escape(methodName)}\s*[(<]", RegexOptions.Compiled),
            $"method {methodName}", ct);
    }

    private async Task<string?> FulfillCallersAsync(string workspaceId, string methodName, CancellationToken ct)
    {
        var solution = _workspace.GetRoslynSolution(workspaceId);
        if (solution != null)
        {
            var declarations = new List<Microsoft.CodeAnalysis.ISymbol>();
            foreach (var project in solution.Projects)
            {
                var found = await SymbolFinder.FindDeclarationsAsync(
                    project, methodName, ignoreCase: false,
                    filter: Microsoft.CodeAnalysis.SymbolFilter.Member, cancellationToken: ct);
                declarations.AddRange(found);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"// Callers of '{methodName}':");

            foreach (var sym in declarations.Take(3))
            {
                var callers = await SymbolFinder.FindCallersAsync(sym, solution, ct);
                foreach (var caller in callers.Take(MaxSearchResults))
                {
                    var location = caller.CallingSymbol.Locations.FirstOrDefault(l => l.IsInSource);
                    if (location == null) continue;
                    sb.AppendLine($"// - {caller.CallingSymbol.ToDisplayString()} in {Path.GetFileName(location.SourceTree?.FilePath ?? "")}");
                }
            }

            if (sb.Length > 20) return sb.ToString();
        }

        // Fallback to text search
        return await SearchByPatternAsync(workspaceId,
            new Regex($@"\b{Regex.Escape(methodName)}\s*\(", RegexOptions.Compiled),
            $"callers of {methodName}", ct);
    }

    private async Task<string?> FulfillCalleesAsync(string workspaceId, string methodName, CancellationToken ct)
    {
        // Text-based: find the method body and extract calls from it
        return await SearchByPatternAsync(workspaceId,
            new Regex($@"\b{Regex.Escape(methodName)}\s*[(<]", RegexOptions.Compiled),
            $"method body of {methodName}", ct);
    }

    private async Task<string?> FulfillSearchAsync(string workspaceId, string searchTerm, CancellationToken ct)
    {
        var pattern = new Regex(Regex.Escape(searchTerm), RegexOptions.Compiled | RegexOptions.IgnoreCase);
        return await SearchByPatternAsync(workspaceId, pattern, $"'{searchTerm}'", ct);
    }

    private async Task<string?> SearchByPatternAsync(string workspaceId, Regex pattern, string label, CancellationToken ct)
    {
        var ws = _workspace.GetWorkspace(workspaceId);
        if (ws == null) return null;

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
                    sb.AppendLine("```csharp");

                    // show context window around first match
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

    private static string FormatSnippetBlock(string path, string snippet)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// SNIPPET from: {path}");
        sb.AppendLine("```csharp");
        sb.AppendLine(snippet);
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static string? ExtractMethodSnippet(string fileContent, string methodName, int declarationLine)
    {
        var lines = fileContent.Split('\n');
        if (declarationLine >= lines.Length) return null;
        var end = Math.Min(lines.Length - 1, declarationLine + MaxSnippetLines);
        return string.Join('\n', lines[declarationLine..(end + 1)]);
    }
}
