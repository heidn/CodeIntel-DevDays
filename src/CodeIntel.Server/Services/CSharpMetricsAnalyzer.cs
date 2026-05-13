using CodeIntel.Server.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeIntel.Server.Services;

public interface ICSharpMetricsAnalyzer
{
    FileMetricsResult Analyze(string filePath, string relativePath, string content);
}

/// <summary>
/// Per-method structural metrics extracted from a C# file via Roslyn syntax walking.
/// No semantic model needed — every metric is purely syntactic, which keeps this
/// analyzer fast (no project compilation) and runnable on raw file content without
/// loading the surrounding solution.
///
/// Metrics computed (see <see cref="MethodMetric"/>):
///   - LengthLines             — end line − start line + 1
///   - CyclomaticComplexity    — 1 + count of branching constructs in the body
///   - NestingDepth            — deepest nested block within the body
///   - ParameterCount          — count of formal parameters
///   - EmptyCatchCount         — `catch` clauses with a body containing zero statements
///   - SyncOverAsyncCount      — `.Result`, `.Wait()`, `.GetAwaiter().GetResult()` invocations
/// </summary>
public class CSharpMetricsAnalyzer : ICSharpMetricsAnalyzer
{
    public FileMetricsResult Analyze(string filePath, string relativePath, string content)
    {
        SyntaxTree tree;
        try
        {
            tree = CSharpSyntaxTree.ParseText(content, path: filePath);
        }
        catch (Exception ex)
        {
            return new FileMetricsResult(filePath, relativePath, Language.CSharp, 0, [], $"parse failed: {ex.Message}");
        }

        var root = tree.GetRoot();
        var totalLines = tree.GetText().Lines.Count;

        var methods = new List<MethodMetric>();
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case MethodDeclarationSyntax m:
                    methods.Add(BuildMetric(m.Identifier.Text, ContainerName(m), m, m.ParameterList?.Parameters.Count ?? 0, m.Body, m.ExpressionBody));
                    break;
                case ConstructorDeclarationSyntax c:
                    methods.Add(BuildMetric(c.Identifier.Text, ContainerName(c), c, c.ParameterList?.Parameters.Count ?? 0, c.Body, c.ExpressionBody));
                    break;
                case LocalFunctionStatementSyntax l:
                    methods.Add(BuildMetric(l.Identifier.Text, ContainerName(l), l, l.ParameterList?.Parameters.Count ?? 0, l.Body, l.ExpressionBody));
                    break;
            }
        }

        return new FileMetricsResult(filePath, relativePath, Language.CSharp, totalLines, methods);
    }

    private static MethodMetric BuildMetric(
        string name, string container, SyntaxNode methodNode, int parameterCount,
        BlockSyntax? body, ArrowExpressionClauseSyntax? expressionBody)
    {
        var span = methodNode.GetLocation().GetLineSpan();
        var startLine = span.StartLinePosition.Line + 1;
        var endLine   = span.EndLinePosition.Line   + 1;

        var bodyNode = (SyntaxNode?)body ?? expressionBody;

        int complexity = 1;
        int emptyCatch = 0;
        int syncOverAsync = 0;
        int nestingDepth = 0;

        if (bodyNode != null)
        {
            complexity    = ComputeCyclomatic(bodyNode);
            emptyCatch    = CountEmptyCatch(bodyNode);
            syncOverAsync = CountSyncOverAsync(bodyNode);
            nestingDepth  = ComputeNestingDepth(bodyNode);
        }

        var flags = new List<string>();
        if (complexity   >= 10) flags.Add("high-complexity");
        if (endLine - startLine + 1 >= 50) flags.Add("long");
        if (emptyCatch   > 0)   flags.Add("empty-catch");
        if (syncOverAsync > 0)  flags.Add("sync-over-async");
        if (parameterCount >= 6) flags.Add("many-params");
        if (nestingDepth >= 4)  flags.Add("deep-nesting");

        return new MethodMetric(
            Name: name,
            Container: container,
            StartLine: startLine,
            EndLine: endLine,
            LengthLines: endLine - startLine + 1,
            CyclomaticComplexity: complexity,
            NestingDepth: nestingDepth,
            ParameterCount: parameterCount,
            EmptyCatchCount: emptyCatch,
            SyncOverAsyncCount: syncOverAsync,
            CursorDeclarationCount: null,
            ExceptionHandlerCount: null,
            SwallowedWhenOthersCount: null,
            Flags: flags);
    }

    private static string ContainerName(SyntaxNode node)
    {
        for (var parent = node.Parent; parent != null; parent = parent.Parent)
        {
            switch (parent)
            {
                case ClassDeclarationSyntax c:     return c.Identifier.Text;
                case RecordDeclarationSyntax r:    return r.Identifier.Text;
                case StructDeclarationSyntax s:    return s.Identifier.Text;
                case InterfaceDeclarationSyntax i: return i.Identifier.Text;
            }
        }
        return "(top-level)";
    }

    // Standard cyclomatic: 1 + (branches & predicates). A `case` clause counts; `default` does not.
    // Logical && / || in conditions add 1 each (McCabe's "modified" definition). `??` and `??=`
    // also branch and are counted.
    private static int ComputeCyclomatic(SyntaxNode body)
    {
        var count = 1;
        foreach (var node in body.DescendantNodes())
        {
            switch (node)
            {
                case IfStatementSyntax:
                case WhileStatementSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case ForEachVariableStatementSyntax:
                case DoStatementSyntax:
                case ConditionalExpressionSyntax: // ternary
                case CaseSwitchLabelSyntax:
                case CasePatternSwitchLabelSyntax:
                case SwitchExpressionArmSyntax:
                case CatchClauseSyntax:
                    count++;
                    break;
                case BinaryExpressionSyntax bin when
                    bin.OperatorToken.IsKind(SyntaxKind.AmpersandAmpersandToken)
                    || bin.OperatorToken.IsKind(SyntaxKind.BarBarToken)
                    || bin.OperatorToken.IsKind(SyntaxKind.QuestionQuestionToken):
                    count++;
                    break;
                case AssignmentExpressionSyntax assign when
                    assign.OperatorToken.IsKind(SyntaxKind.QuestionQuestionEqualsToken):
                    count++;
                    break;
            }
        }
        return count;
    }

    private static int CountEmptyCatch(SyntaxNode body) =>
        body.DescendantNodes()
            .OfType<CatchClauseSyntax>()
            .Count(c => c.Block.Statements.Count == 0);

    private static int CountSyncOverAsync(SyntaxNode body)
    {
        var count = 0;
        foreach (var member in body.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var name = member.Name.Identifier.Text;
            if (name == "Result")
            {
                // Heuristic: only flag when used as a value (read), which is the common deadlock pattern.
                count++;
                continue;
            }
            if (name == "Wait" && member.Parent is InvocationExpressionSyntax)
            {
                count++;
            }
        }
        // `.GetAwaiter().GetResult()` chain — invocation of GetResult on a GetAwaiter result.
        foreach (var inv in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is MemberAccessExpressionSyntax m
                && m.Name.Identifier.Text == "GetResult"
                && m.Expression is InvocationExpressionSyntax inner
                && inner.Expression is MemberAccessExpressionSyntax innerM
                && innerM.Name.Identifier.Text == "GetAwaiter")
            {
                count++;
            }
        }
        return count;
    }

    private static int ComputeNestingDepth(SyntaxNode body)
    {
        // Depth = the deepest chain of "scope-introducing" statements within the body.
        // We compute depth at each leaf descendant and take the max.
        var max = 0;
        foreach (var node in body.DescendantNodes())
        {
            if (!IsScopeIntroducer(node)) continue;
            var depth = 0;
            var current = node;
            while (current != body && current != null)
            {
                if (IsScopeIntroducer(current)) depth++;
                current = current.Parent;
            }
            if (depth > max) max = depth;
        }
        return max;
    }

    private static bool IsScopeIntroducer(SyntaxNode node) => node is
        IfStatementSyntax or ElseClauseSyntax or
        ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax or
        WhileStatementSyntax or DoStatementSyntax or
        TryStatementSyntax or CatchClauseSyntax or FinallyClauseSyntax or
        SwitchStatementSyntax or SwitchSectionSyntax or
        LockStatementSyntax or UsingStatementSyntax or FixedStatementSyntax;
}
