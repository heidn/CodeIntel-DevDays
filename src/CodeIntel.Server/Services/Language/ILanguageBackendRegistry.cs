using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services.LanguageBackends;

/// <summary>
/// Picks the right <see cref="ILanguageBackend"/> for a workspace. The registry
/// is itself a singleton; backends are also singletons (their per-workspace state
/// lives inside them, keyed by workspaceId).
/// </summary>
public interface ILanguageBackendRegistry
{
    ILanguageBackend GetBackend(Language language);

    ILanguageBackend GetBackendForWorkspace(string workspaceId);

    /// <summary>
    /// Records the backend chosen for a workspace at load time so subsequent calls
    /// (analysis, trace, metrics) route consistently without re-checking the language.
    /// </summary>
    void RegisterWorkspace(string workspaceId, Language language);

    void UnregisterWorkspace(string workspaceId);
}

public sealed class LanguageBackendRegistry : ILanguageBackendRegistry
{
    private readonly IReadOnlyList<ILanguageBackend> _backends;
    private readonly Dictionary<string, ILanguageBackend> _byWorkspace = new();
    private readonly ILogger<LanguageBackendRegistry> _logger;
    private readonly object _lock = new();

    public LanguageBackendRegistry(
        IEnumerable<ILanguageBackend> backends,
        ILogger<LanguageBackendRegistry> logger)
    {
        _backends = backends.ToList();
        _logger = logger;
    }

    public ILanguageBackend GetBackend(Language language)
    {
        var backend = _backends.FirstOrDefault(b => b.CanHandle(language));
        if (backend is null)
            throw new NotSupportedException(
                $"No language backend registered for {language}. " +
                $"Registered: {string.Join(", ", _backends.Select(b => b.Language))}");
        return backend;
    }

    public ILanguageBackend GetBackendForWorkspace(string workspaceId)
    {
        lock (_lock)
        {
            if (_byWorkspace.TryGetValue(workspaceId, out var backend))
                return backend;
        }
        throw new InvalidOperationException(
            $"Workspace '{workspaceId}' has no registered backend. Was it loaded successfully?");
    }

    public void RegisterWorkspace(string workspaceId, Language language)
    {
        var backend = GetBackend(language);
        lock (_lock)
        {
            _byWorkspace[workspaceId] = backend;
        }
        _logger.LogInformation("Workspace {Id} routed to {Backend} backend",
            workspaceId, backend.Language);
    }

    public void UnregisterWorkspace(string workspaceId)
    {
        lock (_lock)
        {
            _byWorkspace.Remove(workspaceId);
        }
    }
}
