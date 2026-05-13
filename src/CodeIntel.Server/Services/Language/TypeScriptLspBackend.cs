using CodeIntel.Server.Models;
using CodeIntel.Server.Services.LanguageBackends.Lsp;

namespace CodeIntel.Server.Services.LanguageBackends;

/// <summary>
/// TypeScript / JavaScript backend. Talks to typescript-language-server (an npm
/// package) over LSP for semantic operations — references, definitions, document
/// symbols — and walks the workspace folder for the file tree.
///
/// Per-workspace LSP sessions are managed by <see cref="ILspSessionManager"/>;
/// this class is the LSP-aware adapter the rest of the app sees. When the LSP
/// session is unavailable (binary not on PATH, init timed out, etc.) every
/// operation degrades gracefully to "no result" rather than throwing — callers
/// will fall back to the text-based search paths in ContextRequestHandler.
/// </summary>
public sealed class TypeScriptLspBackend : ILanguageBackend
{
    public const string BackendId = "typescript-lsp";

    private static readonly string[] TsExtensions = [".ts", ".tsx", ".js", ".jsx"];
    private static readonly string[] TsExcludeDirs = ["node_modules", "dist", ".next", "out", ".cache"];
    private static readonly string[] TsExcludePatterns = [".min.js", ".d.ts"];

    private readonly ILspSessionManager _lsp;
    private readonly ILogger<TypeScriptLspBackend> _logger;

    public Language Language => Language.TypeScript;

    public LanguageCapabilities Capabilities { get; } = new(
        SupportsTrace: true,
        SupportsCallers: true,
        SupportsCallees: true,
        SupportsMetrics: false,  // TODO post-B1: tree-sitter-driven TS metrics
        SupportsSemanticSymbolLookup: true);

    public TypeScriptLspBackend(ILspSessionManager lsp, ILogger<TypeScriptLspBackend> logger)
    {
        _lsp = lsp;
        _logger = logger;
    }

    public bool CanHandle(Language language) => language == Language.TypeScript;

    public async Task<Workspace> LoadWorkspaceAsync(string path, CancellationToken ct)
    {
        var workspace = await FileScanner.ScanAsync(
            path: path,
            language: Language.TypeScript,
            extensions: TsExtensions,
            excludeDirs: TsExcludeDirs,
            excludePatterns: TsExcludePatterns,
            ct: ct);

        // Fire-and-forget the LSP session start so workspace-load doesn't block
        // on typescript-language-server's initial project indexing (can take a
        // few seconds on big React repos). Operations that need semantic info
        // will await the session inside the session manager.
        _ = Task.Run(async () =>
        {
            try
            {
                await _lsp.StartSessionAsync(workspace.Id, workspace.RootFolder!, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to start LSP session for workspace {Id} — semantic features disabled, falling back to text search",
                    workspace.Id);
            }
        }, CancellationToken.None);

        return workspace;
    }

    public async Task OnWorkspaceUnloadedAsync(string workspaceId, CancellationToken ct)
    {
        await _lsp.StopSessionAsync(workspaceId, ct);
    }

    public async Task<SymbolLookupResult?> FindClassAsync(string workspaceId, string name, CancellationToken ct)
    {
        var session = await _lsp.TryGetSessionAsync(workspaceId, ct);
        if (session is null) return null;
        return await session.FindClassAsync(name, ct);
    }

    public async Task<SymbolLookupResult?> FindMethodAsync(string workspaceId, string name, CancellationToken ct)
    {
        var session = await _lsp.TryGetSessionAsync(workspaceId, ct);
        if (session is null) return null;
        return await session.FindMethodAsync(name, ct);
    }

    public async Task<IReadOnlyList<CallerInfo>> FindCallersAsync(
        string workspaceId, string methodName, CancellationToken ct)
    {
        var session = await _lsp.TryGetSessionAsync(workspaceId, ct);
        if (session is null) return Array.Empty<CallerInfo>();
        return await session.FindCallersAsync(methodName, ct);
    }

    public async Task<DefinitionLocation?> FindDefinitionAsync(
        string workspaceId, string filePath, int line, int character, CancellationToken ct)
    {
        var session = await _lsp.TryGetSessionAsync(workspaceId, ct);
        if (session is null) return null;
        return await session.FindDefinitionAsync(filePath, line, character, ct);
    }

    public async Task<IReadOnlyList<MethodHandle>> ResolveEntryPointCandidatesAsync(
        string workspaceId, TraceEntryPoint entryPoint, CancellationToken ct)
    {
        var session = await _lsp.TryGetSessionAsync(workspaceId, ct);
        if (session is null) return Array.Empty<MethodHandle>();
        return await session.ResolveEntryPointAsync(entryPoint, ct);
    }

    public async Task<IReadOnlyList<MethodHandle>> FindCallersOfAsync(
        string workspaceId, MethodHandle target, CancellationToken ct)
    {
        var session = await _lsp.TryGetSessionAsync(workspaceId, ct);
        if (session is null) return Array.Empty<MethodHandle>();
        return await session.FindCallersOfAsync(target, ct);
    }

    public async Task<IReadOnlyList<MethodHandle>> FindCalleesOfAsync(
        string workspaceId, MethodHandle source, CancellationToken ct)
    {
        var session = await _lsp.TryGetSessionAsync(workspaceId, ct);
        if (session is null) return Array.Empty<MethodHandle>();
        return await session.FindCalleesOfAsync(source, ct);
    }

    public async Task<string?> GetMethodBodyAsync(string workspaceId, MethodHandle method, CancellationToken ct)
    {
        var session = await _lsp.TryGetSessionAsync(workspaceId, ct);
        if (session is null) return null;
        return await session.GetMethodBodyAsync(method, ct);
    }

    public NodeKind ClassifyNode(MethodHandle method)
    {
        // Heuristic only — TS doesn't have a single "DbContext" pattern. We classify
        // by name hints: anything that looks like an HTTP call or a Prisma/Drizzle/
        // TypeORM DB call gets categorized accordingly.
        var name = method.DisplayName;
        var lower = name.ToLowerInvariant();
        if (lower.Contains("fetch") || lower.Contains("axios")
            || lower.Contains("http") || lower.Contains(".get(") || lower.Contains(".post("))
            return NodeKind.HttpCall;
        if (lower.Contains("prisma.") || lower.Contains("drizzle.")
            || lower.Contains(".query(") || lower.Contains("repository.")
            || lower.Contains("sequelize."))
            return NodeKind.DbAccess;
        return NodeKind.Normal;
    }

    public FileMetricsResult ComputeFileMetrics(string filePath, string relativePath, string content) =>
        new(filePath, relativePath, Language.TypeScript,
            TotalLines: content.Split('\n').Length,
            Methods: [],
            ErrorMessage: "TypeScript metrics not yet implemented");
}
