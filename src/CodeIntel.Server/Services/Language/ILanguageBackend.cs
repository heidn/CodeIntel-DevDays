using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services.LanguageBackends;

/// <summary>
/// Per-language abstraction that the rest of the app talks to instead of poking
/// a specific compiler API. Roslyn becomes <see cref="CSharpRoslynBackend"/>, ANTLR
/// becomes <see cref="PlSqlBackend"/>, and TypeScript ships as an LSP client.
///
/// Operations return language-neutral DTOs and opaque <see cref="MethodHandle"/>s
/// — the backend owns whatever real symbol object lives inside the handle. Consumers
/// like <c>TraceWalker</c> pass handles around but never inspect their contents.
///
/// Not every backend implements every operation. The PL/SQL backend, for instance,
/// has no notion of call-graph traversal — its <see cref="FindCallersAsync"/> just
/// returns empty. Use <see cref="Capabilities"/> to query support before showing
/// affordances in the UI.
/// </summary>
public interface ILanguageBackend
{
    Language Language { get; }

    LanguageCapabilities Capabilities { get; }

    /// <summary>
    /// Decide whether this backend should handle a freshly-loaded workspace.
    /// Called by <see cref="ILanguageBackendRegistry"/> during the dispatch step.
    /// </summary>
    bool CanHandle(Language language);

    /// <summary>
    /// Loads a workspace from disk. For C#, opens an MSBuildWorkspace and walks
    /// the solution. For TS/Java/PL-SQL, walks the folder with language-specific
    /// extension filters. After this call, the backend owns any per-workspace
    /// semantic state until <see cref="OnWorkspaceUnloadedAsync"/> is called.
    /// </summary>
    Task<Workspace> LoadWorkspaceAsync(string path, CancellationToken ct);

    /// <summary>
    /// Called when a workspace is evicted or the app shuts down. Backend should
    /// release any process / file handles it owns for this workspace.
    /// </summary>
    Task OnWorkspaceUnloadedAsync(string workspaceId, CancellationToken ct);

    // ---------- Symbol queries (used by ContextRequestHandler) ----------

    Task<SymbolLookupResult?> FindClassAsync(string workspaceId, string name, CancellationToken ct);

    Task<SymbolLookupResult?> FindMethodAsync(string workspaceId, string name, CancellationToken ct);

    Task<IReadOnlyList<CallerInfo>> FindCallersAsync(string workspaceId, string methodName, CancellationToken ct);

    Task<DefinitionLocation?> FindDefinitionAsync(
        string workspaceId, string filePath, int line, int character, CancellationToken ct);

    // ---------- Trace-mode operations (used by TraceWalker) ----------

    /// <summary>
    /// Resolve a method name (e.g. "OrderService.Submit") or file/line/char position
    /// to candidate method handles. The UI will show a disambiguation dialog when
    /// more than one is returned.
    /// </summary>
    Task<IReadOnlyList<MethodHandle>> ResolveEntryPointCandidatesAsync(
        string workspaceId, TraceEntryPoint entryPoint, CancellationToken ct);

    Task<IReadOnlyList<MethodHandle>> FindCallersOfAsync(
        string workspaceId, MethodHandle target, CancellationToken ct);

    Task<IReadOnlyList<MethodHandle>> FindCalleesOfAsync(
        string workspaceId, MethodHandle source, CancellationToken ct);

    /// <summary>
    /// Returns the full source of the method's declaring syntax (or a best-effort
    /// extraction for backends without a full AST), trimmed to a reasonable length.
    /// </summary>
    Task<string?> GetMethodBodyAsync(string workspaceId, MethodHandle method, CancellationToken ct);

    /// <summary>
    /// Inspects a handle's containing type / method name to classify it as DB,
    /// HTTP, or normal — used for Mermaid shape selection.
    /// </summary>
    NodeKind ClassifyNode(MethodHandle method);

    // ---------- Metrics (used by MetricsService) ----------

    /// <summary>
    /// Returns per-method structural metrics for a single file. Backends that don't
    /// support metrics for their language should return an empty list (the file row
    /// will surface in the UI with no method rows).
    /// </summary>
    FileMetricsResult ComputeFileMetrics(string filePath, string relativePath, string content);
}

/// <summary>
/// Self-describing capabilities — used by the UI to grey out unsupported affordances
/// (e.g. don't show the Trace tab for PL/SQL since cross-file call-graph isn't
/// supported).
/// </summary>
public record LanguageCapabilities(
    bool SupportsTrace,
    bool SupportsCallers,
    bool SupportsCallees,
    bool SupportsMetrics,
    bool SupportsSemanticSymbolLookup
);

/// <summary>
/// Opaque per-method handle. The wrapped value is whatever the backend needs
/// (IMethodSymbol for Roslyn, SymbolInformation+document URI for LSP, a fully-qualified
/// name string for PL/SQL). Consumers must NOT inspect the value — only pass handles
/// back to the same backend that produced them.
/// </summary>
public sealed class MethodHandle
{
    public string BackendId { get; }
    public string Fqn { get; }
    public string DisplayName { get; }
    public string? FilePath { get; }
    public int? Line { get; }
    public object Payload { get; }

    public MethodHandle(
        string backendId,
        string fqn,
        string displayName,
        string? filePath,
        int? line,
        object payload)
    {
        BackendId = backendId;
        Fqn = fqn;
        DisplayName = displayName;
        FilePath = filePath;
        Line = line;
        Payload = payload;
    }

    public T PayloadAs<T>() where T : class =>
        Payload as T ?? throw new InvalidOperationException(
            $"MethodHandle payload is {Payload.GetType().Name}, not {typeof(T).Name}. " +
            $"Backend {BackendId} should only consume handles it produced.");
}

/// <summary>
/// Result of a class/method lookup — a formatted text block ready to feed back to the LLM.
/// </summary>
public record SymbolLookupResult(string FormattedContent, string? FilePath);

public record CallerInfo(string DisplayName, string? FilePath);

