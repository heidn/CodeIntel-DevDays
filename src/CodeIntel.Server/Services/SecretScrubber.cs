using System.Text.RegularExpressions;

namespace CodeIntel.Server.Services;

/// <summary>
/// Best-effort scrub of common secret patterns before a report is written into the repo.
/// Replaces matched runs with <c>[REDACTED:&lt;reason&gt;]</c> and returns the per-pattern
/// hit counts so the caller can warn the user about what was caught.
///
/// This is deliberately a coarse net — the goal is to avoid the embarrassing case
/// where the LLM echoes back an API key in <c>codeSnippet</c>, not to be a full
/// secret-scanner. Pair with policy ("don't analyze files you wouldn't paste in chat").
/// </summary>
public static class SecretScrubber
{
    public record ScrubResult(string Scrubbed, IReadOnlyDictionary<string, int> Hits)
    {
        public bool HasHits => Hits.Values.Any(v => v > 0);
        public int TotalRedactions => Hits.Values.Sum();
    }

    // Each pattern returns (regex, reason). Anchored to either start-of-line or a
    // word-boundary to keep false positives in prose down.
    private static readonly (Regex Pattern, string Reason)[] Patterns =
    {
        // AWS access key id — exactly 20 uppercase + digits beginning with AKIA/ASIA.
        (new(@"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b", RegexOptions.Compiled), "aws-key"),
        // AWS secret access key — heuristic: 40 base64-ish chars labelled secret.
        (new(@"(?i)aws[_-]?secret[_-]?access[_-]?key['""\s:=]+([A-Za-z0-9/+=]{40})", RegexOptions.Compiled), "aws-secret"),
        // Generic high-entropy "secret=..." / "password=..." / "api_key=..." patterns.
        (new(@"(?i)\b(?:api[_-]?key|access[_-]?token|secret(?:[_-]?key)?|password|pwd)\s*[:=]\s*['""]?([A-Za-z0-9._\-/+=]{12,})['""]?", RegexOptions.Compiled), "key-equals-value"),
        // GitHub personal access tokens.
        (new(@"\bghp_[A-Za-z0-9]{36,}\b", RegexOptions.Compiled), "github-pat"),
        // Slack tokens.
        (new(@"\bxox[abposr]-[A-Za-z0-9-]{10,}\b", RegexOptions.Compiled), "slack-token"),
        // Bearer tokens in headers.
        (new(@"(?i)Authorization:\s*Bearer\s+[A-Za-z0-9._\-]+", RegexOptions.Compiled), "bearer-token"),
        // PEM-encoded private keys.
        (new(@"-----BEGIN (?:RSA |EC |OPENSSH |DSA |)PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |OPENSSH |DSA |)PRIVATE KEY-----", RegexOptions.Compiled), "pem-private-key"),
        // JWTs (header.payload.signature with base64url segments).
        (new(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\b", RegexOptions.Compiled), "jwt"),
    };

    public static ScrubResult Scrub(string input)
    {
        if (string.IsNullOrEmpty(input))
            return new ScrubResult(input ?? "", new Dictionary<string, int>());

        var hits = new Dictionary<string, int>();
        var output = input;

        foreach (var (pattern, reason) in Patterns)
        {
            var count = 0;
            output = pattern.Replace(output, _ => { count++; return $"[REDACTED:{reason}]"; });
            if (count > 0) hits[reason] = count;
        }

        return new ScrubResult(output, hits);
    }
}
