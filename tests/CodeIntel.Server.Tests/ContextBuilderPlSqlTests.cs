using CodeIntel.Server.Models;
using CodeIntel.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeIntel.Server.Tests;

public class ContextBuilderPlSqlTests : IAsyncLifetime
{
    private string _root = null!;
    private WorkspaceService _workspace = null!;
    private ContextBuilder _builder = null!;
    private string _workspaceId = null!;
    private string _seedFile = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "codeintel-ctx-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        // Seed file: a stored proc that touches three tables and calls a package routine.
        _seedFile = await WriteAllAsync("get_orders.sql", """
            CREATE OR REPLACE PROCEDURE get_orders AS
            BEGIN
              FOR rec IN (SELECT o.order_id, c.first_name
                          FROM   orders    o
                          JOIN   customers c ON c.customer_id = o.customer_id) LOOP
                audit_pkg.log_event('viewed: ' || rec.order_id);
              END LOOP;
            END;
            """);

        // Dependencies: matching files for two of the three table refs, plus the package.
        await WriteAllAsync("orders.sql", """
            CREATE TABLE orders (
              order_id    NUMBER(10) NOT NULL,
              customer_id NUMBER(10) NOT NULL,
              CONSTRAINT pk_orders PRIMARY KEY (order_id)
            );
            """);

        await WriteAllAsync("customers.sql", """
            CREATE TABLE customers (
              customer_id NUMBER(10)   NOT NULL,
              first_name  VARCHAR2(50) NOT NULL,
              CONSTRAINT pk_customers PRIMARY KEY (customer_id)
            );
            """);

        await WriteAllAsync("audit_pkg.pkb", """
            CREATE OR REPLACE PACKAGE BODY audit_pkg AS
              PROCEDURE log_event(p_msg VARCHAR2) IS BEGIN NULL; END;
            END audit_pkg;
            """);

        // Unrelated SQL file — should NOT show up in context unless explicitly seeded.
        await WriteAllAsync("unrelated.sql", "CREATE TABLE unrelated (id NUMBER);");

        _workspace = new WorkspaceService(Options.Create(new AnalysisOptions()), NullLogger<WorkspaceService>.Instance);
        var ws = await _workspace.LoadAsync(_root);
        _workspaceId = ws.Id;
    }

    public Task DisposeAsync()
    {
        _workspace?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private async Task<string> WriteAllAsync(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private ContextBuilder CreateBuilder(int maxTokens = 5000)
    {
        var parser   = new PlSqlObjectParser();
        var resolver = new PlSqlRepoResolver(_workspace, NullLogger<PlSqlRepoResolver>.Instance);
        var options  = Options.Create(new AnalysisOptions { MaxContextTokens = maxTokens });
        return new ContextBuilder(_workspace, parser, resolver, options, NullLogger<ContextBuilder>.Instance);
    }

    // ── Resolved-dependency appending ──────────────────────────────────────

    [Fact]
    public async Task Build_PlSqlSeed_AppendsResolvedDependencies()
    {
        _builder = CreateBuilder();
        var ctx = await _builder.BuildAsync(_workspaceId, new[] { _seedFile }, maxTokenBudget: 5000);

        // Seed + at least the 3 resolved deps (orders, customers, audit_pkg).
        Assert.True(ctx.Files.Count >= 4, $"expected ≥4 files, got {ctx.Files.Count}");

        var deps = ctx.Files.Where(f => f.IsResolvedDependency).ToList();
        Assert.Contains(deps, f => f.RelativePath.EndsWith("orders.sql",    StringComparison.OrdinalIgnoreCase));
        Assert.Contains(deps, f => f.RelativePath.EndsWith("customers.sql", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(deps, f => f.RelativePath.EndsWith("audit_pkg.pkb", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Build_PlSqlSeed_DoesNotIncludeUnrelatedFiles()
    {
        _builder = CreateBuilder();
        var ctx = await _builder.BuildAsync(_workspaceId, new[] { _seedFile }, maxTokenBudget: 5000);

        Assert.DoesNotContain(ctx.Files, f =>
            f.RelativePath.EndsWith("unrelated.sql", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Build_PlSqlSeed_SeedIsNotMarkedAsDependency()
    {
        _builder = CreateBuilder();
        var ctx = await _builder.BuildAsync(_workspaceId, new[] { _seedFile }, maxTokenBudget: 5000);

        var seed = ctx.Files.Single(f => f.FilePath.Equals(_seedFile, StringComparison.OrdinalIgnoreCase));
        Assert.False(seed.IsResolvedDependency);
    }

    // ── Dedup against seed files ───────────────────────────────────────────

    [Fact]
    public async Task Build_DependencyAlreadySeeded_IsNotDuplicated()
    {
        _builder = CreateBuilder();
        // Seed both the proc and one of the tables it references.
        var ordersPath = Path.Combine(_root, "orders.sql");
        var ctx = await _builder.BuildAsync(_workspaceId, new[] { _seedFile, ordersPath }, maxTokenBudget: 5000);

        var ordersOccurrences = ctx.Files.Count(f =>
            f.FilePath.Equals(ordersPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, ordersOccurrences);

        // The one occurrence should be the seeded (primary) version, not flagged as a dependency.
        var occurrence = ctx.Files.Single(f =>
            f.FilePath.Equals(ordersPath, StringComparison.OrdinalIgnoreCase));
        Assert.False(occurrence.IsResolvedDependency);
    }

    // ── Token budget ───────────────────────────────────────────────────────

    [Fact]
    public async Task Build_TightBudget_StopsAppendingDeps()
    {
        // Budget just enough for the seed; deps should be dropped or truncated.
        _builder = CreateBuilder(maxTokens: 150);
        var ctx = await _builder.BuildAsync(_workspaceId, new[] { _seedFile }, maxTokenBudget: 150);

        // Either zero deps, or deps that fit — but never more tokens than budget.
        Assert.True(ctx.EstimatedTokens <= 150,
            $"expected ≤150 tokens with tight budget, got {ctx.EstimatedTokens}");
    }

    // ── Non-SQL workspace: no PL/SQL dep resolution ────────────────────────

    [Fact]
    public async Task Build_NonSqlSeed_DoesNotAttemptDepResolution()
    {
        // Add a non-SQL file to a fresh workspace and seed it; resolver should not be invoked.
        var jsRoot = Path.Combine(Path.GetTempPath(), "codeintel-js-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(jsRoot);
        try
        {
            var jsFile = Path.Combine(jsRoot, "app.ts");
            await File.WriteAllTextAsync(jsFile, "export const x = 1;");

            var jsWorkspace = new WorkspaceService(Options.Create(new AnalysisOptions()), NullLogger<WorkspaceService>.Instance);
            try
            {
                var ws = await jsWorkspace.LoadAsync(jsRoot);
                var parser   = new PlSqlObjectParser();
                var resolver = new PlSqlRepoResolver(jsWorkspace, NullLogger<PlSqlRepoResolver>.Instance);
                var options  = Options.Create(new AnalysisOptions { MaxContextTokens = 5000 });
                var builder  = new ContextBuilder(jsWorkspace, parser, resolver, options, NullLogger<ContextBuilder>.Instance);

                var ctx = await builder.BuildAsync(ws.Id, new[] { jsFile }, maxTokenBudget: 5000);

                Assert.Single(ctx.Files);
                Assert.DoesNotContain(ctx.Files, f => f.IsResolvedDependency);
            }
            finally { jsWorkspace.Dispose(); }
        }
        finally
        {
            try { Directory.Delete(jsRoot, recursive: true); } catch { }
        }
    }
}
