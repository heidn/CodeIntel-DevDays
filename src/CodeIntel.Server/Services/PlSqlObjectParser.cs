using System.Text.RegularExpressions;
using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services;

public interface IPlSqlObjectParser
{
    ParsedObjectReferences Parse(string source);
}

/// <summary>
/// Heuristic extractor for PL/SQL object references. Strips comments + string literals,
/// then runs anchored regex against DML keywords and package.proc invocation syntax.
/// Not a full PL/SQL parser — false positives/negatives are accepted; the local LLM is
/// the consumer and the briefing-officer architecture tolerates noise.
/// </summary>
public class PlSqlObjectParser : IPlSqlObjectParser
{
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.IgnoreCase;

    // PL/SQL identifier (optionally schema-qualified). $ # _ are valid in Oracle identifiers.
    // Quoted identifiers "Like This" are not handled — rare in stored-proc bodies.
    private const string IdPattern = @"(?:[A-Za-z][A-Za-z0-9_$#]*)";
    private const string QualifiedPattern = @"(?:" + IdPattern + @"\.)?" + IdPattern;

    // Table-position keywords. The trailing group captures the (possibly-qualified) name.
    private static readonly Regex TableFrom    = new($@"\bFROM\s+({QualifiedPattern})", Opts);
    private static readonly Regex TableJoin    = new($@"\bJOIN\s+({QualifiedPattern})", Opts);
    private static readonly Regex TableInto    = new($@"\bINTO\s+({QualifiedPattern})", Opts);
    private static readonly Regex TableUpdate  = new($@"\bUPDATE\s+({QualifiedPattern})", Opts);
    private static readonly Regex TableDelete  = new($@"\bDELETE\s+(?:FROM\s+)?({QualifiedPattern})", Opts);
    private static readonly Regex TableMerge   = new($@"\bMERGE\s+INTO\s+({QualifiedPattern})", Opts);
    private static readonly Regex TableUsing   = new($@"\bUSING\s+({QualifiedPattern})", Opts);

    // Routine invocations: explicit EXECUTE / CALL, or package.proc(...) pattern.
    private static readonly Regex ExplicitCall = new($@"\b(?:EXEC(?:UTE)?|CALL)\s+({QualifiedPattern})", Opts);
    private static readonly Regex QualifiedCall = new($@"\b({IdPattern})\.({IdPattern})\s*\(", Opts);

    // Stop-words that should never be treated as object names if they slip through (PL/SQL keywords +
    // common builtins). Lowercased; compared case-insensitively.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Keywords that can appear immediately after FROM/INTO/JOIN/UPDATE/DELETE in syntax we don't care about.
        "dual", "sys", "table", "the", "lateral", "json_table", "xmltable",
        // Common builtins that follow EXEC in scripts but aren't user objects.
        "immediate",
    };

    public ParsedObjectReferences Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return ParsedObjectReferences.Empty;

        var cleaned = StripCommentsAndStrings(source);

        var tables   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddTable(string raw)
        {
            var name = NormalizeName(raw);
            if (name != null && !StopWords.Contains(name)) tables.Add(name);
        }

        void AddRoutine(string raw)
        {
            var name = NormalizeName(raw);
            if (name != null && !StopWords.Contains(name)) routines.Add(name);
        }

        foreach (var rx in new[] { TableFrom, TableJoin, TableInto, TableUpdate, TableDelete, TableMerge, TableUsing })
            foreach (Match m in rx.Matches(cleaned))
                AddTable(m.Groups[1].Value);

        foreach (Match m in ExplicitCall.Matches(cleaned))
            AddRoutine(m.Groups[1].Value);

        // package.proc(...) — capture both the package and the proc.
        foreach (Match m in QualifiedCall.Matches(cleaned))
        {
            var pkg = m.Groups[1].Value;
            var proc = m.Groups[2].Value;
            if (!StopWords.Contains(pkg)) packages.Add(pkg);
            if (!StopWords.Contains(proc)) routines.Add(proc);
        }

        return new ParsedObjectReferences(
            Tables:   tables.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            Routines: routines.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            Packages: packages.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList()
        );
    }

    private static string? NormalizeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Trim any trailing punctuation the regex may have grabbed.
        raw = raw.Trim().TrimEnd(',', ';', ')', '(');
        // Strip schema prefix: SCHEMA.NAME -> NAME.
        var dot = raw.LastIndexOf('.');
        if (dot >= 0 && dot < raw.Length - 1) raw = raw[(dot + 1)..];
        return raw.Length > 0 ? raw : null;
    }

    // PL/SQL line comments (-- ...), block comments (/* ... */), and single-quoted strings.
    // Strings can contain '' as an escape; we collapse them to a placeholder rather than the
    // literal content so reference-extraction doesn't pull names out of dynamic SQL strings
    // (those are a future enhancement).
    private static readonly Regex CommentsAndStrings = new(
        @"(--[^\r\n]*)|(/\*[\s\S]*?\*/)|('(?:''|[^'])*')",
        RegexOptions.Compiled);

    private static string StripCommentsAndStrings(string source) =>
        CommentsAndStrings.Replace(source, m => new string(' ', m.Length));
}
