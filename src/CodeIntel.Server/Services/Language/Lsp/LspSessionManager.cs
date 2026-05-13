using System.Collections.Concurrent;
using CodeIntel.Server.Models;
using Microsoft.Extensions.Options;

namespace CodeIntel.Server.Services.LanguageBackends.Lsp;

/// <summary>
/// Production LSP session manager. Spawns one typescript-language-server child
/// process per loaded TypeScript workspace, lazily on first use. Sessions are
/// torn down when the workspace is evicted from <see cref="WorkspaceService"/>'s
/// LRU.
///
/// Lifecycle: <c>StartSessionAsync</c> blocks until LSP <c>initialize</c> completes
/// (typically &lt;3s on a small project, can be longer on large ones — see
/// <see cref="LspOptions.InitializeTimeoutSeconds"/>). All errors are swallowed
/// at the boundary so a hung or missing LSP binary degrades gracefully: callers
/// see null sessions and the backend falls back to text-search paths.
/// </summary>
public sealed class LspSessionManager : ILspSessionManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<LspSession>>> _sessions = new();
    private readonly LspOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LspSessionManager> _logger;

    public LspSessionManager(
        IOptions<LspOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<LspSessionManager> logger)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public Task<ILspSession> StartSessionAsync(string workspaceId, string rootFolder, CancellationToken ct)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("LSP is disabled (Lsp:Enabled=false)");

        // The Lazy<Task> trick: if two callers race on the same workspaceId, only
        // one StartAsync runs and both await the same task.
        var lazy = _sessions.GetOrAdd(workspaceId, id => new Lazy<Task<LspSession>>(() =>
            LspSession.StartAsync(id, rootFolder, _options,
                _loggerFactory.CreateLogger($"LspSession[{id}]"), ct)));

        return lazy.Value.ContinueWith<ILspSession>(t =>
        {
            if (t.IsFaulted)
            {
                _sessions.TryRemove(workspaceId, out _);
                Exception toThrow = t.Exception?.InnerException
                    ?? (Exception?)t.Exception
                    ?? new InvalidOperationException("LSP start failed");
                throw toThrow;
            }
            return t.Result;
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    public async Task<ILspSession?> TryGetSessionAsync(string workspaceId, CancellationToken ct)
    {
        if (!_options.Enabled) return null;
        if (!_sessions.TryGetValue(workspaceId, out var lazy)) return null;

        try
        {
            var session = await lazy.Value.WaitAsync(ct);
            return session.IsReady ? session : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TryGetSession failed for {Id}", workspaceId);
            return null;
        }
    }

    public async Task StopSessionAsync(string workspaceId, CancellationToken ct)
    {
        if (_sessions.TryRemove(workspaceId, out var lazy))
        {
            try
            {
                if (lazy.IsValueCreated)
                {
                    var session = await lazy.Value;
                    await session.DisposeAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "StopSession error for {Id}", workspaceId);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (id, lazy) in _sessions.ToArray())
        {
            try
            {
                if (lazy.IsValueCreated)
                    await (await lazy.Value).DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Dispose error for session {Id}", id);
            }
        }
        _sessions.Clear();
    }
}
