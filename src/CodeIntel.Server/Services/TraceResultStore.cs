using System.Text.Json;
using CodeIntel.Server.Data;
using CodeIntel.Server.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace CodeIntel.Server.Services;

public interface ITraceResultStore
{
    void Save(TraceResult result);
    TraceResult? Get(Guid id);
    IReadOnlyList<TraceResult> Recent(int count = 20);
}

public class SqliteTraceResultStore : ITraceResultStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly CodeIntelDb _db;
    private readonly AnalysisOptions _options;
    private readonly ILogger<SqliteTraceResultStore> _logger;

    public SqliteTraceResultStore(
        CodeIntelDb db,
        IOptions<AnalysisOptions> options,
        ILogger<SqliteTraceResultStore> logger)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    public void Save(TraceResult result)
    {
        _ = Task.Run(async () =>
        {
            try { await SaveAsyncCore(result); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist trace {Id}", result.Id); }
        });
    }

    private async Task SaveAsyncCore(TraceResult r)
    {
        await using var conn = await _db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO traces
              (id, started_at, completed_at, duration_seconds, workspace_id,
               entry_point_fqn, entry_point_json, direction, depth,
               nodes_json, edges_json, mermaid, truncated, report_path)
            VALUES
              ($id, $startedAt, $completedAt, $duration, $workspaceId,
               $fqn, $entry, $dir, $depth,
               $nodes, $edges, $mermaid, $truncated, $report)
            ON CONFLICT(id) DO UPDATE SET
              completed_at = excluded.completed_at,
              duration_seconds = excluded.duration_seconds,
              nodes_json = excluded.nodes_json,
              edges_json = excluded.edges_json,
              mermaid = excluded.mermaid,
              truncated = excluded.truncated,
              report_path = excluded.report_path;
            """;
        Bind(cmd, "$id", r.Id.ToString());
        Bind(cmd, "$startedAt", r.StartedAt.ToUniversalTime().Ticks);
        Bind(cmd, "$completedAt", r.CompletedAt.ToUniversalTime().Ticks);
        Bind(cmd, "$duration", r.Duration.TotalSeconds);
        Bind(cmd, "$workspaceId", r.WorkspaceId);
        Bind(cmd, "$fqn", r.EntryPointSymbolFqn);
        Bind(cmd, "$entry", JsonSerializer.Serialize(r.EntryPoint, JsonOpts));
        Bind(cmd, "$dir", r.Direction.ToString());
        Bind(cmd, "$depth", r.Depth);
        Bind(cmd, "$nodes", JsonSerializer.Serialize(r.Nodes, JsonOpts));
        Bind(cmd, "$edges", JsonSerializer.Serialize(r.Edges, JsonOpts));
        Bind(cmd, "$mermaid", r.Mermaid);
        Bind(cmd, "$truncated", r.Truncated ? 1 : 0);
        Bind(cmd, "$report", (object?)r.ReportPath ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();

        await PruneAsync(conn);
    }

    private async Task PruneAsync(SqliteConnection conn)
    {
        if (_options.MaxPersistedResults <= 0) return;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM traces
            WHERE id IN (
              SELECT id FROM traces ORDER BY started_at DESC LIMIT -1 OFFSET $cap
            );
            """;
        Bind(cmd, "$cap", _options.MaxPersistedResults);
        await cmd.ExecuteNonQueryAsync();
    }

    public TraceResult? Get(Guid id) => GetAsyncCore(id).GetAwaiter().GetResult();

    private async Task<TraceResult?> GetAsyncCore(Guid id)
    {
        await using var conn = await _db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM traces WHERE id = $id";
        Bind(cmd, "$id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return Hydrate(reader);
    }

    public IReadOnlyList<TraceResult> Recent(int count = 20) =>
        RecentAsyncCore(count).GetAwaiter().GetResult();

    private async Task<IReadOnlyList<TraceResult>> RecentAsyncCore(int count)
    {
        await using var conn = await _db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM traces ORDER BY started_at DESC LIMIT $n";
        Bind(cmd, "$n", count);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<TraceResult>(count);
        while (await reader.ReadAsync()) list.Add(Hydrate(reader));
        return list;
    }

    private static TraceResult Hydrate(SqliteDataReader reader)
    {
        var nodes = JsonSerializer.Deserialize<List<TraceNode>>(Str(reader, "nodes_json"), JsonOpts) ?? new();
        var edges = JsonSerializer.Deserialize<List<TraceEdge>>(Str(reader, "edges_json"), JsonOpts) ?? new();
        var entry = JsonSerializer.Deserialize<TraceEntryPoint>(Str(reader, "entry_point_json"), JsonOpts)
            ?? new TraceEntryPoint(null, null, null, null);

        return new TraceResult(
            Id: Guid.Parse(Str(reader, "id")),
            StartedAt: new DateTime(Int64(reader, "started_at"), DateTimeKind.Utc),
            CompletedAt: new DateTime(Int64(reader, "completed_at"), DateTimeKind.Utc),
            WorkspaceId: Str(reader, "workspace_id"),
            EntryPoint: entry,
            EntryPointSymbolFqn: Str(reader, "entry_point_fqn"),
            Direction: Enum.Parse<TraceDirection>(Str(reader, "direction")),
            Depth: Int32(reader, "depth"),
            Nodes: nodes,
            Edges: edges,
            Mermaid: Str(reader, "mermaid"),
            Truncated: Int32(reader, "truncated") == 1,
            Duration: TimeSpan.FromSeconds(Dbl(reader, "duration_seconds")),
            ReportPath: NullableStr(reader, "report_path"));
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
