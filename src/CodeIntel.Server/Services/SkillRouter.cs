using System.Text.RegularExpressions;
using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services;

/// <summary>
/// Lightweight content-keyed skill router. When the assembled context matches one
/// or more known patterns (async hot spots, raw SQL, HttpClient usage, etc.) the
/// router contributes an extra paragraph to the system prompt that biases the
/// model toward the relevant class of issues.
///
/// This is the seed of the planned skills system in CLAUDE.md — there are no
/// per-skill MD files yet, but the router shape lets us swap to file-backed
/// skills later without changing call sites.
/// </summary>
public interface ISkillRouter
{
    /// <summary>
    /// Returns a system-prompt addendum (or empty if nothing fires) based on what
    /// the context looks like. Examined surface: file extensions + content regex.
    /// </summary>
    string BuildAddendum(CodeContext context);

    /// <summary>
    /// Names of the skills that fired. Surfaced via SignalR status so the user
    /// understands what specialization is being applied.
    /// </summary>
    IReadOnlyList<string> RouteSkills(CodeContext context);
}

public class SkillRouter : ISkillRouter
{
    private record Skill(
        string Name,
        string Summary,
        Predicate<CodeContext> Matches,
        string PromptAddendum);

    private static readonly Skill[] Skills =
    {
        new(
            Name: "concurrency",
            Summary: "async/await + threading hot spots",
            Matches: ctx => HasAny(ctx, new Regex(@"\b(?:async\s+\w|await\s+|Task\.|Parallel\.|Thread\.|lock\s*\()", RegexOptions.Compiled)),
            PromptAddendum: """

                Concurrency skill: this code uses async/await and/or threading. Pay extra attention to:
                  - Missing ConfigureAwait(false) in library code
                  - Async-over-sync (Task.Result, .Wait()) deadlocks
                  - Lock objects exposed to outside callers
                  - Mutable shared state without synchronization
                """),

        new(
            Name: "raw-sql",
            Summary: "string-concatenated SQL — watch for injection",
            Matches: ctx => HasAny(ctx, new Regex(@"(?i)\b(?:SELECT|INSERT|UPDATE|DELETE|MERGE)\b.*\+.*['""]|FromSqlRaw|ExecuteSqlRaw", RegexOptions.Compiled)),
            PromptAddendum: """

                SQL skill: raw or interpolated SQL is present. Pay extra attention to:
                  - String-concatenated user input → SQL injection
                  - Missing parameterization (use FromSqlInterpolated or DbParameter)
                  - Implicit conversions in WHERE clauses on indexed columns
                """),

        new(
            Name: "http-client",
            Summary: "outbound HTTP — auth, retry, timeout",
            Matches: ctx => HasAny(ctx, new Regex(@"\b(?:HttpClient|IHttpClientFactory|RestClient|new HttpRequestMessage)\b", RegexOptions.Compiled)),
            PromptAddendum: """

                HTTP-client skill: outbound HTTP calls present. Pay extra attention to:
                  - HttpClient instantiated per request (socket exhaustion) vs. injected
                  - Missing timeout / cancellation token plumbing
                  - Bearer-token logging or echoing in error paths
                  - Missing retry policy on transient failures
                """),

        new(
            Name: "auth",
            Summary: "auth, identity, credentials",
            Matches: ctx => HasAny(ctx, new Regex(@"(?i)\b(?:Authentication|Authorize|Principal|Claim|Identity|Password|SignIn|Token|Jwt)\b", RegexOptions.Compiled)),
            PromptAddendum: """

                Auth skill: authentication / authorization surface present. Pay extra attention to:
                  - Missing [Authorize] on endpoints that mutate state
                  - Plaintext password handling or weak hashing
                  - Token persistence or logging
                  - Authorization checks that compare strings without case-normalization
                """),

        new(
            Name: "plsql-cursor",
            Summary: "PL/SQL cursor + exception patterns",
            Matches: ctx => ctx.Language == Language.Sql
                            && HasAny(ctx, new Regex(@"(?i)\b(?:CURSOR|OPEN\s+\w+|FETCH|EXCEPTION\s+WHEN|RAISE_APPLICATION_ERROR)\b", RegexOptions.Compiled)),
            PromptAddendum: """

                PL/SQL skill: cursors and exception handlers present. Pay extra attention to:
                  - Cursors that aren't closed on every exit path
                  - `EXCEPTION WHEN OTHERS THEN NULL` swallowing errors
                  - Implicit commits inside loops
                  - Row-by-row processing that should be set-based
                """),
    };

    public string BuildAddendum(CodeContext context)
    {
        var matched = Skills.Where(s => s.Matches(context)).ToList();
        if (matched.Count == 0) return "";
        return string.Concat(matched.Select(s => s.PromptAddendum));
    }

    public IReadOnlyList<string> RouteSkills(CodeContext context) =>
        Skills.Where(s => s.Matches(context)).Select(s => s.Name).ToList();

    private static bool HasAny(CodeContext context, Regex pattern)
    {
        foreach (var f in context.Files)
        {
            if (pattern.IsMatch(f.Content)) return true;
        }
        return false;
    }
}
