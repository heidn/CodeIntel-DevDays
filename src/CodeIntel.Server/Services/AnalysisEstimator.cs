using CodeIntel.Server.Models;
using Microsoft.Extensions.Options;

namespace CodeIntel.Server.Services;

public record EstimateResult(
    int EstimatedTokens,
    double EstimatedSeconds,
    int SampleSize,
    string Explanation);

public interface IAnalysisEstimator
{
    Task<EstimateResult> EstimateAsync(string workspaceId, IReadOnlyList<string> filePaths, CancellationToken ct);
}

/// <summary>
/// Best-effort "this will take roughly N seconds" projection shown before the run starts.
/// Pulls the per-token rate from recent completed analyses (median of duration/tokens)
/// and applies it to the estimated context-token count. Falls back to a coarse constant
/// rate when there is no history yet.
/// </summary>
public class AnalysisEstimator : IAnalysisEstimator
{
    private const int MinSampleForRate = 2;
    // Fallback when no history exists: 7B q4 CPU produces ~10 tokens/sec.
    // Combined with a typical 5K-token context taking ~90s, that's about 18ms/context-token.
    private const double FallbackSecondsPerToken = 0.018;

    private readonly IWorkspaceService _workspace;
    private readonly IAnalysisResultStore _store;
    private readonly AnalysisOptions _options;
    private readonly ILogger<AnalysisEstimator> _logger;

    public AnalysisEstimator(
        IWorkspaceService workspace,
        IAnalysisResultStore store,
        IOptions<AnalysisOptions> options,
        ILogger<AnalysisEstimator> logger)
    {
        _workspace = workspace;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EstimateResult> EstimateAsync(string workspaceId, IReadOnlyList<string> filePaths, CancellationToken ct)
    {
        var tokens = await EstimateTokensAsync(workspaceId, filePaths, ct);

        var recent = _store.Recent(20)
            .Where(r => r.ContextTokens > 0 && r.Duration.TotalSeconds > 1)
            .ToList();

        double secondsPerToken;
        string explanation;
        int sampleSize = recent.Count;

        if (recent.Count >= MinSampleForRate)
        {
            // Median is more robust than mean when one run timed out at 600s.
            var rates = recent
                .Select(r => r.Duration.TotalSeconds / r.ContextTokens)
                .OrderBy(x => x)
                .ToList();
            secondsPerToken = rates[rates.Count / 2];
            explanation = $"based on median of your last {recent.Count} runs";
        }
        else
        {
            secondsPerToken = FallbackSecondsPerToken;
            explanation = recent.Count == 0
                ? "no history yet — using a coarse default rate"
                : $"only {recent.Count} prior run; using a coarse default rate";
        }

        var seconds = tokens * secondsPerToken;
        return new EstimateResult(tokens, seconds, sampleSize, explanation);
    }

    private async Task<int> EstimateTokensAsync(string workspaceId, IReadOnlyList<string> filePaths, CancellationToken ct)
    {
        var totalChars = 0L;
        foreach (var p in filePaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var content = await _workspace.ReadFileAsync(workspaceId, p, ct);
                totalChars += content.Length;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Estimator skipping unreadable file {Path}", p);
            }
        }
        var capped = Math.Min(totalChars, _options.MaxContextTokens / _options.TokensPerCharEstimate);
        return (int)Math.Ceiling(capped * _options.TokensPerCharEstimate);
    }
}
