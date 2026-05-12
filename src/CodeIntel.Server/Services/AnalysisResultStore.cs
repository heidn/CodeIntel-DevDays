using System.Collections.Concurrent;
using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services;

public interface IAnalysisResultStore
{
    void Save(AnalysisResult result);
    AnalysisResult? Get(Guid id);
    IReadOnlyList<AnalysisResult> Recent(int count = 20);
}

public class InMemoryAnalysisResultStore : IAnalysisResultStore
{
    private readonly ConcurrentDictionary<Guid, AnalysisResult> _store = new();

    public void Save(AnalysisResult result) => _store[result.Id] = result;

    public AnalysisResult? Get(Guid id) => _store.TryGetValue(id, out var r) ? r : null;

    public IReadOnlyList<AnalysisResult> Recent(int count = 20) =>
        _store.Values.OrderByDescending(r => r.StartedAt).Take(count).ToList();
}
