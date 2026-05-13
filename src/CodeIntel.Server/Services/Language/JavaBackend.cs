using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services.LanguageBackends;

/// <summary>
/// Java backend stub. Java support pre-dates B1 and was always file-scan +
/// text-fallback only. This backend preserves that behaviour: no semantic
/// symbol lookup, no trace, no metrics. The class exists so the registry
/// has a backend to dispatch to for <see cref="Language.Java"/> workspaces;
/// upgrading to JDT/LSP would slot in behind this same interface.
/// </summary>
public sealed class JavaBackend : ILanguageBackend
{
    public const string BackendId = "java-stub";

    private static readonly string[] JavaExtensions = [".java"];
    private static readonly string[] JavaExcludeDirs = ["target", "build", ".gradle"];

    public Language Language => Language.Java;

    public LanguageCapabilities Capabilities { get; } = new(
        SupportsTrace: false,
        SupportsCallers: false,
        SupportsCallees: false,
        SupportsMetrics: false,
        SupportsSemanticSymbolLookup: false);

    public bool CanHandle(Language language) => language == Language.Java;

    public Task<Workspace> LoadWorkspaceAsync(string path, CancellationToken ct) =>
        FileScanner.ScanAsync(
            path: path,
            language: Language.Java,
            extensions: JavaExtensions,
            excludeDirs: JavaExcludeDirs,
            excludePatterns: [],
            ct: ct);

    public Task OnWorkspaceUnloadedAsync(string workspaceId, CancellationToken ct) => Task.CompletedTask;

    public Task<SymbolLookupResult?> FindClassAsync(string workspaceId, string name, CancellationToken ct) =>
        Task.FromResult<SymbolLookupResult?>(null);

    public Task<SymbolLookupResult?> FindMethodAsync(string workspaceId, string name, CancellationToken ct) =>
        Task.FromResult<SymbolLookupResult?>(null);

    public Task<IReadOnlyList<CallerInfo>> FindCallersAsync(string workspaceId, string methodName, CancellationToken ct) =>
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

    public NodeKind ClassifyNode(MethodHandle method) => NodeKind.Normal;

    public FileMetricsResult ComputeFileMetrics(string filePath, string relativePath, string content) =>
        new(filePath, relativePath, Language.Java, TotalLines: content.Split('\n').Length, Methods: [], ErrorMessage: null);
}
