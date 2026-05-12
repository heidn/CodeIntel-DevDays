using System.Collections.Concurrent;

namespace CodeIntel.Server.Services;

public interface IAnalysisCancellationRegistry
{
    void Register(Guid analysisId, CancellationTokenSource cts);
    bool Cancel(Guid analysisId);
    void Remove(Guid analysisId);
    bool IsRunning(Guid analysisId);
}

public class AnalysisCancellationRegistry : IAnalysisCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _registry = new();

    public void Register(Guid analysisId, CancellationTokenSource cts) =>
        _registry[analysisId] = cts;

    public bool Cancel(Guid analysisId)
    {
        if (!_registry.TryGetValue(analysisId, out var cts)) return false;
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Remove(Guid analysisId) => _registry.TryRemove(analysisId, out _);

    public bool IsRunning(Guid analysisId) => _registry.ContainsKey(analysisId);
}
