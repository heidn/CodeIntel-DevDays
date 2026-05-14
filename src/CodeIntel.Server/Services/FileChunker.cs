using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services;

/// <summary>
/// One contiguous slice of a file. Lines are 1-based and inclusive on both ends.
/// </summary>
public record ChunkRange(int StartLine, int EndLine, string Content);

/// <summary>
/// Splits a single source file into sequential chunks for analysis when the file
/// exceeds the per-run context budget. Boundary detection is language-aware where
/// it's cheap (C#/TS via brace balance, PL/SQL via END;/CREATE-OR-REPLACE seams);
/// everything else falls back to line-balanced halves with a small overlap so
/// cross-boundary patterns don't get split mid-statement.
///
/// Algorithm version is exposed via <see cref="Version"/> and folded into the
/// result cache key so future tuning invalidates chunked results automatically.
/// </summary>
public static class FileChunker
{
    /// <summary>
    /// Algorithm version. Bump on any behavioural change (boundary heuristic, overlap
    /// size, fallback split logic). Folded into the result-cache key so older chunked
    /// runs are invalidated automatically when the algorithm changes.
    /// </summary>
    public const string Version = "chunk-v1";

    private const int OverlapLines = 10;

    /// <summary>
    /// Returns null when the file fits inside <paramref name="maxTokensPerChunk"/>
    /// (caller should use the file as-is). Otherwise returns the ordered chunks.
    /// </summary>
    public static IReadOnlyList<ChunkRange>? ComputeChunks(
        string content,
        string extension,
        int maxTokensPerChunk,
        double tokensPerCharEstimate,
        int maxChunks)
    {
        if (string.IsNullOrEmpty(content)) return null;
        var totalTokens = (int)Math.Ceiling(content.Length * tokensPerCharEstimate);
        if (totalTokens <= maxTokensPerChunk) return null;

        var lines = SplitLines(content);
        var maxCharsPerChunk = (int)(maxTokensPerChunk / tokensPerCharEstimate);

        // Try language-specific seams first; fall back to line-balanced if it doesn't
        // produce a usable partition (e.g., minified file, one giant function).
        var seams = FindSeams(lines, extension);
        var chunks = SeamsToChunks(lines, seams, maxCharsPerChunk, maxChunks);
        if (chunks is null || chunks.Count == 0)
            chunks = LineBalancedChunks(lines, maxCharsPerChunk, maxChunks);

        return chunks;
    }

    private static List<string> SplitLines(string content)
    {
        var lines = new List<string>();
        var i = 0;
        var start = 0;
        while (i < content.Length)
        {
            if (content[i] == '\n')
            {
                lines.Add(content.Substring(start, i - start + 1));
                start = i + 1;
            }
            i++;
        }
        if (start < content.Length) lines.Add(content.Substring(start));
        return lines;
    }

    /// <summary>
    /// Returns candidate seam line indices (0-based) where a chunk can naturally end.
    /// A seam is a *line after which* a clean break is acceptable.
    /// </summary>
    private static List<int> FindSeams(List<string> lines, string extension)
    {
        var ext = (extension ?? "").ToLowerInvariant();
        if (PlSqlFileExtensions.Contains(ext)) return FindPlSqlSeams(lines);
        return ext switch
        {
            ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".java" => FindBraceSeams(lines),
            _ => new List<int>(),
        };
    }

    /// <summary>
    /// Brace-balance seam detector. A seam is the line where depth transitions from 1
    /// back to 0 — i.e., the end of a top-level class/function. Tolerant of strings
    /// and line comments via a small state machine; not a full lexer.
    /// </summary>
    private static List<int> FindBraceSeams(List<string> lines)
    {
        var seams = new List<int>();
        var depth = 0;
        for (var idx = 0; idx < lines.Count; idx++)
        {
            var line = lines[idx];
            var inString = false;
            var stringQuote = '\0';
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (inString)
                {
                    if (c == '\\' && i + 1 < line.Length) { i++; continue; }
                    if (c == stringQuote) inString = false;
                    continue;
                }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break; // line comment
                if (c == '"' || c == '\'' || c == '`') { inString = true; stringQuote = c; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    var prev = depth;
                    depth = Math.Max(0, depth - 1);
                    if (prev == 1 && depth == 0) { seams.Add(idx); break; }
                }
            }
        }
        return seams;
    }

    /// <summary>
    /// PL/SQL seam: `END;` (optionally with a name) followed by blank or another
    /// CREATE OR REPLACE, OR a `CREATE OR REPLACE` line itself (one slot before it).
    /// Case-insensitive prefix match keeps this cheap.
    /// </summary>
    private static List<int> FindPlSqlSeams(List<string> lines)
    {
        var seams = new List<int>();
        for (var idx = 0; idx < lines.Count; idx++)
        {
            var trimmed = lines[idx].TrimStart();
            if (trimmed.StartsWith("END", StringComparison.OrdinalIgnoreCase)
                && (trimmed.Contains(';') || trimmed.Length <= 5))
            {
                seams.Add(idx);
            }
            else if (idx > 0
                     && trimmed.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase)
                     && trimmed.Length > 6
                     && char.IsWhiteSpace(trimmed[6]))
            {
                seams.Add(idx - 1);
            }
        }
        return seams;
    }

    /// <summary>
    /// Walks the file and emits one chunk per "seam reached without overflowing the
    /// per-chunk budget" event. If the next seam would overflow, emit a chunk up to
    /// the prior seam and start a fresh chunk there.
    /// </summary>
    private static List<ChunkRange>? SeamsToChunks(
        List<string> lines, List<int> seams, int maxCharsPerChunk, int maxChunks)
    {
        if (seams.Count == 0) return null;
        var chunks = new List<ChunkRange>();
        var chunkStartLine = 0;
        var lastUsableSeam = -1;
        var charsSinceStart = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            charsSinceStart += lines[i].Length;
            var isSeam = seams.BinarySearch(i) >= 0;

            if (charsSinceStart > maxCharsPerChunk)
            {
                // Overflowed. Cut at the last seam we saw inside this chunk.
                if (lastUsableSeam >= chunkStartLine)
                {
                    chunks.Add(BuildRange(lines, chunkStartLine, lastUsableSeam));
                    // Restart fresh from the next line (no overlap when we have a
                    // structural boundary — the carry-over notes carry context).
                    chunkStartLine = lastUsableSeam + 1;
                    lastUsableSeam = -1;
                    charsSinceStart = SumLengths(lines, chunkStartLine, i);
                    if (chunks.Count >= maxChunks - 1)
                    {
                        // Spend the last slot on everything remaining.
                        chunks.Add(BuildRange(lines, chunkStartLine, lines.Count - 1));
                        return chunks;
                    }
                }
                else
                {
                    // No seam in this region — fall through to line-balanced.
                    return null;
                }
            }
            if (isSeam) lastUsableSeam = i;
        }

        // Tail
        if (chunkStartLine < lines.Count)
            chunks.Add(BuildRange(lines, chunkStartLine, lines.Count - 1));

        // If we got just one chunk back, the file didn't actually need splitting at
        // these seams — let the caller treat it as "no chunking needed".
        return chunks.Count <= 1 ? null : chunks;
    }

    private static List<ChunkRange> LineBalancedChunks(
        List<string> lines, int maxCharsPerChunk, int maxChunks)
    {
        var chunks = new List<ChunkRange>();
        var lineIdx = 0;
        while (lineIdx < lines.Count && chunks.Count < maxChunks)
        {
            var start = lineIdx;
            var charsSoFar = 0;
            while (lineIdx < lines.Count && charsSoFar + lines[lineIdx].Length <= maxCharsPerChunk)
            {
                charsSoFar += lines[lineIdx].Length;
                lineIdx++;
            }
            // Don't infinite-loop on a single line larger than the budget.
            if (lineIdx == start) lineIdx = start + 1;

            var end = lineIdx - 1;
            chunks.Add(BuildRange(lines, start, end));

            // Apply overlap for the next chunk so a statement that straddles a boundary
            // is at least partially visible on both sides.
            if (lineIdx < lines.Count)
                lineIdx = Math.Max(end + 1 - OverlapLines, end + 1);
        }

        // Last chunk takes everything remaining if we hit maxChunks early.
        if (lineIdx < lines.Count && chunks.Count > 0)
        {
            var last = chunks[^1];
            chunks[^1] = BuildRange(lines, last.StartLine - 1, lines.Count - 1);
        }
        return chunks;
    }

    private static ChunkRange BuildRange(List<string> lines, int startIdx, int endIdx)
    {
        var content = string.Concat(lines.GetRange(startIdx, endIdx - startIdx + 1));
        return new ChunkRange(startIdx + 1, endIdx + 1, content);
    }

    private static int SumLengths(List<string> lines, int startIdx, int endIdxInclusive)
    {
        var total = 0;
        for (var i = startIdx; i <= endIdxInclusive && i < lines.Count; i++)
            total += lines[i].Length;
        return total;
    }
}
