using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services;

/// <summary>
/// Collapses near-duplicate findings emitted across agentic iterations.
/// The 7B model frequently re-states the same issue with a slightly different
/// wording or on a different line of the same logical block. This pass groups
/// by (severity, file path, lowercased title) and keeps the first occurrence,
/// appending a "(also at line N, ...)" hint to the description when there were
/// multiple line numbers for the same logical finding.
/// </summary>
public static class FindingsAggregator
{
    public static List<Finding> Collapse(IReadOnlyList<Finding> findings)
    {
        if (findings.Count <= 1) return findings.ToList();

        var groups = findings
            .Select((f, i) => (Finding: f, Order: i))
            .GroupBy(x => (x.Finding.Severity, File: x.Finding.FilePath ?? "?", Title: x.Finding.Title.Trim().ToLowerInvariant()))
            .OrderBy(g => g.Min(x => x.Order));

        var collapsed = new List<Finding>(findings.Count);
        foreach (var group in groups)
        {
            var members = group.OrderBy(x => x.Order).ToList();
            var primary = members[0].Finding;
            if (members.Count == 1)
            {
                collapsed.Add(primary);
                continue;
            }

            var extraLines = members
                .Skip(1)
                .Select(m => m.Finding.LineNumber)
                .Where(l => l.HasValue && l != primary.LineNumber)
                .Select(l => l!.Value)
                .Distinct()
                .ToList();

            var hint = extraLines.Count > 0
                ? $"\n\n_Also reported at line(s) {string.Join(", ", extraLines)} ({members.Count} occurrences collapsed)._"
                : $"\n\n_{members.Count} similar reports collapsed._";

            collapsed.Add(primary with { Description = primary.Description + hint });
        }

        return collapsed;
    }
}
