using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services.LanguageBackends.Lsp;

/// <summary>
/// Stub LSP session manager that always returns null sessions. Active when
/// LSP is disabled in config or when typescript-language-server isn't available
/// on the host. With this stub installed, TypeScript workspaces still load and
/// the file tree + raw analysis path still works — only semantic-driven trace
/// and class/method lookups degrade to "no results".
///
/// Replaced by <see cref="LspSessionManager"/> when LSP is enabled.
/// </summary>
public sealed class NullLspSessionManager : ILspSessionManager
{
    private readonly ILogger<NullLspSessionManager> _logger;

    public NullLspSessionManager(ILogger<NullLspSessionManager> logger)
    {
        _logger = logger;
    }

    public Task<ILspSession> StartSessionAsync(string workspaceId, string rootFolder, CancellationToken ct)
    {
        _logger.LogInformation(
            "LSP disabled — TypeScript workspace {Id} loaded without semantic backend",
            workspaceId);
        return Task.FromException<ILspSession>(
            new InvalidOperationException("LSP is disabled; configure Lsp:Enabled=true in appsettings.json"));
    }

    public Task<ILspSession?> TryGetSessionAsync(string workspaceId, CancellationToken ct) =>
        Task.FromResult<ILspSession?>(null);

    public Task StopSessionAsync(string workspaceId, CancellationToken ct) => Task.CompletedTask;
}
