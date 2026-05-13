using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services.LanguageBackends.Lsp;

/// <summary>
/// Manages per-workspace LSP child processes. One typescript-language-server
/// instance per loaded workspace, spawned lazily on first semantic operation
/// (or eagerly during workspace load) and torn down on workspace eviction.
/// </summary>
public interface ILspSessionManager
{
    /// <summary>
    /// Starts an LSP session for the given workspace if one isn't already running.
    /// Throws if the LSP binary isn't available — callers are expected to catch
    /// and degrade gracefully.
    /// </summary>
    Task<ILspSession> StartSessionAsync(string workspaceId, string rootFolder, CancellationToken ct);

    /// <summary>
    /// Returns the running session for a workspace, or null if no session is
    /// running and one can't be started right now. Used for "best effort" calls
    /// from the backend — callers fall back to text search on null.
    /// </summary>
    Task<ILspSession?> TryGetSessionAsync(string workspaceId, CancellationToken ct);

    Task StopSessionAsync(string workspaceId, CancellationToken ct);
}

/// <summary>
/// One running LSP session, scoped to a workspace. Implementations talk JSON-RPC
/// to a typescript-language-server child process over stdio.
/// </summary>
public interface ILspSession
{
    string WorkspaceId { get; }
    bool IsReady { get; }

    Task<SymbolLookupResult?> FindClassAsync(string name, CancellationToken ct);
    Task<SymbolLookupResult?> FindMethodAsync(string name, CancellationToken ct);
    Task<IReadOnlyList<CallerInfo>> FindCallersAsync(string methodName, CancellationToken ct);
    Task<DefinitionLocation?> FindDefinitionAsync(string filePath, int line, int character, CancellationToken ct);

    Task<IReadOnlyList<MethodHandle>> ResolveEntryPointAsync(TraceEntryPoint entryPoint, CancellationToken ct);
    Task<IReadOnlyList<MethodHandle>> FindCallersOfAsync(MethodHandle target, CancellationToken ct);
    Task<IReadOnlyList<MethodHandle>> FindCalleesOfAsync(MethodHandle source, CancellationToken ct);
    Task<string?> GetMethodBodyAsync(MethodHandle method, CancellationToken ct);
}
