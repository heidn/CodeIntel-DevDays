using CodeIntel.Server.Models;
using CodeIntel.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeIntel.Server.Tests;

public class PlSqlRepoResolverTests : IAsyncLifetime
{
    private string _root = null!;
    private WorkspaceService _workspace = null!;
    private PlSqlRepoResolver _resolver = null!;
    private string _workspaceId = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "codeintel-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        await WriteAllAsync("my_proc.sql", """
            CREATE OR REPLACE PROCEDURE my_proc AS
            BEGIN
              NULL;
            END;
            """);

        await WriteAllAsync("Mixed_Case_Proc.sql", """
            CREATE OR REPLACE PROCEDURE Mixed_Case_Proc AS
            BEGIN
              NULL;
            END;
            """);

        await WriteAllAsync("orders_pkg.pkb", """
            CREATE OR REPLACE PACKAGE BODY orders_pkg AS
              PROCEDURE do_thing IS BEGIN NULL; END;
            END orders_pkg;
            """);

        // A bundle file containing a procedure whose name doesn't match the filename.
        // Resolver should fall back to DDL grep.
        await WriteAllAsync("bundle.sql", """
            -- bundle of small procs
            CREATE OR REPLACE PROCEDURE buried_proc AS
            BEGIN
              NULL;
            END;

            CREATE OR REPLACE PROCEDURE HR.qualified_proc AS
            BEGIN
              NULL;
            END;
            """);

        _workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance);
        var ws = await _workspace.LoadAsync(_root);
        _workspaceId = ws.Id;

        _resolver = new PlSqlRepoResolver(_workspace, NullLogger<PlSqlRepoResolver>.Instance);
    }

    public Task DisposeAsync()
    {
        _workspace?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private Task WriteAllAsync(string relative, string content) =>
        File.WriteAllTextAsync(Path.Combine(_root, relative), content);

    // ── Filename match ─────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_ExactFilenameMatch_Found()
    {
        var result = await _resolver.ResolveAsync(_workspaceId, "my_proc");
        Assert.NotNull(result);
        Assert.Equal("my_proc",  result!.Name);
        Assert.Equal("filename", result.ResolvedVia);
        Assert.Contains("CREATE OR REPLACE PROCEDURE my_proc", result.Content);
    }

    [Fact]
    public async Task Resolve_FilenameMatchIsCaseInsensitive()
    {
        var result = await _resolver.ResolveAsync(_workspaceId, "MIXED_CASE_PROC");
        Assert.NotNull(result);
        Assert.Equal("filename", result!.ResolvedVia);
    }

    [Fact]
    public async Task Resolve_PackageBodyExtension_Found()
    {
        var result = await _resolver.ResolveAsync(_workspaceId, "orders_pkg");
        Assert.NotNull(result);
        Assert.EndsWith(".pkb", result!.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolve_SchemaPrefix_IsStrippedBeforeFilenameMatch()
    {
        // "HR.my_proc" should resolve via filename match on my_proc.sql.
        var result = await _resolver.ResolveAsync(_workspaceId, "HR.my_proc");
        Assert.NotNull(result);
        Assert.Equal("filename", result!.ResolvedVia);
    }

    // ── DDL grep fallback ──────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_BuriedProc_FoundViaDdlGrep()
    {
        var result = await _resolver.ResolveAsync(_workspaceId, "buried_proc");
        Assert.NotNull(result);
        Assert.Equal("ddl-grep", result!.ResolvedVia);
        Assert.EndsWith("bundle.sql", result.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolve_SchemaQualifiedDdl_FoundViaDdlGrep()
    {
        // The file declares HR.qualified_proc — our DDL pattern accepts optional schema prefix.
        var result = await _resolver.ResolveAsync(_workspaceId, "qualified_proc");
        Assert.NotNull(result);
        Assert.Equal("ddl-grep", result!.ResolvedVia);
    }

    // ── Negative cases ─────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_NonExistentObject_ReturnsNull()
    {
        var result = await _resolver.ResolveAsync(_workspaceId, "does_not_exist_anywhere");
        Assert.Null(result);
    }

    [Fact]
    public async Task Resolve_UnknownWorkspace_ReturnsNull()
    {
        var result = await _resolver.ResolveAsync("nonexistent-workspace-id", "my_proc");
        Assert.Null(result);
    }

    [Fact]
    public async Task Resolve_BlankObjectName_ReturnsNull()
    {
        var result = await _resolver.ResolveAsync(_workspaceId, "   ");
        Assert.Null(result);
    }
}
