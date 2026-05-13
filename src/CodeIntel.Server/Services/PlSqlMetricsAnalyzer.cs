using Antlr4.Runtime;
using CodeIntel.Server.Grammar;
using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services;

public interface IPlSqlMetricsAnalyzer
{
    FileMetricsResult Analyze(string filePath, string relativePath, string content);
}

/// <summary>
/// Per-routine PL/SQL metrics extracted by walking the lexer token stream produced by
/// <see cref="PlSqlRefsLexer"/>. We deliberately do NOT parse — full PL/SQL parsing
/// would require pulling in the ~10k-line antlr/grammars-v4 grammar. The narrow
/// grammar at <see cref="PlSqlRefs.g4"/> tokenizes strings/comments/quoted identifiers
/// correctly and tags the keywords we care about; a state-machine token walker is
/// enough for the structural metrics we want.
///
/// Limitations to note for the report:
///   - Nested PROCEDURE/FUNCTION declarations are extracted as separate routines AND
///     their tokens count toward the outer routine's complexity. Real LOB code rarely
///     uses deeply-nested local subroutines, so this overlap is acceptable for v1.
///   - Parameter count is approximated as 1 + top-level commas in the first
///     parenthesised group after the routine name. Parameter syntax (IN/OUT/DEFAULT)
///     is not modeled.
/// </summary>
public class PlSqlMetricsAnalyzer : IPlSqlMetricsAnalyzer
{
    private const int LP = 2; // T__1 — '('
    private const int RP = 3; // T__2 — ')'

    public FileMetricsResult Analyze(string filePath, string relativePath, string content)
    {
        if (string.IsNullOrEmpty(content))
            return new FileMetricsResult(filePath, relativePath, Language.Sql, 0, []);

        IList<IToken> tokens;
        try
        {
            var input = new AntlrInputStream(content);
            var lexer = new PlSqlRefsLexer(input);
            lexer.RemoveErrorListeners();
            tokens = lexer.GetAllTokens();
        }
        catch (Exception ex)
        {
            return new FileMetricsResult(filePath, relativePath, Language.Sql, 0, [], $"lex failed: {ex.Message}");
        }

        var totalLines = content.Count(c => c == '\n') + 1;

        var methods = new List<MethodMetric>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Type == PlSqlRefsLexer.PROCEDURE || t.Type == PlSqlRefsLexer.FUNCTION)
            {
                var routine = TryExtractRoutine(tokens, i);
                if (routine != null) methods.Add(routine);
                // Do NOT advance past the routine body: nested PROCEDURE/FUNCTION
                // should be picked up by the next outer-loop iteration.
            }
        }

        return new FileMetricsResult(filePath, relativePath, Language.Sql, totalLines, methods);
    }

    private static MethodMetric? TryExtractRoutine(IList<IToken> tokens, int startIdx)
    {
        var keyword = tokens[startIdx];
        var startLine = keyword.Line;

        // 1) Get the routine name: skip dots for schema.proc, take the last IDENT before `(`/`IS`/`AS`/`RETURN`/`;`.
        var idx = startIdx + 1;
        string? name = null;
        while (idx < tokens.Count)
        {
            var tk = tokens[idx];
            if (tk.Type == PlSqlRefsLexer.IDENT || tk.Type == PlSqlRefsLexer.QUOTED_IDENT)
            {
                name = Unquote(tk.Text);
            }
            else if (tk.Type == LP
                  || tk.Type == PlSqlRefsLexer.IS_KW
                  || tk.Type == PlSqlRefsLexer.AS_KW
                  || tk.Type == PlSqlRefsLexer.RETURN_KW
                  || (tk.Type == PlSqlRefsLexer.PUNCT && tk.Text == ";"))
            {
                break;
            }
            idx++;
        }
        if (name == null) return null;

        // 2) Parse parameter list (optional).
        var parameterCount = 0;
        if (idx < tokens.Count && tokens[idx].Type == LP)
        {
            (parameterCount, idx) = ScanParameters(tokens, idx);
        }

        // 3) Walk forward looking for IS_KW / AS_KW (body start) or `;` (spec-only declaration).
        var hasBody = false;
        while (idx < tokens.Count)
        {
            var tk = tokens[idx];
            if (tk.Type == PlSqlRefsLexer.IS_KW || tk.Type == PlSqlRefsLexer.AS_KW)
            {
                hasBody = true;
                idx++;
                break;
            }
            if (tk.Type == PlSqlRefsLexer.PUNCT && tk.Text == ";")
            {
                // Spec-only declaration — no body to measure. Emit a tiny stub so the user
                // sees the signature but with zero complexity / length.
                return new MethodMetric(
                    Name: name, Container: "(spec)",
                    StartLine: startLine, EndLine: tk.Line,
                    LengthLines: tk.Line - startLine + 1,
                    CyclomaticComplexity: 1,
                    NestingDepth: 0,
                    ParameterCount: parameterCount,
                    EmptyCatchCount: 0,
                    SyncOverAsyncCount: 0,
                    CursorDeclarationCount: 0,
                    ExceptionHandlerCount: 0,
                    SwallowedWhenOthersCount: 0,
                    Flags: ["spec-only"]);
            }
            idx++;
        }
        if (!hasBody) return null;

        // 4) Walk body: from end of IS_KW/AS_KW until matching outer END_KW.
        //    depth starts at 0 (we're in declaration section); BEGIN_KW => depth++; END_KW => depth--.
        //    Routine ends when an END_KW brings depth back to 0 (the outer BEGIN-END's END).
        var depth = 0;
        var seenBegin = false;
        var inExceptionSection = false;
        int cursorCount = 0;
        int handlerCount = 0;
        int swallowedCount = 0;
        int branchCount = 0;     // contributes to cyclomatic
        int maxDepth = 0;
        int endLine = startLine;

        for (; idx < tokens.Count; idx++)
        {
            var tk = tokens[idx];
            endLine = tk.Line;

            switch (tk.Type)
            {
                case PlSqlRefsLexer.BEGIN_KW:
                    depth++;
                    seenBegin = true;
                    if (depth > maxDepth) maxDepth = depth;
                    inExceptionSection = false; // a nested BEGIN resets the exception state
                    break;

                case PlSqlRefsLexer.END_KW:
                    depth--;
                    if (seenBegin && depth <= 0)
                    {
                        endLine = tk.Line;
                        goto done;
                    }
                    inExceptionSection = false;
                    break;

                case PlSqlRefsLexer.EXCEPTION:
                    // EXCEPTION may also introduce a declaration (e.g., `my_err EXCEPTION;`).
                    // The handler-section EXCEPTION appears AFTER a BEGIN at the current depth.
                    if (seenBegin && depth >= 1)
                        inExceptionSection = true;
                    break;

                case PlSqlRefsLexer.CURSOR:
                    if (depth == 0) cursorCount++;
                    break;

                case PlSqlRefsLexer.WHEN:
                    if (inExceptionSection)
                    {
                        handlerCount++;
                        // Look ahead for "OTHERS THEN NULL ;" sequence to flag swallowing.
                        if (IsWhenOthersNull(tokens, idx))
                            swallowedCount++;
                    }
                    else
                    {
                        // WHEN inside CASE — counts as a branch.
                        branchCount++;
                    }
                    break;

                case PlSqlRefsLexer.IF_KW:
                case PlSqlRefsLexer.ELSIF_KW:
                case PlSqlRefsLexer.LOOP_KW:
                case PlSqlRefsLexer.WHILE_KW:
                case PlSqlRefsLexer.FOR_KW:
                case PlSqlRefsLexer.CASE_KW:
                case PlSqlRefsLexer.AND_KW:
                case PlSqlRefsLexer.OR_KW:
                    branchCount++;
                    break;
            }
        }
        // Stream ended without a closing END_KW — accept what we have.

        done:

        var cyclomatic = 1 + branchCount;
        var lengthLines = endLine - startLine + 1;

        var flags = new List<string>();
        if (cyclomatic     >= 10) flags.Add("high-complexity");
        if (lengthLines    >= 50) flags.Add("long");
        if (swallowedCount > 0)   flags.Add("swallowed-when-others");
        if (cursorCount    >= 3)  flags.Add("many-cursors");
        if (maxDepth       >= 4)  flags.Add("deep-nesting");
        if (parameterCount >= 6)  flags.Add("many-params");

        return new MethodMetric(
            Name: name,
            Container: keyword.Type == PlSqlRefsLexer.FUNCTION ? "function" : "procedure",
            StartLine: startLine,
            EndLine: endLine,
            LengthLines: lengthLines,
            CyclomaticComplexity: cyclomatic,
            NestingDepth: maxDepth,
            ParameterCount: parameterCount,
            EmptyCatchCount: 0,
            SyncOverAsyncCount: 0,
            CursorDeclarationCount: cursorCount,
            ExceptionHandlerCount: handlerCount,
            SwallowedWhenOthersCount: swallowedCount,
            Flags: flags);
    }

    private static (int paramCount, int newIdx) ScanParameters(IList<IToken> tokens, int lparenIdx)
    {
        var depth = 1;
        var idx = lparenIdx + 1;
        var commas = 0;
        var sawAnyContent = false;

        while (idx < tokens.Count && depth > 0)
        {
            var tk = tokens[idx];
            if (tk.Type == LP) depth++;
            else if (tk.Type == RP) depth--;
            else if (depth == 1 && tk.Type == PlSqlRefsLexer.PUNCT && tk.Text == ",") commas++;
            else if (tk.Type != PlSqlRefsLexer.WS) sawAnyContent = true;
            idx++;
        }

        var paramCount = sawAnyContent ? commas + 1 : 0;
        return (paramCount, idx);
    }

    // Heuristic: WHEN OTHERS THEN NULL [;]  — possibly with whitespace tokens between.
    private static bool IsWhenOthersNull(IList<IToken> tokens, int whenIdx)
    {
        int j = whenIdx + 1;
        // OTHERS
        if (j >= tokens.Count || tokens[j].Type != PlSqlRefsLexer.OTHERS) return false;
        j++;
        // THEN
        if (j >= tokens.Count || tokens[j].Type != PlSqlRefsLexer.THEN) return false;
        j++;
        // NULL
        return j < tokens.Count && tokens[j].Type == PlSqlRefsLexer.NULL_KW;
    }

    private static string Unquote(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"'
            ? text[1..^1].Replace("\"\"", "\"")
            : text;
}
