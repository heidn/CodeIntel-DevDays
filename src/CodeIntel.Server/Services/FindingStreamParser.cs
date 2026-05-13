using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services;

/// <summary>
/// Stateful streaming parser. Feed tokens in as they arrive; emits findings incrementally.
/// Also collects context requests for the agentic loop — these are read after the stream ends.
/// </summary>
public class FindingStreamParser
{
    private readonly StringBuilder _buffer = new();
    private readonly List<Finding> _findings = new();
    private readonly List<ContextRequest> _contextRequests = new();
    private readonly List<ParseFailure> _malformed = new();

    private static readonly Regex FindingRegex = new(
        @"<finding>(?<json>.*?)</finding>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex FindingOpenRegex = new(
        @"<finding>",
        RegexOptions.Compiled);

    private static readonly Regex FindingCloseRegex = new(
        @"</finding>",
        RegexOptions.Compiled);

    private static readonly Regex ContextRequestRegex = new(
        @"<request_context\s+type=""(?<type>[^""]+)"">(?<target>.*?)</request_context>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public IReadOnlyList<Finding> Findings => _findings;
    public IReadOnlyList<ContextRequest> ContextRequests => _contextRequests;
    public IReadOnlyList<ParseFailure> MalformedFindings => _malformed;
    public string RawOutput => _buffer.ToString();
    public bool IsDone { get; private set; }

    /// <summary>
    /// Number of `&lt;finding&gt;` openings that never received a matching `&lt;/finding&gt;`
    /// closing tag in the stream. These are silently lost — the orchestrator should log this.
    /// </summary>
    public int IncompleteFindingCount
    {
        get
        {
            var text = _buffer.ToString();
            var opens = FindingOpenRegex.Matches(text).Count;
            var closes = FindingCloseRegex.Matches(text).Count;
            return Math.Max(0, opens - closes);
        }
    }

    public record ParseFailure(string Snippet, string Error);

    /// <summary>
    /// Append a chunk of streaming text. Returns any newly-completed findings.
    /// Context requests are accumulated silently and read via ContextRequests after the stream ends.
    /// </summary>
    public IEnumerable<Finding> Append(string chunk)
    {
        _buffer.Append(chunk);
        var text = _buffer.ToString();

        if (!IsDone && text.Contains("<done />", StringComparison.OrdinalIgnoreCase))
            IsDone = true;

        // emit new findings incrementally
        var findingMatches = FindingRegex.Matches(text);
        var newFindings = new List<Finding>();
        for (int i = _findings.Count + _malformed.Count; i < findingMatches.Count; i++)
        {
            var json = findingMatches[i].Groups["json"].Value.Trim();
            var (finding, error) = TryParseFinding(json);
            if (finding != null)
            {
                _findings.Add(finding);
                newFindings.Add(finding);
            }
            else
            {
                _malformed.Add(new ParseFailure(Truncate(json, 200), error ?? "(unknown)"));
            }
        }

        // collect context requests (de-duplicated; read after stream ends)
        var crMatches = ContextRequestRegex.Matches(text);
        for (int i = _contextRequests.Count; i < crMatches.Count; i++)
        {
            var typeStr = crMatches[i].Groups["type"].Value.Trim();
            var target = crMatches[i].Groups["target"].Value.Trim();
            var requestType = ParseContextRequestType(typeStr);
            _contextRequests.Add(new ContextRequest(requestType, target));
        }

        return newFindings;
    }

    private static (Finding? finding, string? error) TryParseFinding(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var severityStr = GetString(root, "severity") ?? "info";
            var severity = ParseSeverity(severityStr);

            var finding = new Finding(
                Severity: severity,
                Title: GetString(root, "title") ?? "(no title)",
                Description: GetString(root, "description") ?? "",
                FilePath: GetString(root, "filePath"),
                LineNumber: GetInt(root, "lineNumber"),
                CodeSnippet: GetString(root, "codeSnippet")
            );
            return (finding, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";

    private static string? GetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Null) return null;
        if (prop.ValueKind == JsonValueKind.String) return prop.GetString();
        return prop.ToString();
    }

    private static int? GetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Null) return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n)) return n;
        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var s)) return s;
        return null;
    }

    private static Severity ParseSeverity(string s) => s.ToLowerInvariant() switch
    {
        "bug" => Severity.Bug,
        "warning" => Severity.Warning,
        "suggestion" => Severity.Suggestion,
        "info" => Severity.Info,
        "deadcode" or "dead_code" or "dead-code" => Severity.DeadCode,
        _ => Severity.Info
    };

    private static ContextRequestType ParseContextRequestType(string s) => s.ToLowerInvariant() switch
    {
        "file" => ContextRequestType.File,
        "class" => ContextRequestType.Class,
        "method" => ContextRequestType.Method,
        "callers_of" or "callers-of" => ContextRequestType.CallersOf,
        "callees_of" or "callees-of" => ContextRequestType.CalleesOf,
        "search_code" or "search-code" => ContextRequestType.SearchCode,
        "oracle_object" or "oracle-object" or "object" or "table" or "view" or "procedure" or "package" or "function"
            => ContextRequestType.OracleObject,
        _ => ContextRequestType.File,
    };
}
