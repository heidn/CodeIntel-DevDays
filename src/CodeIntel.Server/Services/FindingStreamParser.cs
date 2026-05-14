using System.Text;
using System.Text.Json;
using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services;

/// <summary>
/// Stateful streaming parser. Feed tokens in as they arrive; emits findings incrementally.
/// Also collects context requests for the agentic loop — these are read after the stream ends.
///
/// Implementation: single-pass scanner that only inspects the newly-appended chunk + a small
/// tail window for partial-tag detection. Prior versions re-ran a regex on the full buffer
/// every token, which was O(n²) over the stream.
/// </summary>
public class FindingStreamParser
{
    private const string FindingOpen   = "<finding>";
    private const string FindingClose  = "</finding>";
    private const string RequestOpen   = "<request_context";
    private const string RequestClose  = "</request_context>";
    private const string DoneMarker    = "<done />";

    private readonly StringBuilder _buffer = new();
    private readonly List<Finding> _findings = new();
    private readonly List<ContextRequest> _contextRequests = new();
    private readonly List<ParseFailure> _malformed = new();

    // Forward scan index: everything before this position has been consumed.
    private int _scanPos;
    // Count of unclosed <finding> opens we've seen (open - close, excluding completed pairs).
    private int _openFindingCount;
    private int _completedOpenCount;
    // When >= 0, we've consumed a `<finding>` opener at this position but its
    // `</finding>` closer hasn't streamed in yet. The next Append must resume
    // looking for the closer from past the opener body, NOT scan for the next
    // opener (which would orphan the pending finding forever).
    private int _pendingOpenBodyStart = -1;

    public IReadOnlyList<Finding> Findings => _findings;
    public IReadOnlyList<ContextRequest> ContextRequests => _contextRequests;
    public IReadOnlyList<ParseFailure> MalformedFindings => _malformed;
    public string RawOutput => _buffer.ToString();
    public bool IsDone { get; private set; }

    /// <summary>
    /// Number of <c>&lt;finding&gt;</c> openings that never received a matching closing tag.
    /// Computed incrementally as the stream is consumed.
    /// </summary>
    public int IncompleteFindingCount => Math.Max(0, _openFindingCount - _completedOpenCount);

    public record ParseFailure(string Snippet, string Error);

    /// <summary>
    /// Append a chunk of streaming text. Returns any newly-completed findings.
    /// Context requests are accumulated silently and read via ContextRequests after the stream ends.
    /// </summary>
    public IEnumerable<Finding> Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return Array.Empty<Finding>();

        _buffer.Append(chunk);
        var emitted = new List<Finding>();

        // If we already saw a `<finding>` opener but its closer hadn't streamed in
        // yet, look for the closer FIRST. Without this, the scanner below would
        // resume from past the opener and never re-match it, orphaning every
        // multi-line finding the model emits across multiple stream chunks.
        if (_pendingOpenBodyStart >= 0)
        {
            var closeIdx = IndexOf(FindingClose, _pendingOpenBodyStart);
            if (closeIdx < 0) return emitted;

            _completedOpenCount++;
            var json = Slice(_pendingOpenBodyStart, closeIdx).Trim();
            var (finding, error) = TryParseFinding(json);
            if (finding != null)
            {
                _findings.Add(finding);
                emitted.Add(finding);
            }
            else
            {
                _malformed.Add(new ParseFailure(Truncate(json, 200), error ?? "(unknown)"));
            }
            _scanPos = closeIdx + FindingClose.Length;
            _pendingOpenBodyStart = -1;
        }

        while (true)
        {
            // What's the nearest interesting marker from _scanPos forward?
            var openF    = IndexOf(FindingOpen,   _scanPos);
            var openR    = IndexOf(RequestOpen,   _scanPos);
            var doneIdx  = IndexOf(DoneMarker,    _scanPos);

            var next = MinNonNegative(openF, openR, doneIdx);
            if (next < 0) break;

            // <done /> always wins if it's the earliest marker — model is signaling
            // completion and won't emit more tags.
            if (next == doneIdx)
            {
                IsDone = true;
                _scanPos = doneIdx + DoneMarker.Length;
                continue;
            }

            if (next == openF)
            {
                _openFindingCount++;
                var bodyStart = openF + FindingOpen.Length;
                var closeIdx = IndexOf(FindingClose, bodyStart);
                if (closeIdx < 0)
                {
                    // Closing tag hasn't streamed in yet. Remember the body start so
                    // the next Append resumes from here looking for the closer.
                    _pendingOpenBodyStart = bodyStart;
                    _scanPos = bodyStart;
                    break;
                }

                _completedOpenCount++;
                var json = Slice(bodyStart, closeIdx).Trim();
                var (finding, error) = TryParseFinding(json);
                if (finding != null)
                {
                    _findings.Add(finding);
                    emitted.Add(finding);
                }
                else
                {
                    _malformed.Add(new ParseFailure(Truncate(json, 200), error ?? "(unknown)"));
                }
                _scanPos = closeIdx + FindingClose.Length;
                continue;
            }

            // <request_context type="...">...</request_context>
            if (next == openR)
            {
                var attrStart = openR + RequestOpen.Length;
                var openTagEnd = IndexOf(">", attrStart);
                if (openTagEnd < 0)
                {
                    _scanPos = openR;
                    break;
                }
                var closeIdx = IndexOf(RequestClose, openTagEnd + 1);
                if (closeIdx < 0)
                {
                    _scanPos = openR;
                    break;
                }
                var attrs = Slice(attrStart, openTagEnd);
                var target = Slice(openTagEnd + 1, closeIdx).Trim();
                var typeStr = ExtractTypeAttribute(attrs);
                _contextRequests.Add(new ContextRequest(ParseContextRequestType(typeStr), target));
                _scanPos = closeIdx + RequestClose.Length;
                continue;
            }
        }

        return emitted;
    }

    private int IndexOf(string needle, int from)
    {
        if (from >= _buffer.Length) return -1;
        // StringBuilder.ToString() over a small window — cheaper than materializing the
        // whole buffer. For typical token chunks this stays in the low hundreds of bytes.
        return _buffer.ToString().IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
    }

    private string Slice(int start, int endExclusive) =>
        _buffer.ToString(start, endExclusive - start);

    private static int MinNonNegative(params int[] values)
    {
        var min = -1;
        foreach (var v in values)
        {
            if (v < 0) continue;
            if (min < 0 || v < min) min = v;
        }
        return min;
    }

    private static string ExtractTypeAttribute(string attrs)
    {
        // attrs is the chunk between `<request_context` and `>` — e.g. ` type="file"`.
        // Tolerate single or double quotes and surrounding whitespace.
        var idx = attrs.IndexOf("type", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        var eq = attrs.IndexOf('=', idx);
        if (eq < 0) return "";
        var quote = -1;
        for (var i = eq + 1; i < attrs.Length; i++)
        {
            var c = attrs[i];
            if (c == '"' || c == '\'') { quote = i; break; }
            if (!char.IsWhiteSpace(c)) break;
        }
        if (quote < 0) return "";
        var endQuote = attrs.IndexOf(attrs[quote], quote + 1);
        if (endQuote < 0) return "";
        return attrs.Substring(quote + 1, endQuote - quote - 1);
    }

    private static (Finding? finding, string? error) TryParseFinding(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var severityStr = GetString(root, "severity") ?? "info";
            var severity = ParseSeverity(severityStr);
            var confidence = ParseConfidence(GetString(root, "confidence"));

            var finding = new Finding(
                Severity: severity,
                Title: GetString(root, "title") ?? "(no title)",
                Description: GetString(root, "description") ?? "",
                FilePath: GetString(root, "filePath"),
                LineNumber: GetInt(root, "lineNumber"),
                CodeSnippet: GetString(root, "codeSnippet"),
                Confidence: confidence
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

    // Default to High when the model omits the field — keeps old prompts / cached outputs working.
    private static Confidence ParseConfidence(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "low" => Confidence.Low,
        _ => Confidence.High,
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
