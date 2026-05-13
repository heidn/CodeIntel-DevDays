using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services;

public record FindingsDiff(
    List<Finding> Added,
    List<Finding> Resolved,
    List<Finding> Persisted);

public static class FindingsComparer
{
    /// <summary>
    /// Compares two analysis runs and bins each finding into <c>Added</c>, <c>Resolved</c>,
    /// or <c>Persisted</c> based on a coarse signature (severity + file path + title).
    /// Description and line number can drift between runs and aren't part of the key.
    /// </summary>
    public static FindingsDiff Compare(AnalysisResult before, AnalysisResult after)
    {
        string Sig(Finding f) => $"{f.Severity}|{f.FilePath ?? "?"}|{f.Title.Trim().ToLowerInvariant()}";

        var beforeMap = before.Findings.ToDictionary(Sig, f => f, StringComparer.Ordinal);
        var afterMap  = after.Findings.ToDictionary(Sig, f => f, StringComparer.Ordinal);

        var added     = afterMap.Where(kv => !beforeMap.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        var resolved  = beforeMap.Where(kv => !afterMap.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        var persisted = afterMap.Where(kv =>  beforeMap.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();

        return new FindingsDiff(added, resolved, persisted);
    }
}
