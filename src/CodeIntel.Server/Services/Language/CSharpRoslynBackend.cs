using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using CodeIntel.Server.Models;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Workspace = CodeIntel.Server.Models.Workspace;

namespace CodeIntel.Server.Services.LanguageBackends;

/// <summary>
/// Roslyn-backed implementation for C# workspaces. Owns the per-workspace
/// <see cref="MSBuildWorkspace"/> + <see cref="Solution"/> pair; nothing outside
/// this class touches Microsoft.CodeAnalysis types after the B1 refactor.
///
/// The implementation is a near-verbatim move of the pre-B1 Roslyn calls that
/// previously lived in <c>WorkspaceService</c>, <c>ContextRequestHandler</c>, and
/// <c>TraceWalker</c>. Behaviour is unchanged for C# workspaces; the goal is the
/// abstraction seam, not new semantics.
/// </summary>
public sealed class CSharpRoslynBackend : ILanguageBackend, IDisposable
{
    public const string BackendId = "csharp-roslyn";
    private const int MaxSearchResults = 5;
    private const int MaxSnippetLines = 80;
    private const int MaxBodyChars = 2000;

    private static bool _msbuildRegistered;
    private static readonly object _msbuildLock = new();

    private readonly ICSharpMetricsAnalyzer _metricsAnalyzer;
    private readonly ILogger<CSharpRoslynBackend> _logger;
    private readonly ConcurrentDictionary<string, LoadedCSharp> _byWorkspace = new();

    public Language Language => Language.CSharp;

    public LanguageCapabilities Capabilities { get; } = new(
        SupportsTrace: true,
        SupportsCallers: true,
        SupportsCallees: true,
        SupportsMetrics: true,
        SupportsSemanticSymbolLookup: true);

    public CSharpRoslynBackend(
        ICSharpMetricsAnalyzer metricsAnalyzer,
        ILogger<CSharpRoslynBackend> logger)
    {
        _metricsAnalyzer = metricsAnalyzer;
        _logger = logger;
    }

    public bool CanHandle(Language language) => language == Language.CSharp;

    public async Task<Workspace> LoadWorkspaceAsync(string path, CancellationToken ct)
    {
        EnsureMsBuildRegistered();

        string slnPath;
        if (File.Exists(path))
        {
            slnPath = path;
        }
        else
        {
            slnPath = Directory.GetFiles(path, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault()
                ?? Directory.GetFiles(path, "*.csproj", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new FileNotFoundException($"No .sln or .csproj found in {path}");
        }

        _logger.LogInformation("Loading C# solution {Path}", slnPath);

        var msWorkspace = MSBuildWorkspace.Create();
        msWorkspace.WorkspaceFailed += (_, args) =>
            _logger.LogWarning("Workspace warning: {Diagnostic}", args.Diagnostic.Message);

        Solution solution;
        if (slnPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            solution = await msWorkspace.OpenSolutionAsync(slnPath, cancellationToken: ct);
        else
        {
            var project = await msWorkspace.OpenProjectAsync(slnPath, cancellationToken: ct);
            solution = project.Solution;
        }

        var projects = new List<ProjectNode>();
        foreach (var project in solution.Projects.OrderBy(p => p.Name))
        {
            var files = new List<FileNode>();
            foreach (var doc in project.Documents.OrderBy(d => d.FilePath))
            {
                if (string.IsNullOrEmpty(doc.FilePath) || !File.Exists(doc.FilePath)) continue;
                var info = new FileInfo(doc.FilePath);
                var lineCount = 0;
                try { lineCount = File.ReadLines(doc.FilePath).Count(); } catch { }
                files.Add(new FileNode(
                    AbsolutePath: doc.FilePath,
                    RelativePath: GetRelativePath(slnPath, doc.FilePath),
                    FileName: Path.GetFileName(doc.FilePath),
                    LineCount: lineCount,
                    SizeBytes: info.Length
                ));
            }
            projects.Add(new ProjectNode(Name: project.Name, Path: project.FilePath ?? "", Files: files));
        }

        var workspaceId = Guid.NewGuid().ToString("N")[..12];
        var workspace = new Workspace(
            Id: workspaceId,
            ProjectPath: slnPath,
            ProjectName: Path.GetFileNameWithoutExtension(slnPath),
            Projects: projects,
            LoadedAt: DateTime.UtcNow,
            Language: Language.CSharp,
            RootFolder: Path.GetDirectoryName(slnPath),
            EntryFile: slnPath);

        _byWorkspace[workspaceId] = new LoadedCSharp(workspace, msWorkspace, solution);
        _logger.LogInformation("Loaded {ProjectCount} projects, {FileCount} files in workspace {Id}",
            projects.Count, projects.Sum(p => p.Files.Count), workspaceId);
        return workspace;
    }

    public Task OnWorkspaceUnloadedAsync(string workspaceId, CancellationToken ct)
    {
        if (_byWorkspace.TryRemove(workspaceId, out var loaded))
        {
            try { loaded.MsWorkspace?.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Workspace dispose error"); }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Exposed only so <see cref="WorkspaceService"/> can build the public Workspace
    /// listing without re-walking the file tree. Trace/context handlers should
    /// NOT call this — they go through the interface.
    /// </summary>
    public Solution? GetSolution(string workspaceId) =>
        _byWorkspace.TryGetValue(workspaceId, out var loaded) ? loaded.Solution : null;

    // ============================================================
    //  Symbol queries — replace the old ContextRequestHandler paths
    // ============================================================

    public async Task<SymbolLookupResult?> FindClassAsync(string workspaceId, string name, CancellationToken ct)
    {
        var solution = GetSolution(workspaceId);
        if (solution is null) return null;

        foreach (var project in solution.Projects)
        {
            var symbols = await SymbolFinder.FindDeclarationsAsync(
                project, name, ignoreCase: false,
                filter: SymbolFilter.Type, cancellationToken: ct);

            foreach (var sym in symbols.Take(1))
            {
                var location = sym.Locations.FirstOrDefault(l => l.IsInSource);
                if (location?.SourceTree is null) continue;
                var filePath = location.SourceTree.FilePath;
                if (!File.Exists(filePath)) continue;
                var content = await File.ReadAllTextAsync(filePath, ct);
                return new SymbolLookupResult(FormatBlock(filePath, content, "FILE", "csharp"), filePath);
            }
        }
        return null;
    }

    public async Task<SymbolLookupResult?> FindMethodAsync(string workspaceId, string name, CancellationToken ct)
    {
        var solution = GetSolution(workspaceId);
        if (solution is null) return null;

        foreach (var project in solution.Projects)
        {
            var symbols = await SymbolFinder.FindDeclarationsAsync(
                project, name, ignoreCase: false,
                filter: SymbolFilter.Member, cancellationToken: ct);

            foreach (var sym in symbols.Take(1))
            {
                var declRef = sym.DeclaringSyntaxReferences.FirstOrDefault();
                if (declRef is null) continue;
                var syntax = await declRef.GetSyntaxAsync(ct);
                var source = syntax.ToFullString();
                if (string.IsNullOrWhiteSpace(source)) continue;
                var filePath = declRef.SyntaxTree.FilePath;
                return new SymbolLookupResult(FormatBlock(filePath, source, "SNIPPET from", "csharp"), filePath);
            }
        }
        return null;
    }

    public async Task<IReadOnlyList<CallerInfo>> FindCallersAsync(
        string workspaceId, string methodName, CancellationToken ct)
    {
        var solution = GetSolution(workspaceId);
        if (solution is null) return Array.Empty<CallerInfo>();

        var results = new List<CallerInfo>();
        var declarations = new List<ISymbol>();
        foreach (var project in solution.Projects)
        {
            var found = await SymbolFinder.FindDeclarationsAsync(
                project, methodName, ignoreCase: false,
                filter: SymbolFilter.Member, cancellationToken: ct);
            declarations.AddRange(found);
        }

        foreach (var sym in declarations.Take(3))
        {
            var callers = await SymbolFinder.FindCallersAsync(sym, solution, ct);
            foreach (var caller in callers.Take(MaxSearchResults))
            {
                var location = caller.CallingSymbol.Locations.FirstOrDefault(l => l.IsInSource);
                if (location is null) continue;
                results.Add(new CallerInfo(
                    DisplayName: caller.CallingSymbol.ToDisplayString(),
                    FilePath: location.SourceTree?.FilePath));
            }
        }
        return results;
    }

    public async Task<DefinitionLocation?> FindDefinitionAsync(
        string workspaceId, string filePath, int line, int character, CancellationToken ct)
    {
        var solution = GetSolution(workspaceId);
        if (solution is null) return null;

        var document = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        if (document is null) return null;

        var sourceText = await document.GetTextAsync(ct);
        var lineIndex = line - 1;
        if (lineIndex < 0 || lineIndex >= sourceText.Lines.Count) return null;

        var lineSpanLength = sourceText.Lines[lineIndex].Span.Length;
        var position = sourceText.Lines[lineIndex].Start + Math.Min(character, lineSpanLength);

        var root = await document.GetSyntaxRootAsync(ct);
        var model = await document.GetSemanticModelAsync(ct);
        if (root is null || model is null) return null;

        var token = root.FindToken(position);
        var node = token.Parent;
        if (node is null) return null;

        var symbol = model.GetSymbolInfo(node).Symbol
            ?? model.GetSymbolInfo(node).CandidateSymbols.FirstOrDefault()
            ?? model.GetDeclaredSymbol(node);

        if (symbol is null) return null;

        var defLocation = symbol.OriginalDefinition.Locations
            .FirstOrDefault(l => l.IsInSource && l.SourceTree is not null);

        if (defLocation is null) return null;

        var defSpan = defLocation.GetLineSpan();
        return new DefinitionLocation(
            FilePath: defLocation.SourceTree!.FilePath,
            Line: defSpan.StartLinePosition.Line + 1,
            Character: defSpan.StartLinePosition.Character,
            SymbolName: symbol.Name);
    }

    // ============================================================
    //  Trace-mode operations — replace the old TraceWalker paths
    // ============================================================

    public async Task<IReadOnlyList<MethodHandle>> ResolveEntryPointCandidatesAsync(
        string workspaceId, TraceEntryPoint entryPoint, CancellationToken ct)
    {
        var solution = GetSolution(workspaceId);
        if (solution is null) return Array.Empty<MethodHandle>();

        if (!string.IsNullOrWhiteSpace(entryPoint.MethodName))
        {
            var methods = await FindCandidateMethodsAsync(solution, entryPoint.MethodName, ct);
            return methods.Select(m => BuildHandle(m)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(entryPoint.FilePath)
            && entryPoint.Line is int line
            && entryPoint.Character is int chr)
        {
            var document = solution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => string.Equals(d.FilePath, entryPoint.FilePath, StringComparison.OrdinalIgnoreCase));
            if (document is null) return Array.Empty<MethodHandle>();

            var sourceText = await document.GetTextAsync(ct);
            var lineIndex = line - 1;
            if (lineIndex < 0 || lineIndex >= sourceText.Lines.Count) return Array.Empty<MethodHandle>();
            var position = sourceText.Lines[lineIndex].Start
                + Math.Min(chr, sourceText.Lines[lineIndex].Span.Length);

            var root = await document.GetSyntaxRootAsync(ct);
            var model = await document.GetSemanticModelAsync(ct);
            if (root is null || model is null) return Array.Empty<MethodHandle>();

            var token = root.FindToken(position);
            var node = token.Parent;
            if (node is null) return Array.Empty<MethodHandle>();

            var sym = model.GetSymbolInfo(node).Symbol
                ?? model.GetSymbolInfo(node).CandidateSymbols.FirstOrDefault()
                ?? model.GetDeclaredSymbol(node);

            if (sym is IMethodSymbol method)
                return new[] { BuildHandle((method.OriginalDefinition as IMethodSymbol) ?? method) };
        }

        return Array.Empty<MethodHandle>();
    }

    public async Task<IReadOnlyList<MethodHandle>> FindCallersOfAsync(
        string workspaceId, MethodHandle target, CancellationToken ct)
    {
        var solution = GetSolution(workspaceId);
        if (solution is null) return Array.Empty<MethodHandle>();

        var method = target.PayloadAs<IMethodSymbol>();
        var callers = await SymbolFinder.FindCallersAsync(method, solution, ct);
        var results = new List<MethodHandle>();
        foreach (var caller in callers)
        {
            if (caller.CallingSymbol is not IMethodSymbol m) continue;
            if (!IsTraceable(m)) continue;
            results.Add(BuildHandle(m));
        }
        return results;
    }

    public async Task<IReadOnlyList<MethodHandle>> FindCalleesOfAsync(
        string workspaceId, MethodHandle source, CancellationToken ct)
    {
        var solution = GetSolution(workspaceId);
        if (solution is null) return Array.Empty<MethodHandle>();

        var method = source.PayloadAs<IMethodSymbol>();
        var results = new List<MethodHandle>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declRef in method.DeclaringSyntaxReferences)
        {
            ct.ThrowIfCancellationRequested();
            var syntaxNode = await declRef.GetSyntaxAsync(ct);
            var doc = solution.GetDocument(syntaxNode.SyntaxTree);
            if (doc is null) continue;

            var model = await doc.GetSemanticModelAsync(ct);
            if (model is null) continue;

            foreach (var node in syntaxNode.DescendantNodes())
            {
                ct.ThrowIfCancellationRequested();
                IMethodSymbol? called = node switch
                {
                    InvocationExpressionSyntax inv =>
                        model.GetSymbolInfo(inv).Symbol as IMethodSymbol,
                    ObjectCreationExpressionSyntax oce =>
                        model.GetSymbolInfo(oce).Symbol as IMethodSymbol,
                    _ => null,
                };
                if (called is null) continue;
                var orig = (called.OriginalDefinition as IMethodSymbol) ?? called;
                if (!IsTraceable(orig)) continue;

                var fqn = orig.ToDisplayString();
                if (seen.Add(fqn)) results.Add(BuildHandle(orig));
            }
        }
        return results;
    }

    public async Task<string?> GetMethodBodyAsync(string workspaceId, MethodHandle method, CancellationToken ct)
    {
        var sym = method.PayloadAs<IMethodSymbol>();
        foreach (var declRef in sym.DeclaringSyntaxReferences)
        {
            ct.ThrowIfCancellationRequested();
            var syntax = await declRef.GetSyntaxAsync(ct);
            var text = syntax.ToFullString();
            return text.Length <= MaxBodyChars
                ? text
                : text.Substring(0, MaxBodyChars) + "\n// ... [truncated]";
        }
        return null;
    }

    public NodeKind ClassifyNode(MethodHandle handle)
    {
        var m = handle.PayloadAs<IMethodSymbol>();
        var containingType = m.ContainingType;
        if (containingType is null) return NodeKind.Normal;

        var t = containingType;
        while (t is not null)
        {
            var n = t.Name;
            if (n is "DbContext" or "IdentityDbContext" or "DbSet") return NodeKind.DbAccess;
            t = t.BaseType;
        }

        foreach (var iface in containingType.AllInterfaces)
        {
            if (iface.Name is "IQueryable" or "IAsyncQueryProvider") return NodeKind.DbAccess;
        }

        if (m.Name is "SaveChanges" or "SaveChangesAsync"
                   or "FromSqlRaw" or "FromSqlInterpolated" or "FromSql"
                   or "ExecuteSqlRaw" or "ExecuteSqlInterpolated" or "ExecuteSqlRawAsync")
            return NodeKind.DbAccess;

        if (containingType.Name is "HttpClient" or "HttpMessageHandler" or "IHttpClientFactory")
            return NodeKind.HttpCall;

        if (m.Name is "GetAsync" or "PostAsync" or "PutAsync" or "PatchAsync"
                   or "DeleteAsync" or "SendAsync" or "GetStringAsync"
                   or "GetByteArrayAsync" or "GetStreamAsync")
        {
            if (!m.Locations.Any(l => l.IsInSource)) return NodeKind.HttpCall;
        }

        return NodeKind.Normal;
    }

    public FileMetricsResult ComputeFileMetrics(string filePath, string relativePath, string content) =>
        _metricsAnalyzer.Analyze(filePath, relativePath, content);

    // ============================================================
    //  Roslyn helpers — private
    // ============================================================

    private MethodHandle BuildHandle(IMethodSymbol m)
    {
        var loc = m.Locations.FirstOrDefault(l => l.IsInSource);
        return new MethodHandle(
            backendId: BackendId,
            fqn: m.ToDisplayString(),
            displayName: BuildDisplayName(m),
            filePath: loc?.SourceTree?.FilePath,
            line: loc is not null ? loc.GetLineSpan().StartLinePosition.Line + 1 : null,
            payload: m);
    }

    private static string BuildDisplayName(IMethodSymbol m)
    {
        var typeName = m.ContainingType?.Name ?? "?";
        return m.MethodKind switch
        {
            MethodKind.Constructor => $"new {typeName}",
            MethodKind.StaticConstructor => $"{typeName}.cctor",
            _ => $"{typeName}.{m.Name}",
        };
    }

    private static bool IsTraceable(IMethodSymbol m) =>
        m.MethodKind is MethodKind.Ordinary
            or MethodKind.Constructor
            or MethodKind.LocalFunction
        && m.Locations.Any(l => l.IsInSource);

    private static async Task<List<IMethodSymbol>> FindCandidateMethodsAsync(
        Solution solution, string methodNameRef, CancellationToken ct)
    {
        var parts = methodNameRef.Trim().Split('.');
        var methodPart = parts[^1];
        var typeHint = parts.Length > 1 ? parts[^2] : null;
        var nsHint = parts.Length > 2 ? string.Join('.', parts[..^2]) : null;

        var candidates = new List<IMethodSymbol>();
        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            var found = await SymbolFinder.FindDeclarationsAsync(
                project, methodPart, ignoreCase: false,
                filter: SymbolFilter.Member, cancellationToken: ct);
            candidates.AddRange(found.OfType<IMethodSymbol>()
                .Where(m => m.Locations.Any(l => l.IsInSource)));
        }

        if (typeHint is not null)
            candidates = candidates.Where(m => m.ContainingType?.Name == typeHint).ToList();
        if (nsHint is not null)
            candidates = candidates
                .Where(m => m.ContainingNamespace?.ToDisplayString().EndsWith(nsHint, StringComparison.Ordinal) ?? false)
                .ToList();

        return candidates
            .GroupBy(m => m.ToDisplayString(), StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    private static string FormatBlock(string path, string content, string label, string fence)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// {label}: {path}");
        sb.AppendLine($"```{fence}");
        sb.AppendLine(content);
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static string GetRelativePath(string basePath, string filePath)
    {
        var baseDir = Directory.Exists(basePath) ? basePath : Path.GetDirectoryName(basePath);
        if (string.IsNullOrEmpty(baseDir)) return filePath;
        try { return Path.GetRelativePath(baseDir, filePath); }
        catch { return filePath; }
    }

    private static void EnsureMsBuildRegistered()
    {
        if (_msbuildRegistered) return;
        lock (_msbuildLock)
        {
            if (_msbuildRegistered) return;
            if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
            _msbuildRegistered = true;
        }
    }

    public void Dispose()
    {
        foreach (var loaded in _byWorkspace.Values)
            loaded.MsWorkspace?.Dispose();
        _byWorkspace.Clear();
    }

    private record LoadedCSharp(Workspace Workspace, MSBuildWorkspace MsWorkspace, Solution Solution);
}
