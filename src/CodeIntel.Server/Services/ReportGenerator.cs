using System.Text;
using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services;

public interface IReportGenerator
{
    string GenerateMarkdown(AnalysisResult result);
}

public class ReportGenerator : IReportGenerator
{
    public string GenerateMarkdown(AnalysisResult result)
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
            // group by severity
            var bySeverity = result.Findings
                .GroupBy(f => f.Severity)
                .OrderBy(g => (int)g.Key);

            foreach (var group in bySeverity)
            {
                sb.AppendLine($"### {SeverityIcon(group.Key)} {group.Key} ({group.Count()})");
                sb.AppendLine();
                foreach (var finding in group)
                {
                    sb.AppendLine($"#### {finding.Title}");
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
                        sb.AppendLine("```csharp");
                        sb.AppendLine(finding.CodeSnippet);
                        sb.AppendLine("```");
                    }
                    sb.AppendLine();
                }
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## For Claude Opus");
        sb.AppendLine();
        sb.AppendLine("Paste this report into Claude Opus with one of these prompts:");
        sb.AppendLine();
        sb.AppendLine("- *Generate a Jira ticket for the highest-priority finding above.*");
        sb.AppendLine("- *Write a step-by-step fix plan for the bug(s) identified.*");
        sb.AppendLine("- *Write a PR description that addresses these findings.*");
        sb.AppendLine("- *Assess deployment risk and rollback strategy for these changes.*");
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
