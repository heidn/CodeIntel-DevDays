using CodeIntel.Server.Data;
using CodeIntel.Server.Models;
using Microsoft.Extensions.Options;

namespace CodeIntel.Server.Services;

public interface IResultCache
{
    /// <summary>
    /// Returns a prior analysis result if the same {preset, model, file-content hash}
    /// has been seen within the configured TTL. Free-text mode never caches.
    /// </summary>
    Task<AnalysisResult?> LookupAsync(AnalysisRequest request, string? contentHash, string? modelName, CancellationToken ct);

    /// <summary>
    /// Records the cache key against an analysis id. Idempotent on duplicate keys.
    /// </summary>
    Task RememberAsync(string cacheKey, Guid analysisId, CancellationToken ct);
}

public class ResultCache : IResultCache
{
    private readonly CodeIntelDb _db;
    private readonly IAnalysisResultStore _store;
    private readonly AnalysisOptions _options;
    private readonly ILogger<ResultCache> _logger;

    public ResultCache(
        CodeIntelDb db,
        IAnalysisResultStore store,
        IOptions<AnalysisOptions> options,
        ILogger<ResultCache> logger)
    {
        _db = db;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AnalysisResult?> LookupAsync(AnalysisRequest request, string? contentHash, string? modelName, CancellationToken ct)
    {
        if (!_options.EnableResultCache) return null;
        if (request.Mode != AnalysisMode.Preset) return null;
        var cacheKey = ContentHasher.BuildCacheKey(request.PresetKey, modelName, contentHash, _options.MaxContextTokens);
        if (cacheKey is null) return null;

        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT analysis_id, created_at FROM result_cache WHERE cache_key = $key";
        var pKey = cmd.CreateParameter(); pKey.ParameterName = "$key"; pKey.Value = cacheKey;
        cmd.Parameters.Add(pKey);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var analysisId = Guid.Parse(reader.GetString(0));
        var createdAtTicks = reader.GetInt64(1);
        var createdAt = new DateTime(createdAtTicks, DateTimeKind.Utc);

        if (DateTime.UtcNow - createdAt > TimeSpan.FromHours(_options.ResultCacheTtlHours))
        {
            _logger.LogInformation("Cache entry for {Key} is past TTL ({Hours}h); ignoring", cacheKey, _options.ResultCacheTtlHours);
            return null;
        }

        var hit = _store.Get(analysisId);
        if (hit is null)
        {
            // The analysis was pruned but the cache entry survived — clean up.
            await DeleteAsync(conn, cacheKey, ct);
            return null;
        }

        _logger.LogInformation("Result cache HIT for preset={Preset} hash={Hash} → {Id}",
            request.PresetKey, contentHash, analysisId);
        return hit;
    }

    public async Task RememberAsync(string cacheKey, Guid analysisId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO result_cache (cache_key, analysis_id, created_at)
            VALUES ($key, $id, $created)
            ON CONFLICT(cache_key) DO UPDATE SET
              analysis_id = excluded.analysis_id,
              created_at = excluded.created_at;
            """;
        Add(cmd, "$key", cacheKey);
        Add(cmd, "$id", analysisId.ToString());
        Add(cmd, "$created", DateTime.UtcNow.Ticks);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteAsync(Microsoft.Data.Sqlite.SqliteConnection conn, string key, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM result_cache WHERE cache_key = $key";
        Add(cmd, "$key", key);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void Add(Microsoft.Data.Sqlite.SqliteCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
