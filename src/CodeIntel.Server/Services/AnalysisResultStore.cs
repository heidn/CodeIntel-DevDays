using System.Text.Json;
using CodeIntel.Server.Data;
using CodeIntel.Server.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace CodeIntel.Server.Services;

public interface IAnalysisResultStore
{
    void Save(AnalysisResult result);
    AnalysisResult? Get(Guid id);
    IReadOnlyList<AnalysisResult> Recent(int count = 20, string? workspaceId = null);
}

/// <summary>
/// Persists analysis results to SQLite. Findings + analyzed-file lists are stored as
/// JSON blobs since they are queried as a whole, never row-by-row. The fire-and-forget
/// Save() pattern matches the in-memory predecessor — callers don't await persistence.
/// </summary>
public class SqliteAnalysisResultStore : IAnalysisResultStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly CodeIntelDb _db;
    private readonly AnalysisOptions _options;
    private readonly ILogger<SqliteAnalysisResultStore> _logger;

    public SqliteAnalysisResultStore(
        CodeIntelDb db,
        IOptions<AnalysisOptions> options,
        ILogger<SqliteAnalysisResultStore> logger)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    public void Save(AnalysisResult result)
    {
        // Blocking write. Callers (orchestrator + save-to-repo flow) emit follow-up
        // events / accept fetch-by-id calls immediately after Save() returns, so the
        // row MUST be queryable before we return. SQLite WAL writes of a few KB are
        // microseconds and Save() is called from a background Task, so blocking is fine.
        SaveAsyncCore(result).GetAwaiter().GetResult();
    }

    private async Task SaveAsyncCore(AnalysisResult r)
    {
        await using var conn = await _db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO analyses
              (id, started_at, completed_at, duration_seconds, mode, preset_key, free_text_prompt,
               workspace_id, workspace_root, context_tokens, analyzed_files_json, findings_json,
               raw_llm_output, report_path, content_hash)
            VALUES
              ($id, $startedAt, $completedAt, $duration, $mode, $preset, $freeText,
               $workspaceId, $workspaceRoot, $tokens, $files, $findings,
               $raw, $report, $hash)
            ON CONFLICT(id) DO UPDATE SET
              completed_at = excluded.completed_at,
              duration_seconds = excluded.duration_seconds,
              findings_json = excluded.findings_json,
              raw_llm_output = excluded.raw_llm_output,
              report_path = excluded.report_path,
              content_hash = excluded.content_hash;
            """;
        Bind(cmd, "$id", r.Id.ToString());
        Bind(cmd, "$startedAt", r.StartedAt.ToUniversalTime().Ticks);
        Bind(cmd, "$completedAt", r.CompletedAt.ToUniversalTime().Ticks);
        Bind(cmd, "$duration", r.Duration.TotalSeconds);
        Bind(cmd, "$mode", r.Mode.ToString());
        Bind(cmd, "$preset", (object?)r.PresetKey ?? DBNull.Value);
        Bind(cmd, "$freeText", (object?)r.FreeTextPrompt ?? DBNull.Value);
        Bind(cmd, "$workspaceId", r.WorkspaceId);
        Bind(cmd, "$workspaceRoot", (object?)r.WorkspaceRoot ?? DBNull.Value);
        Bind(cmd, "$tokens", r.ContextTokens);
        Bind(cmd, "$files", JsonSerializer.Serialize(r.AnalyzedFiles, JsonOpts));
        Bind(cmd, "$findings", JsonSerializer.Serialize(r.Findings, JsonOpts));
        Bind(cmd, "$raw", r.RawLlmOutput);
        Bind(cmd, "$report", (object?)r.ReportPath ?? DBNull.Value);
        Bind(cmd, "$hash", (object?)r.ContentHash ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();

        await PruneAsync(conn);
    }

    private async Task PruneAsync(SqliteConnection conn)
    {
        if (_options.MaxPersistedResults <= 0) return;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM analyses
            WHERE id IN (
              SELECT id FROM analyses
              ORDER BY started_at DESC
              LIMIT -1 OFFSET $cap
            );
            """;
        Bind(cmd, "$cap", _options.MaxPersistedResults);
        await cmd.ExecuteNonQueryAsync();
    }

    public AnalysisResult? Get(Guid id)
    {
        return GetAsyncCore(id).GetAwaiter().GetResult();
    }

    private async Task<AnalysisResult?> GetAsyncCore(Guid id)
    {
        await using var conn = await _db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM analyses WHERE id = $id";
        Bind(cmd, "$id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return Hydrate(reader);
    }

    public IReadOnlyList<AnalysisResult> Recent(int count = 20, string? workspaceId = null)
    {
        return RecentAsyncCore(count, workspaceId).GetAwaiter().GetResult();
    }

    private async Task<IReadOnlyList<AnalysisResult>> RecentAsyncCore(int count, string? workspaceId)
    {
        await using var conn = await _db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            cmd.CommandText = "SELECT * FROM analyses ORDER BY started_at DESC LIMIT $n";
        }
        else
        {
            cmd.CommandText = "SELECT * FROM analyses WHERE workspace_id = $ws ORDER BY started_at DESC LIMIT $n";
            Bind(cmd, "$ws", workspaceId);
        }
        Bind(cmd, "$n", count);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<AnalysisResult>(count);
        while (await reader.ReadAsync()) list.Add(Hydrate(reader));
        return list;
    }

    private static AnalysisResult Hydrate(SqliteDataReader reader)
    {
        var findings = JsonSerializer.Deserialize<List<Finding>>(Str(reader, "findings_json"), JsonOpts)
            ?? new List<Finding>();
        var files = JsonSerializer.Deserialize<List<string>>(Str(reader, "analyzed_files_json"), JsonOpts)
            ?? new List<string>();
        return new AnalysisResult(
            Id: Guid.Parse(Str(reader, "id")),
            StartedAt: new DateTime(Int64(reader, "started_at"), DateTimeKind.Utc),
            CompletedAt: new DateTime(Int64(reader, "completed_at"), DateTimeKind.Utc),
            Mode: Enum.Parse<AnalysisMode>(Str(reader, "mode")),
            PresetKey: NullableStr(reader, "preset_key"),
            FreeTextPrompt: NullableStr(reader, "free_text_prompt"),
            AnalyzedFiles: files,
            Findings: findings,
            RawLlmOutput: Str(reader, "raw_llm_output"),
            ContextTokens: Int32(reader, "context_tokens"),
            Duration: TimeSpan.FromSeconds(Dbl(reader, "duration_seconds")),
            WorkspaceId: Str(reader, "workspace_id"),
            ReportPath: NullableStr(reader, "report_path"),
            WorkspaceRoot: NullableStr(reader, "workspace_root"),
            ContentHash: NullableStr(reader, "content_hash"));
    }

    private static string  Str       (SqliteDataReader r, string col) => r.GetString(r.GetOrdinal(col));
    private static long    Int64     (SqliteDataReader r, string col) => r.GetInt64 (r.GetOrdinal(col));
    private static int     Int32     (SqliteDataReader r, string col) => r.GetInt32 (r.GetOrdinal(col));
    private static double  Dbl       (SqliteDataReader r, string col) => r.GetDouble(r.GetOrdinal(col));
    private static string? NullableStr(SqliteDataReader r, string col)
    {
        var idx = r.GetOrdinal(col);
        return r.IsDBNull(idx) ? null : r.GetString(idx);
    }

    private static void Bind(SqliteCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
