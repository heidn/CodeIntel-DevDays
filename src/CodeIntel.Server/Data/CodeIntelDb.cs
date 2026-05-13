using CodeIntel.Server.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace CodeIntel.Server.Data;

/// <summary>
/// Lightweight SQLite-backed store for analysis + trace history, ignored findings,
/// and the file-hash result cache. All tables are created on first use.
///
/// Concurrency: each call opens its own connection (SQLite uses file-level locking,
/// not connection-level — the WAL journal mode keeps reads non-blocking).
/// </summary>
public sealed class CodeIntelDb
{
    private readonly string _connectionString;
    private readonly ILogger<CodeIntelDb> _logger;
    private readonly Lazy<Task> _initTask;

    public CodeIntelDb(IOptions<DataOptions> options, IWebHostEnvironment env, ILogger<CodeIntelDb> logger)
    {
        _logger = logger;
        var configured = options.Value.DatabasePath;
        var resolved = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, configured));
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);

        // Cache=Shared lets the bookkeeping SemaphoreSlim be the bottleneck rather
        // than SQLITE_BUSY retries when the orchestrator and a reader race.
        _connectionString = $"Data Source={resolved};Cache=Shared";
        _initTask = new Lazy<Task>(InitializeAsync);
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        await _initTask.Value;
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private async Task InitializeAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        await pragma.ExecuteNonQueryAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS analyses (
                id TEXT PRIMARY KEY NOT NULL,
                started_at INTEGER NOT NULL,
                completed_at INTEGER NOT NULL,
                duration_seconds REAL NOT NULL,
                mode TEXT NOT NULL,
                preset_key TEXT,
                free_text_prompt TEXT,
                workspace_id TEXT NOT NULL,
                workspace_root TEXT,
                context_tokens INTEGER NOT NULL,
                analyzed_files_json TEXT NOT NULL,
                findings_json TEXT NOT NULL,
                raw_llm_output TEXT NOT NULL,
                report_path TEXT,
                content_hash TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_analyses_started_at ON analyses(started_at DESC);
            CREATE INDEX IF NOT EXISTS idx_analyses_workspace ON analyses(workspace_id);
            CREATE INDEX IF NOT EXISTS idx_analyses_cache ON analyses(preset_key, content_hash);

            CREATE TABLE IF NOT EXISTS traces (
                id TEXT PRIMARY KEY NOT NULL,
                started_at INTEGER NOT NULL,
                completed_at INTEGER NOT NULL,
                duration_seconds REAL NOT NULL,
                workspace_id TEXT NOT NULL,
                entry_point_fqn TEXT NOT NULL,
                entry_point_json TEXT NOT NULL,
                direction TEXT NOT NULL,
                depth INTEGER NOT NULL,
                nodes_json TEXT NOT NULL,
                edges_json TEXT NOT NULL,
                mermaid TEXT NOT NULL,
                truncated INTEGER NOT NULL,
                report_path TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_traces_started_at ON traces(started_at DESC);

            CREATE TABLE IF NOT EXISTS ignored_findings (
                workspace_root TEXT NOT NULL,
                signature TEXT NOT NULL,
                title TEXT,
                file_path TEXT,
                line_number INTEGER,
                ignored_at INTEGER NOT NULL,
                note TEXT,
                PRIMARY KEY (workspace_root, signature)
            );

            CREATE TABLE IF NOT EXISTS result_cache (
                cache_key TEXT PRIMARY KEY NOT NULL,
                analysis_id TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                FOREIGN KEY (analysis_id) REFERENCES analyses(id) ON DELETE CASCADE
            );
            """;
        await cmd.ExecuteNonQueryAsync();
        _logger.LogInformation("SQLite schema initialized at {Conn}", _connectionString);
    }
}
