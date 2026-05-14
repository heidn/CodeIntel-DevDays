using System.Text;
using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services;

public interface IReportGenerator
{
    string GenerateMarkdown(AnalysisResult result, string? referenceFilename = null);
    string GenerateTraceMarkdown(TraceResult result, string? referenceFilename = null);
}

public class ReportGenerator : IReportGenerator
{
    public string GenerateMarkdown(AnalysisResult result, string? referenceFilename = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Code Intelligence Report");
        sb.AppendLine();
        sb.AppendLine($"**Generated:** {result.CompletedAt:yyyy-MM-dd HH:mm:ss} UTC  ");
        sb.AppendLine($"**Mode:** {DescribeMode(result)}  ");
        sb.AppendLine($"**Duration:** {result.Duration.TotalSeconds:F1}s  ");
        sb.AppendLine($"**Context tokens:** ~{result.ContextTokens:N0}");
        sb.AppendLine();

        sb.AppendLine("## Scope");
        sb.AppendLine();
        if (result.AnalyzedFiles.Count == 0)
        {
            sb.AppendLine("_(no files)_");
        }
        else
        {
            foreach (var file in result.AnalyzedFiles)
                sb.AppendLine($"- `{file}`");
        }
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(result.FreeTextPrompt))
        {
            sb.AppendLine("## Question");
            sb.AppendLine();
            sb.AppendLine($"> {result.FreeTextPrompt.Replace("\n", "\n> ")}");
            sb.AppendLine();
        }

        sb.AppendLine("## Findings");
        sb.AppendLine();
        if (result.Findings.Count == 0)
        {
            sb.AppendLine("_No structured findings emitted. See raw output below._");
        }
        else
        {
            var lowCount = result.Findings.Count(f => f.Confidence == Confidence.Low);
            if (lowCount > 0)
            {
                var highCount = result.Findings.Count - lowCount;
                sb.AppendLine($"> {highCount} high-confidence · {lowCount} low-confidence. Low-confidence findings need verification before action.");
                sb.AppendLine();
            }

            var bySeverity = result.Findings
                .GroupBy(f => f.Severity)
                .OrderBy(g => (int)g.Key);

            foreach (var group in bySeverity)
            {
                sb.AppendLine($"### {SeverityIcon(group.Key)} {group.Key} ({group.Count()})");
                sb.AppendLine();
                foreach (var finding in group)
                {
                    var titleSuffix = finding.Confidence == Confidence.Low ? " _(low confidence)_" : "";
                    sb.AppendLine($"#### {finding.Title}{titleSuffix}");
                    if (!string.IsNullOrWhiteSpace(finding.FilePath))
                    {
                        var loc = finding.LineNumber.HasValue
                            ? $"`{finding.FilePath}:{finding.LineNumber.Value}`"
                            : $"`{finding.FilePath}`";
                        sb.AppendLine($"**Location:** {loc}");
                    }
                    sb.AppendLine();
                    sb.AppendLine(finding.Description);
                    if (!string.IsNullOrWhiteSpace(finding.CodeSnippet))
                    {
                        sb.AppendLine();
                        sb.AppendLine($"```{CodeLang(finding.FilePath)}");
                        sb.AppendLine(finding.CodeSnippet);
                        sb.AppendLine("```");
                    }
                    sb.AppendLine();
                }
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Copilot Next Step");
        sb.AppendLine();
        var brief = BuildCopilotBrief(result);
        sb.AppendLine(brief.Lead);
        sb.AppendLine();
        if (!string.IsNullOrEmpty(referenceFilename))
        {
            sb.AppendLine("Reference this file in Copilot Chat:");
            sb.AppendLine();
            sb.AppendLine($"```text");
            sb.AppendLine($"#file:{referenceFilename}");
            sb.AppendLine("```");
            sb.AppendLine();
        }
        sb.AppendLine("Then ask:");
        sb.AppendLine();
        sb.AppendLine("```text");
        sb.AppendLine(brief.PromptBody);
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("<details>");
        sb.AppendLine("<summary>Raw LLM output</summary>");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(result.RawLlmOutput);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("</details>");

        return sb.ToString();
    }

    private record CopilotBrief(string Lead, string PromptBody);

    private static CopilotBrief BuildCopilotBrief(AnalysisResult result)
    {
        if (result.Mode == AnalysisMode.FreeText)
        {
            var question = string.IsNullOrWhiteSpace(result.FreeTextPrompt)
                ? "the original question above"
                : result.FreeTextPrompt!.Trim();
            return new(
                "Use the findings above as starting context to address the developer's question.",
                $"The findings above were generated by a local model investigating this question:\n\n  \"{question}\"\n\nUsing the findings as evidence, give the most accurate, complete answer you can. Cite specific file paths and line numbers from the findings. If a finding seems wrong, say so and explain why."
            );
        }

        return result.PresetKey switch
        {
            "find-bugs" => new(
                "The local model surfaced potential bugs. Use Copilot to verify, prioritize, and propose concrete fixes.",
                """
                Review each finding above. For every high-severity item:
                  1. Open the referenced file and confirm the bug actually exists.
                  2. If real, propose a precise code edit (show the diff).
                  3. Explain the failure mode and why the fix is correct.
                  4. Note any related code paths that should also be checked.

                If a finding is a false positive, say so and explain.
                Group your output by file. End with a one-line recommendation for next action.
                """),

            "find-dead-code" => new(
                "The local model flagged candidates for dead code. Verify each before deleting.",
                """
                For each candidate above:
                  1. Confirm there are truly no references — consider reflection, dependency injection,
                     test-only usage, public API surface, and dynamic dispatch.
                  2. If safe to remove, propose the exact edit (file, lines to delete).
                  3. If you cannot confirm, mark it as uncertain and explain what would prove safety.

                Group output by file. Recommend a deletion order if any items have dependencies on each other.
                """),

            "find-business-rules" => new(
                "The local model extracted business rules from the code. Use Copilot to refine them into documentation.",
                """
                For each rule above:
                  1. Restate it in plain business language (no code terminology).
                  2. Identify the source-of-truth code path (file:line).
                  3. Flag any rules that appear contradictory, redundant, or unenforced.
                  4. Note rules that lack obvious test coverage.

                Produce a flat numbered list grouped by domain area. End with open questions for product / domain owners.
                """),

            "summarize" => new(
                "The local model produced a synopsis of the code. Use Copilot to refine into a developer-facing overview.",
                """
                Using the synopsis above, produce a one-page architecture overview:
                  - What this code does (2-3 sentences).
                  - Key components and their responsibilities (bulleted).
                  - Important data flows or call paths.
                  - External dependencies (libraries, services, databases).
                  - Risks, surprises, or unclear areas a new dev should know about.

                Keep it dense and skimmable. Reference specific files when relevant.
                """),

            "find-bugs-sql" => new(
                "The local model surfaced potential PL/SQL bugs. Use Copilot to verify each against Oracle 19c behavior before filing.",
                """
                Review each finding above. For every item:
                  1. Confirm the bug actually exists by reading the cited line in context (open the file referenced under #file:).
                  2. Validate the failure mode against Oracle 19c semantics. Cite the doc / data dictionary view if relevant
                     (ALL_SOURCE, ALL_CONSTRAINTS, ALL_TAB_COLUMNS, ALL_TRIGGERS).
                  3. If real, propose a precise PL/SQL edit (show the before/after). Note any objects whose definition you'd
                     need to inspect before merging (e.g., the actual columns of a table referenced only by alias).
                  4. If a finding is a false positive, say so and explain (e.g., "EXCEPTION block at line N handles this").

                Group output by severity. Produce a Jira-ready summary table at the end: severity / title / file:line / status (confirmed / rejected / needs-investigation).
                """),

            "find-business-rules-sql" => new(
                "The local model extracted business rules from PL/SQL and DDL. Use Copilot to refine into a Confluence-ready spec.",
                """
                For each rule above:
                  1. Restate in plain business language. Drop PL/SQL terminology.
                  2. Identify the enforcement mechanism: CHECK constraint / FK / proc logic / trigger / package default. Cite file:line.
                  3. Flag rules that appear contradictory, redundant, or unenforced anywhere (e.g., a CHECK constraint that
                     a procedure bypasses with `EXCEPTION WHEN OTHERS`).
                  4. Note rules that lack obvious test coverage.

                Produce a Confluence-ready table: rule / domain area / enforcement mechanism / source (file:line) / notes.
                End with a flat list of open questions for product / data owners.
                """),

            "cleanup-stored-proc" => new(
                "The local model identified PL/SQL cleanup targets. Use Copilot to produce a sequenced refactor plan.",
                """
                Using the findings above, produce a numbered refactor plan. For each item:
                  1. Rationale — what makes this code hard to read or maintain (cite the finding).
                  2. Before snippet — quote the current code (file:line).
                  3. After snippet — show the proposed PL/SQL. Preserve behavior; do not change business logic.
                  4. Risk — what could break (lock contention, autonomous transaction interaction, trigger-fire-count change).
                  5. Test approach — how a developer would verify behavior is preserved.

                Sequence the items so independent changes go first and dependent ones last. End with a one-line recommendation
                for which item is the highest-value, lowest-risk place to start.
                """),

            "efficiency-review" => new(
                "The local model flagged PL/SQL performance signals. Use Copilot to confirm against real plan data before changing anything.",
                """
                For each suggestion above:
                  1. Open the file referenced under #file:. Read the actual query.
                  2. Identify the table(s) and indexes the predicate would need to use. Reject the suggestion if the assumed
                     index doesn't exist in the DDL (or if the table is small enough that a full scan is fine).
                  3. For confirmed items, write the exact EXPLAIN PLAN command the developer should run, including bind values
                     that exercise the suspect path.
                  4. Estimate impact: order-of-magnitude row counts and round-trips eliminated. Don't claim a speedup you
                     can't justify from the code.

                Produce a prioritized list: rank by (impact × confidence) / risk. For each item include the EXPLAIN PLAN
                snippet inline. Reject and explain any finding you can't defend.
                """),

            _ => new(
                "Use the findings above as input for follow-up analysis.",
                "Review the findings above. Verify their accuracy, prioritize them, and propose specific next actions. Reference file paths and line numbers from the findings in your response."),
        };
    }

    public string GenerateTraceMarkdown(TraceResult result, string? referenceFilename = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Call-Trail Trace");
        sb.AppendLine();
        sb.AppendLine($"**Generated:** {result.CompletedAt:yyyy-MM-dd HH:mm:ss} UTC  ");
        sb.AppendLine($"**Entry point:** `{result.EntryPointSymbolFqn}`  ");
        sb.AppendLine($"**Direction:** {result.Direction}  ");
        sb.AppendLine($"**Depth:** {result.Depth}  ");
        sb.AppendLine($"**Nodes:** {result.Nodes.Count} · **Edges:** {result.Edges.Count}{(result.Truncated ? " (truncated — per-node fan-out cap hit)" : "")}  ");
        sb.AppendLine($"**Duration:** {result.Duration.TotalSeconds:F1}s");
        sb.AppendLine();

        sb.AppendLine("## Call graph");
        sb.AppendLine();
        sb.AppendLine("```mermaid");
        sb.Append(result.Mermaid);
        if (!result.Mermaid.EndsWith("\n")) sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("## Node synopses");
        sb.AppendLine();
        foreach (var node in result.Nodes)
        {
            sb.AppendLine($"### {node.DisplayName}");
            if (!string.IsNullOrEmpty(node.FilePath))
            {
                var loc = node.Line.HasValue
                    ? $"`{node.FilePath}:{node.Line.Value}`"
                    : $"`{node.FilePath}`";
                sb.AppendLine($"**Location:** {loc}  ");
            }
            sb.AppendLine($"**Symbol:** `{node.SymbolFqn}`");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(node.Synopsis) ? "_(no synopsis)_" : node.Synopsis);
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Copilot Next Step");
        sb.AppendLine();
        var brief = BuildTraceCopilotBrief(result);
        sb.AppendLine(brief);
        sb.AppendLine();
        if (!string.IsNullOrEmpty(referenceFilename))
        {
            sb.AppendLine("Reference this file in Copilot Chat:");
            sb.AppendLine();
            sb.AppendLine("```text");
            sb.AppendLine($"#file:{referenceFilename}");
            sb.AppendLine("```");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildTraceCopilotBrief(TraceResult r) => r.Direction switch
    {
        TraceDirection.Callers => $"""
            The call graph above shows what currently invokes `{r.EntryPointSymbolFqn}`.
            Ask Copilot:

            ```text
            Using the call graph and per-node synopses above, identify the most likely caller
            chain for a bug where {r.EntryPointSymbolFqn.Split('.').Last()} produces an unexpected result.
            For each suspicious caller, name the input or branch that would route through it.
            ```
            """,

        TraceDirection.Callees => $"""
            The call graph above shows what `{r.EntryPointSymbolFqn}` does internally.
            Ask Copilot:

            ```text
            Using the call graph and per-node synopses above, produce a one-page developer-facing
            overview of {r.EntryPointSymbolFqn.Split('.').Last()}: what it does, what it touches
            (DBs, files, external services), and any risks or surprises a new dev should know.
            ```
            """,

        TraceDirection.Both => $"""
            The call graph above shows both what invokes `{r.EntryPointSymbolFqn}` and what it
            calls internally. Ask Copilot:

            ```text
            Using the bidirectional call graph and per-node synopses above, write a focused
            change-impact analysis: if I modify the behavior of {r.EntryPointSymbolFqn.Split('.').Last()},
            which callers are affected, and which downstream operations might break?
            ```
            """,

        _ => "Reference the call graph and per-node synopses above in Copilot Chat to dig deeper.",
    };

    private static string CodeLang(string? filePath)
    {
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        if (ext is not null && PlSqlFileExtensions.Contains(ext)) return "sql";
        return ext switch
        {
            ".cs" => "csharp",
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" => "javascript",
            ".py" => "python",
            ".go" => "go",
            ".java" => "java",
            _ => ""
        };
    }

    private static string DescribeMode(AnalysisResult r) => r.Mode switch
    {
        AnalysisMode.Preset => $"Preset — {r.PresetKey}",
        AnalysisMode.FreeText => "Free-text question",
        _ => r.Mode.ToString()
    };

    private static string SeverityIcon(Severity s) => s switch
    {
        Severity.Bug => "🔴",
        Severity.Warning => "🟡",
        Severity.Suggestion => "🟢",
        Severity.DeadCode => "💀",
        Severity.Info => "🔵",
        _ => "•"
    };
}
