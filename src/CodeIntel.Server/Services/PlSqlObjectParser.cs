using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using CodeIntel.Server.Grammar;
using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services;

public interface IPlSqlObjectParser
{
    ParsedObjectReferences Parse(string source);
}

/// <summary>
/// ANTLR-backed PL/SQL object-reference extractor. Tokenization (strings,
/// comments, quoted identifiers, schema-qualified names) is handled by the
/// generated <see cref="PlSqlRefsLexer"/>; structural extraction is done by
/// <see cref="ObjectRefVisitor"/> walking the parse tree produced by
/// <see cref="PlSqlRefsParser"/>.
///
/// This replaces a regex-based predecessor that mis-classified references
/// inside comments / string literals and tripped over multi-line statements.
/// The grammar lives at <c>Grammar/PlSqlRefs.g4</c>; build-time codegen is
/// handled by the <c>Antlr4BuildTasks</c> NuGet package.
/// </summary>
public class PlSqlObjectParser : IPlSqlObjectParser
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Keywords / pseudo-tables that can appear in FROM / INTO / etc. but aren't
        // real user objects we want to resolve.
        "dual", "sys", "table", "the", "lateral", "json_table", "xmltable",
        // Common token after EXEC in scripts.
        "immediate",
    };

    public ParsedObjectReferences Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return ParsedObjectReferences.Empty;

        var input = new AntlrInputStream(source);
        var lexer = new PlSqlRefsLexer(input);
        lexer.RemoveErrorListeners();
        var tokens = new CommonTokenStream(lexer);
        var parser = new PlSqlRefsParser(tokens);
        // PL/SQL we don't fully grammar-cover will produce error nodes; silence them.
        parser.RemoveErrorListeners();
        parser.ErrorHandler = new BailingErrorStrategy();

        IParseTree tree;
        try { tree = parser.script(); }
        catch (Antlr4.Runtime.Misc.ParseCanceledException) { return ParsedObjectReferences.Empty; }

        var visitor = new ObjectRefVisitor(StopWords);
        visitor.Visit(tree);

        return new ParsedObjectReferences(
            Tables:   visitor.Tables  .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            Routines: visitor.Routines.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            Packages: visitor.Packages.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList()
        );
    }

    /// <summary>
    /// Falls back to <see cref="DefaultErrorStrategy"/> on recovery rather than
    /// throwing, because we want partial extraction even when the input contains
    /// PL/SQL syntax we don't grammar-cover (DECLARE blocks, CREATE OR REPLACE
    /// statements, etc.).
    /// </summary>
    private sealed class BailingErrorStrategy : DefaultErrorStrategy
    {
        public override void ReportError(Antlr4.Runtime.Parser recognizer, RecognitionException e) { /* swallow */ }
        public override void Recover(Antlr4.Runtime.Parser recognizer, RecognitionException e)
        {
            // Skip one token and continue — default behavior, but without the noise.
            recognizer.Consume();
        }
    }

    private sealed class ObjectRefVisitor : PlSqlRefsBaseVisitor<object?>
    {
        public HashSet<string> Tables   { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Routines { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _stop;

        public ObjectRefVisitor(HashSet<string> stop) => _stop = stop;

        public override object? VisitFromRef(PlSqlRefsParser.FromRefContext c)   { AddTable(c.qualifiedName()); return null; }
        public override object? VisitJoinRef(PlSqlRefsParser.JoinRefContext c)   { AddTable(c.qualifiedName()); return null; }
        public override object? VisitIntoRef(PlSqlRefsParser.IntoRefContext c)   { AddTable(c.qualifiedName()); return null; }
        public override object? VisitUpdateRef(PlSqlRefsParser.UpdateRefContext c) { AddTable(c.qualifiedName()); return null; }
        public override object? VisitDeleteRef(PlSqlRefsParser.DeleteRefContext c) { AddTable(c.qualifiedName()); return null; }
        public override object? VisitMergeRef(PlSqlRefsParser.MergeRefContext c)   { AddTable(c.qualifiedName()); return null; }
        public override object? VisitUsingRef(PlSqlRefsParser.UsingRefContext c)   { AddTable(c.qualifiedName()); return null; }

        public override object? VisitExecCall(PlSqlRefsParser.ExecCallContext c)
        {
            AddRoutine(c.qualifiedName());
            return null;
        }

        public override object? VisitPkgProcCall(PlSqlRefsParser.PkgProcCallContext c)
        {
            // pkgProcCall: IDENT '.' IDENT '(' — both identifiers are direct children.
            var idents = c.IDENT();
            if (idents.Length >= 2)
            {
                var pkg  = Unquote(idents[0].GetText());
                var proc = Unquote(idents[1].GetText());
                if (!IsStop(pkg))  Packages.Add(pkg);
                if (!IsStop(proc)) Routines.Add(proc);
            }
            return null;
        }

        private void AddTable(PlSqlRefsParser.QualifiedNameContext? ctx)
        {
            var name = ExtractTail(ctx);
            if (name != null && !IsStop(name)) Tables.Add(name);
        }

        private void AddRoutine(PlSqlRefsParser.QualifiedNameContext? ctx)
        {
            var name = ExtractTail(ctx);
            if (name != null && !IsStop(name)) Routines.Add(name);
        }

        // The unqualified tail of `schema.name` (or just `name`).
        private static string? ExtractTail(PlSqlRefsParser.QualifiedNameContext? ctx)
        {
            if (ctx is null) return null;
            // Each side is either IDENT or QUOTED_IDENT — they're alternatives in the same slot.
            var idents = ctx.IDENT();
            var quoted = ctx.QUOTED_IDENT();
            var tokens = new List<IToken>();
            foreach (var t in idents) tokens.Add(t.Symbol);
            foreach (var t in quoted) tokens.Add(t.Symbol);
            if (tokens.Count == 0) return null;
            tokens.Sort((a, b) => a.StartIndex.CompareTo(b.StartIndex));
            return Unquote(tokens[^1].Text);
        }

        private bool IsStop(string name) => _stop.Contains(name);

        private static string Unquote(string text) =>
            text.Length >= 2 && text[0] == '"' && text[^1] == '"'
                ? text[1..^1].Replace("\"\"", "\"")
                : text;
    }
}
