using CodeIntel.Server.Services;

namespace CodeIntel.Server.Tests;

public class FindingStreamParserTests
{
    [Fact]
    public void Append_SingleLineFinding_Parses()
    {
        var p = new FindingStreamParser();
        var emitted = p.Append("<finding>{\"severity\":\"bug\",\"title\":\"x\",\"description\":\"y\"}</finding>").ToList();

        Assert.Single(emitted);
        Assert.Equal("x", emitted[0].Title);
        Assert.Equal(0, p.IncompleteFindingCount);
    }

    [Fact]
    public void Append_MultiLineFindingSplitAcrossChunks_Parses()
    {
        // Reproduces the parser bug surfaced by chunked runs: the model emits
        // <finding> on one line, body across many lines, </finding> on the last —
        // and the streaming parser sees them across separate Append calls. Prior
        // to the pending-open fix, the closer was orphaned and the finding showed
        // up in IncompleteFindingCount instead of Findings.
        var p = new FindingStreamParser();

        p.Append("<finding>\n  {\n    \"severity\": \"deadcode\",\n");
        p.Append("    \"confidence\": \"high\",\n    \"title\": \"unused helper\",\n");
        p.Append("    \"description\": \"never called\"\n  }\n");
        var emitted = p.Append("</finding>\n").ToList();

        Assert.Single(emitted);
        Assert.Equal("unused helper", emitted[0].Title);
        Assert.Equal(0, p.IncompleteFindingCount);
        Assert.Empty(p.MalformedFindings);
    }

    [Fact]
    public void Append_MultipleMultiLineFindings_AllParse()
    {
        var p = new FindingStreamParser();

        // First finding split into two chunks
        p.Append("<finding>\n  {\"severity\":\"bug\",\"title\":\"first\",");
        p.Append("\"description\":\"d1\"}\n</finding>\n");
        // Second finding split similarly
        p.Append("<finding>\n  {\"severity\":\"warning\",\"title\":\"second\",");
        var emitted = p.Append("\"description\":\"d2\"}\n</finding>\n<done />").ToList();

        Assert.Equal(2, p.Findings.Count);
        Assert.Equal("first",  p.Findings[0].Title);
        Assert.Equal("second", p.Findings[1].Title);
        Assert.True(p.IsDone);
        Assert.Equal(0, p.IncompleteFindingCount);
    }

    [Fact]
    public void Append_TrulyIncompleteFinding_StillCountsAsIncomplete()
    {
        var p = new FindingStreamParser();

        p.Append("<finding>\n  {\"severity\":\"bug\",\"title\":\"truncated\"");
        // No more chunks — closer never arrives.

        Assert.Empty(p.Findings);
        Assert.Equal(1, p.IncompleteFindingCount);
    }

    [Fact]
    public void Append_DoneAfterFinding_StreamsBoth()
    {
        var p = new FindingStreamParser();

        var emitted = p.Append(
            "<finding>{\"severity\":\"info\",\"title\":\"a\",\"description\":\"b\"}</finding>\n<done />"
        ).ToList();

        Assert.Single(emitted);
        Assert.True(p.IsDone);
    }
}
