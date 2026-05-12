using System.Collections.Concurrent;
using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services;

public interface ITraceResultStore
{
    void Save(TraceResult result);
    TraceResult? Get(Guid id);
    IReadOnlyList<TraceResult> Recent(int count = 20);
}

public class InMemoryTraceResultStore : ITraceResultStore
{
    private readonly ConcurrentDictionary<Guid, TraceResult> _byId = new();
    private readonly object _lock = new();
    private readonly LinkedList<Guid> _recent = new();
    private const int MaxRetained = 100;

    public void Save(TraceResult result)
    {
        _byId[result.Id] = result;
        lock (_lock)
        {
            _recent.Remove(result.Id);
            _recent.AddFirst(result.Id);
            while (_recent.Count > MaxRetained)
            {
                var oldest = _recent.Last!.Value;
                _recent.RemoveLast();
                _byId.TryRemove(oldest, out _);
            }
        }
    }

    public TraceResult? Get(Guid id) => _byId.TryGetValue(id, out var r) ? r : null;

    public IReadOnlyList<TraceResult> Recent(int count = 20)
    {
        lock (_lock)
        {
            return _recent
                .Take(count)
                .Select(id => _byId.TryGetValue(id, out var r) ? r : null)
                .Where(r => r is not null)
                .Cast<TraceResult>()
                .ToList();
        }
    }
}
