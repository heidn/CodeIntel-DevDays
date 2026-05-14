using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CodeIntel.Server.Models;
using CodeIntel.Server.Services.LanguageBackends;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Options;
using Workspace = CodeIntel.Server.Models.Workspace;

namespace CodeIntel.Server.Services;

/// <summary>
/// Workspace facade. Owns the workspace dict, LRU eviction, and the workspace-scoped
/// file-read with <see cref="PathSafety"/>. Language-specific loading and semantic
/// queries are delegated to <see cref="ILanguageBackendRegistry"/> — this service
/// is intentionally thin after the B1 refactor.
///
/// Note the class name (<c>WorkspaceService</c>) and file name (<c>RoslynWorkspaceService.cs</c>)
/// differ for historical reasons — the original implementation was Roslyn-only.
/// </summary>
public interface IWorkspaceService
{
    Task<Workspace> LoadAsync(string path, CancellationToken ct = default);
    Workspace? GetWorkspace(string workspaceId);
    Task<string> ReadFileAsync(string workspaceId, string relativeOrAbsolutePath, CancellationToken ct = default);

    /// <summary>
    /// Returns the underlying Roslyn <see cref="Solution"/> for a workspace, or
    /// null if the workspace isn't C#. Kept for legacy callers that need the raw
    /// Solution; new code should go through <see cref="ILanguageBackend"/> instead.
    /// </summary>
    Solution? GetRoslynSolution(string workspaceId);

    Task<DefinitionLocation?> FindDefinitionAsync(
        string workspaceId, string filePath, int line, int character, CancellationToken ct = default);
}

public sealed class WorkspaceService : IWorkspaceService
{
    private readonly ILogger<WorkspaceService> _logger;
    private readonly AnalysisOptions _options;
    private readonly IServiceProvider _services;

    // Lazy-resolved because the registry depends transitively on PlSqlBackend,
    // which depends on PlSqlRepoResolver, which depends on this WorkspaceService —
    // forming a ctor cycle. Resolved on first use after the container is built.
    private ILanguageBackendRegistry? _registry;
    private CSharpRoslynBackend? _csharpBackend;
    private ILanguageBackendRegistry Registry =>
        _registry ??= _services.GetRequiredService<ILanguageBackendRegistry>();
    private CSharpRoslynBackend CSharpBackend =>
        _csharpBackend ??= _services.GetRequiredService<CSharpRoslynBackend>();

    private readonly ConcurrentDictionary<string, Workspace> _workspaces = new();
    private readonly object _lruLock = new();
    private readonly LinkedList<string> _lruOrder = new();

    public WorkspaceService(
        IOptions<AnalysisOptions> options,
        IServiceProvider services,
        ILogger<WorkspaceService> logger)
    {
        _options = options.Value;
        _services = services;
        _logger = logger;
    }

    public async Task<Workspace> LoadAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty", nameof(path));

        var language = LanguageDetector.Detect(path);
        _logger.LogInformation("Loading {Path} as {Language}", path, language);

        var backend = Registry.GetBackend(language);
        var workspace = await backend.LoadWorkspaceAsync(path, ct);

        _workspaces[workspace.Id] = workspace;
        Registry.RegisterWorkspace(workspace.Id, language);
        await EvictIfNeededAsync(workspace.Id, ct);
        TouchLru(workspace.Id);

        _logger.LogInformation("Loaded workspace {Id} via {Backend}", workspace.Id, backend.Language);
        return workspace;
    }

    private async Task EvictIfNeededAsync(string newId, CancellationToken ct)
    {
        // The new workspace will be added to the LRU after this — pre-check whether
        // the *resulting* count would exceed the cap and evict the oldest if so.
        string? evictId = null;
        lock (_lruLock)
        {
            var cap = Math.Max(1, _options.MaxLoadedWorkspaces);
            if (_lruOrder.Count >= cap && !_lruOrder.Contains(newId))
            {
                evictId = _lruOrder.Last!.Value;
                _lruOrder.RemoveLast();
            }
        }

        if (evictId is null) return;
        if (_workspaces.TryRemove(evictId, out var evicted))
        {
            var backend = Registry.GetBackendForWorkspace(evictId);
            try { await backend.OnWorkspaceUnloadedAsync(evictId, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "Backend unload error for evicted workspace"); }
            Registry.UnregisterWorkspace(evictId);
            _logger.LogInformation("Evicted LRU workspace {Id} ({Project})", evictId, evicted.ProjectName);
        }
    }

    private void TouchLru(string workspaceId)
    {
        lock (_lruLock)
        {
            _lruOrder.Remove(workspaceId);
            _lruOrder.AddFirst(workspaceId);
        }
    }

    public Workspace? GetWorkspace(string workspaceId) =>
        _workspaces.TryGetValue(workspaceId, out var w) ? w : null;

    public Solution? GetRoslynSolution(string workspaceId) =>
        CSharpBackend.GetSolution(workspaceId);

    public async Task<string> ReadFileAsync(string workspaceId, string path, CancellationToken ct = default)
    {
        var ws = GetWorkspace(workspaceId) ?? throw new InvalidOperationException("Workspace not loaded");
        var rootDir = ws.RootFolder ?? throw new InvalidOperationException("Workspace has no resolved root folder.");

        var absolutePath = Path.IsPathRooted(path) ? path : Path.Combine(rootDir, path);

        if (!PathSafety.IsInside(absolutePath, rootDir))
            throw new UnauthorizedAccessException("Path is outside workspace root.");

        var resolved = Path.GetFullPath(absolutePath);
        if (!File.Exists(resolved))
            throw new FileNotFoundException($"File not found: {resolved}");

        return await File.ReadAllTextAsync(resolved, ct);
    }

    public async Task<DefinitionLocation?> FindDefinitionAsync(
        string workspaceId, string filePath, int line, int character, CancellationToken ct = default)
    {
        // First ask the backend — for C# this is the Roslyn semantic path, for TS
        // it's an LSP textDocument/definition call. Fall back to a text-pattern
        // search if the backend declines.
        var backend = Registry.GetBackendForWorkspace(workspaceId);
        var semantic = await backend.FindDefinitionAsync(workspaceId, filePath, line, character, ct);
        if (semantic is not null) return semantic;

        return await FindDefinitionByTextAsync(workspaceId, filePath, line, character, ct);
    }

    // ============================================================
    //  Text-pattern fallback for non-semantic definition lookups
    // ============================================================

    private async Task<DefinitionLocation?> FindDefinitionByTextAsync(
        string workspaceId, string requestingFilePath, int line, int character, CancellationToken ct)
    {
        if (!File.Exists(requestingFilePath)) return null;
        var fileContent = await File.ReadAllTextAsync(requestingFilePath, ct);
        var fileLines = fileContent.Split('\n');
        var lineIndex = line - 1;
        if (lineIndex < 0 || lineIndex >= fileLines.Length) return null;
        var word = ExtractWordFromLine(fileLines[lineIndex], character);
        if (word == null) return null;

        var ws = GetWorkspace(workspaceId);
        if (ws == null) return null;

        var ext = Path.GetExtension(requestingFilePath).ToLowerInvariant();
        var patterns = DefinitionPatterns(ext, word);
        if (patterns.Count == 0) return null;

        var groupExts = SameGroupExtensions(ext);

        var candidates = ws.Projects
            .SelectMany(p => p.Files)
            .Where(f => groupExts.Any(e => f.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => string.Equals(f.AbsolutePath, requestingFilePath, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToList();

        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var content = await File.ReadAllTextAsync(file.AbsolutePath, ct);
                var lines = content.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    foreach (var pattern in patterns)
                    {
                        if (pattern.IsMatch(lines[i]))
                        {
                            return new DefinitionLocation(
                                FilePath: file.AbsolutePath,
                                Line: i + 1,
                                Character: 0,
                                SymbolName: word);
                        }
                    }
                }
            }
            catch { /* skip unreadable files */ }
        }

        return null;
    }

    private static string? ExtractWordFromLine(string line, int character)
    {
        if (character >= line.Length) return null;
        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
        var pos = character;
        if (!IsWordChar(line[pos]))
        {
            if (pos > 0 && IsWordChar(line[pos - 1])) pos--;
            else return null;
        }
        var start = pos;
        var end = pos;
        while (start > 0 && IsWordChar(line[start - 1])) start--;
        while (end < line.Length - 1 && IsWordChar(line[end + 1])) end++;
        var word = line[start..(end + 1)];
        return word.Length > 0 ? word : null;
    }

    private static IReadOnlyList<string> SameGroupExtensions(string ext)
    {
        if (PlSqlFileExtensions.Contains(ext)) return PlSqlFileExtensions.All;
        return ext switch
        {
            ".ts" or ".tsx" or ".js" or ".jsx" => [".ts", ".tsx", ".js", ".jsx"],
            ".java"                             => [".java"],
            _                                   => [ext],
        };
    }

    private static IReadOnlyList<Regex> DefinitionPatterns(string ext, string word)
    {
        var w = Regex.Escape(word);
        const RegexOptions R = RegexOptions.Compiled;
        const RegexOptions RI = RegexOptions.Compiled | RegexOptions.IgnoreCase;

        if (PlSqlFileExtensions.Contains(ext))
            return
            [
                new($@"\bCREATE\b.*?\b(?:PROCEDURE|FUNCTION|PACKAGE(?:\s+BODY)?|TABLE|VIEW|TYPE)\b\s+(?:\w+\.)?{w}\b", RI),
                new($@"^\s*(?:PROCEDURE|FUNCTION)\s+(?:\w+\.)?{w}\b", RI),
            ];

        return ext switch
        {
            ".ts" or ".tsx" or ".js" or ".jsx" =>
            [
                new($@"(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s*\*?\s+{w}\s*[(<]", R),
                new($@"(?:export\s+)?(?:const|let|var)\s+{w}\s*[:=]", R),
                new($@"(?:export\s+)?(?:abstract\s+)?(?:class|interface|type|enum)\s+{w}\b", R),
                new($@"^\s*(?:(?:public|private|protected|static|async|override|abstract|readonly|get|set)\s+)+{w}\s*[(<]", R),
                new($@"^\s+{w}\s*[(<]", R),
            ],
            ".java" =>
            [
                new($@"(?:class|interface|enum|record)\s+{w}\b", R),
                new($@"\b[\w<>\[\]]+\s+{w}\s*\(", R),
            ],
            _ => [],
        };
    }
}

/// <summary>
/// Detects the language of a path (file or folder) using extension hints, marker
/// files (.sln, tsconfig.json, pom.xml), and finally a file-count heuristic.
/// </summary>
public static class LanguageDetector
{
    private static readonly string[] TsExtensions = [".ts", ".tsx", ".js", ".jsx"];
    private static readonly string[] JavaExtensions = [".java"];
    private static readonly string[] CsExtensions = [".cs"];

    public static Language Detect(string path)
    {
        var lower = path.ToLowerInvariant();

        if (lower.EndsWith(".sln") || lower.EndsWith(".csproj"))
            return Language.CSharp;
        if (lower.EndsWith("tsconfig.json") || lower.EndsWith("package.json"))
            return Language.TypeScript;
        if (lower.EndsWith("pom.xml") || lower.EndsWith("build.gradle") || lower.EndsWith("build.gradle.kts"))
            return Language.Java;
        if (PlSqlFileExtensions.All.Any(lower.EndsWith))
            return Language.Sql;

        if (!Directory.Exists(path)) return Language.CSharp;

        if (Directory.GetFiles(path, "*.sln").Length > 0 || Directory.GetFiles(path, "*.csproj").Length > 0)
            return Language.CSharp;
        if (File.Exists(Path.Combine(path, "tsconfig.json")) || File.Exists(Path.Combine(path, "package.json")))
            return Language.TypeScript;
        if (File.Exists(Path.Combine(path, "pom.xml")) || Directory.GetFiles(path, "build.gradle*").Length > 0)
            return Language.Java;

        return CountDominantLanguage(path);
    }

    private static Language CountDominantLanguage(string dir)
    {
        var counts = new Dictionary<Language, int>
        {
            [Language.CSharp] = 0, [Language.TypeScript] = 0, [Language.Java] = 0, [Language.Sql] = 0,
        };
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (CsExtensions.Contains(ext)) counts[Language.CSharp]++;
                else if (TsExtensions.Contains(ext)) counts[Language.TypeScript]++;
                else if (JavaExtensions.Contains(ext)) counts[Language.Java]++;
                else if (PlSqlFileExtensions.Contains(ext)) counts[Language.Sql]++;
            }
        }
        catch { /* best effort */ }
        return counts.OrderByDescending(kv => kv.Value).First().Key;
    }
}
