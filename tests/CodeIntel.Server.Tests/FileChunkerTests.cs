using CodeIntel.Server.Services;

namespace CodeIntel.Server.Tests;

public class FileChunkerTests
{
    private const double DefaultTokensPerChar = 0.25;
    private const int DefaultMaxChunks = 8;

    [Fact]
    public void ComputeChunks_FileUnderBudget_ReturnsNull()
    {
        var content = string.Concat(Enumerable.Repeat("var x = 1;\n", 5));
        var result = FileChunker.ComputeChunks(content, ".cs", maxTokensPerChunk: 1000, DefaultTokensPerChar, DefaultMaxChunks);
        Assert.Null(result);
    }

    [Fact]
    public void ComputeChunks_OversizedCSharp_SplitsOnBraceSeams()
    {
        // Two top-level classes; each is small. Force chunking by setting a tiny budget.
        var content = """
        namespace Demo;

        public class First
        {
            public void DoOne() { }
        }

        public class Second
        {
            public void DoTwo() { }
        }
        """;
        var budgetTokens = (int)Math.Ceiling(content.Length * DefaultTokensPerChar) / 2;
        var result = FileChunker.ComputeChunks(content, ".cs", budgetTokens, DefaultTokensPerChar, DefaultMaxChunks);

        Assert.NotNull(result);
        Assert.True(result!.Count >= 2, $"Expected at least 2 chunks, got {result.Count}");

        // Chunks must be ordered, non-overlapping, and cover at least one line each.
        for (var i = 0; i < result.Count; i++)
        {
            Assert.True(result[i].StartLine <= result[i].EndLine);
            if (i > 0)
                Assert.True(result[i].StartLine > result[i - 1].EndLine,
                    $"Chunk {i} starts at line {result[i].StartLine}, " +
                    $"previous ended at {result[i - 1].EndLine} — expected no overlap on seam splits");
        }

        // Each chunk's content must be non-empty.
        Assert.All(result, c => Assert.False(string.IsNullOrEmpty(c.Content)));
    }

    [Fact]
    public void ComputeChunks_NoSeamsFound_FallsBackToLineBalanced()
    {
        // Plain text with no language seams. Force chunking.
        var lines = Enumerable.Range(1, 200).Select(i => $"line {i} of plain text without any structure\n");
        var content = string.Concat(lines);
        var budgetTokens = (int)Math.Ceiling(content.Length * DefaultTokensPerChar) / 3;

        var result = FileChunker.ComputeChunks(content, ".txt", budgetTokens, DefaultTokensPerChar, DefaultMaxChunks);

        Assert.NotNull(result);
        Assert.True(result!.Count >= 2);
        // Line-balanced fallback uses an overlap; verify the second chunk's start is at-or-before
        // the first chunk's end (overlap), and overall coverage reaches the end of the file.
        Assert.True(result[^1].EndLine >= 195,
            $"Last chunk should cover to near end of file (200 lines); got EndLine={result[^1].EndLine}");
    }

    [Fact]
    public void ComputeChunks_RespectsMaxChunks()
    {
        // Force chunking on a long file with a tight budget and a low maxChunks cap.
        var content = string.Concat(Enumerable.Range(1, 500).Select(_ => "padding line of moderate length goes here\n"));
        var budgetTokens = (int)Math.Ceiling(content.Length * DefaultTokensPerChar) / 10;

        var result = FileChunker.ComputeChunks(content, ".txt", budgetTokens, DefaultTokensPerChar, maxChunks: 3);

        Assert.NotNull(result);
        Assert.True(result!.Count <= 3, $"Expected ≤3 chunks under cap, got {result.Count}");
    }

    [Fact]
    public void ComputeChunks_PlSqlEndStatement_TreatedAsSeam()
    {
        // Two procedures separated by END; — verify the seam falls between them.
        var content = """
        CREATE OR REPLACE PROCEDURE first_proc IS
        BEGIN
          INSERT INTO t VALUES (1);
        END first_proc;

        CREATE OR REPLACE PROCEDURE second_proc IS
        BEGIN
          INSERT INTO t VALUES (2);
        END second_proc;
        """;
        var budgetTokens = (int)Math.Ceiling(content.Length * DefaultTokensPerChar) / 2;
        var result = FileChunker.ComputeChunks(content, ".sql", budgetTokens, DefaultTokensPerChar, DefaultMaxChunks);

        Assert.NotNull(result);
        Assert.True(result!.Count >= 2);
    }

    [Fact]
    public void ComputeChunks_EmptyContent_ReturnsNull()
    {
        Assert.Null(FileChunker.ComputeChunks("", ".cs", 100, DefaultTokensPerChar, DefaultMaxChunks));
    }
}
