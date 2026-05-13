using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services.LanguageBackends;

/// <summary>
/// PL/SQL backend. Wraps the existing ANTLR-based <see cref="IPlSqlRepoResolver"/>
/// for the OracleObject context request, and the <see cref="IPlSqlMetricsAnalyzer"/>
/// for structural metrics. Trace mode and cross-file callers are not supported —
/// PL/SQL has no concept of a call graph in the Roslyn sense, and the dynamic
/// EXECUTE IMMEDIATE / package.proc() dispatch patterns common in real PL/SQL
/// repos defeat static analysis.
/// </summary>
public sealed class PlSqlBackend : ILanguageBackend
{
    public const string BackendId = "plsql";

    private static readonly string[] SqlExtensions = [".sql", ".pkg", ".pkb"];

    private readonly IPlSqlRepoResolver _resolver;
    private readonly IPlSqlMetricsAnalyzer _metricsAnalyzer;
    private readonly ILogger<PlSqlBackend> _logger;

    public Language Language => Language.Sql;

    public LanguageCapabilities Capabilities { get; } = new(
        SupportsTrace: false,
        SupportsCallers: false,
        SupportsCallees: false,
        SupportsMetrics: true,
        SupportsSemanticSymbolLookup: false);

    public PlSqlBackend(
        IPlSqlRepoResolver resolver,
        IPlSqlMetricsAnalyzer metricsAnalyzer,
        ILogger<PlSqlBackend> logger)
    {
        _resolver = resolver;
        _metricsAnalyzer = metricsAnalyzer;
        _logger = logger;
    }

    public bool CanHandle(Language language) => language == Language.Sql;

    public Task<Workspace> LoadWorkspaceAsync(string path, CancellationToken ct) =>
        FileScanner.ScanAsync(
            path: path,
            language: Language.Sql,
            extensions: SqlExtensions,
            excludeDirs: [],
            excludePatterns: [],
            ct: ct);

    public Task OnWorkspaceUnloadedAsync(string workspaceId, CancellationToken ct) =>
        Task.CompletedTask;

    public async Task<SymbolLookupResult?> FindClassAsync(string workspaceId, string name, CancellationToken ct)
    {
        // In PL/SQL there's no "class" per se — try resolving by object name.
        var resolution = await _resolver.ResolveAsync(workspaceId, name, PlSqlObjectKind.Unknown, ct);
        if (resolution is null) return null;
        return new SymbolLookupResult(FormatPlSqlBlock(resolution), resolution.RelativePath);
    }

    public Task<SymbolLookupResult?> FindMethodAsync(string workspaceId, string name, CancellationToken ct) =>
        FindClassAsync(workspaceId, name, ct);

    public Task<IReadOnlyList<CallerInfo>> FindCallersAsync(
        string workspaceId, string methodName, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CallerInfo>>(Array.Empty<CallerInfo>());

    public Task<DefinitionLocation?> FindDefinitionAsync(
        string workspaceId, string filePath, int line, int character, CancellationToken ct) =>
        Task.FromResult<DefinitionLocation?>(null);

    public Task<IReadOnlyList<MethodHandle>> ResolveEntryPointCandidatesAsync(
        string workspaceId, TraceEntryPoint entryPoint, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MethodHandle>>(Array.Empty<MethodHandle>());

    public Task<IReadOnlyList<MethodHandle>> FindCallersOfAsync(
        string workspaceId, MethodHandle target, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MethodHandle>>(Array.Empty<MethodHandle>());

    public Task<IReadOnlyList<MethodHandle>> FindCalleesOfAsync(
        string workspaceId, MethodHandle source, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MethodHandle>>(Array.Empty<MethodHandle>());

    public Task<string?> GetMethodBodyAsync(string workspaceId, MethodHandle method, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    public NodeKind ClassifyNode(MethodHandle method) => NodeKind.DbAccess;

    public FileMetricsResult ComputeFileMetrics(string filePath, string relativePath, string content) =>
        _metricsAnalyzer.Analyze(filePath, relativePath, content);

    /// <summary>
    /// Lets <c>ContextRequestHandler</c> route the OracleObject context-request type
    /// directly to the resolver without going through FindClass/FindMethod.
    /// </summary>
    public async Task<SymbolLookupResult?> ResolveOracleObjectAsync(
        string workspaceId, string objectName, CancellationToken ct)
    {
        var resolution = await _resolver.ResolveAsync(workspaceId, objectName, PlSqlObjectKind.Unknown, ct);
        return resolution is null
            ? null
            : new SymbolLookupResult(FormatPlSqlBlock(resolution), resolution.RelativePath);
    }

    private static string FormatPlSqlBlock(PlSqlResolution resolution)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"// PL/SQL OBJECT: {resolution.Name} (resolved via {resolution.ResolvedVia})");
        sb.AppendLine($"// FILE: {resolution.RelativePath}");
        sb.AppendLine("```sql");
        sb.AppendLine(resolution.Content);
        sb.AppendLine("```");
        return sb.ToString();
    }
}
