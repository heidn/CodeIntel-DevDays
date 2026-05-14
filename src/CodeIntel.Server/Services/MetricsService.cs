using System.Text.Json;
using CodeIntel.Server.Data;
using CodeIntel.Server.Models;
using CodeIntel.Server.Services.LanguageBackends;
using Microsoft.Data.Sqlite;

namespace CodeIntel.Server.Services;

public interface IMetricsService
{
    Task<WorkspaceMetricsResult> ComputeAsync(string workspaceId, IReadOnlyList<string>? filePaths, CancellationToken ct);
}

/// <summary>
/// Workspace-wide metrics computation. Dispatches per file to the C# (Roslyn) or
/// PL/SQL (ANTLR token-stream) analyzer, then aggregates into a summary plus
/// per-file / per-method rows. Results are cached in SQLite keyed on the SHA-256
/// of the file content set so re-opening the Metrics tab on an unchanged workspace
/// is instant.
/// </summary>
public class MetricsService : IMetricsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IWorkspaceService _workspace;
    private readonly ILanguageBackendRegistry _backends;
    private readonly CodeIntelDb _db;
    private readonly ILogger<MetricsService> _logger;

    public MetricsService(
        IWorkspaceService workspace,
        ILanguageBackendRegistry backends,
        CodeIntelDb db,
        ILogger<MetricsService> logger)
    {
        _workspace = workspace;
        _backends = backends;
        _db = db;
        _logger = logger;
    }

    public async Task<WorkspaceMetricsResult> ComputeAsync(
        string workspaceId, IReadOnlyList<string>? filePaths, CancellationToken ct)
    {
        var ws = _workspace.GetWorkspace(workspaceId)
            ?? throw new InvalidOperationException("Workspace not loaded.");

        // Metrics analyzers exist only for C# (Roslyn) and PL/SQL (ANTLR). For
        // TypeScript / Java, bail early with Supported=false so the UI shows an
        // explicit "not implemented" placeholder instead of a misleading 0/0.
        if (!IsMetricsSupported(ws.Language))
        {
            _logger.LogInformation(
                "Metrics not supported for {Language} workspace {WorkspaceId}", ws.Language, workspaceId);
            return new WorkspaceMetricsResult(
                WorkspaceId: workspaceId,
                ComputedAt: DateTime.UtcNow,
                Language: ws.Language,
                ContentHash: "",
                Summary: EmptySummary(),
                Files: [],
                Supported: false);
        }

        var targets = ResolveTargets(ws, filePaths);
        if (targets.Count == 0)
        {
            return new WorkspaceMetricsResult(
                WorkspaceId: workspaceId,
                ComputedAt: DateTime.UtcNow,
                Language: ws.Language,
                ContentHash: "",
                Summary: EmptySummary(),
                Files: []);
        }

        var hash = await ContentHasher.HashFilesAsync(_workspace, workspaceId, targets, ct) ?? "";
        var cacheKey = $"{workspaceId}|{hash}";

        // Cache lookup.
        var cached = await TryLoadCachedAsync(cacheKey, ct);
        if (cached != null)
        {
            _logger.LogInformation("Metrics cache hit for {WorkspaceId} ({Hash})", workspaceId, hash[..Math.Min(8, hash.Length)]);
            return cached;
        }

        var backend = _backends.GetBackendForWorkspace(workspaceId);

        var fileResults = new List<FileMetricsResult>(targets.Count);
        foreach (var path in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var content = await _workspace.ReadFileAsync(workspaceId, path, ct);
                var relative = MakeRelative(ws.RootFolder, path);
                fileResults.Add(backend.ComputeFileMetrics(path, relative, content));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Metrics: failed on {Path}", path);
                fileResults.Add(new FileMetricsResult(
                    FilePath: path,
                    RelativePath: MakeRelative(ws.RootFolder, path),
                    Language: ws.Language,
                    TotalLines: 0,
                    Methods: [],
                    ErrorMessage: ex.Message));
            }
        }

        var summary = BuildSummary(fileResults);
        var result0 = new WorkspaceMetricsResult(
            WorkspaceId: workspaceId,
            ComputedAt: DateTime.UtcNow,
            Language: ws.Language,
            ContentHash: hash,
            Summary: summary,
            Files: fileResults);

        await SaveCacheAsync(cacheKey, workspaceId, hash, result0, ct);
        return result0;
    }

    private static List<string> ResolveTargets(Workspace ws, IReadOnlyList<string>? filePaths)
    {
        if (filePaths != null && filePaths.Count > 0)
            return filePaths.Where(p => HasMetricExtension(p)).ToList();

        return ws.Projects
            .SelectMany(p => p.Files)
            .Select(f => f.AbsolutePath)
            .Where(HasMetricExtension)
            .ToList();
    }

    private static bool HasMetricExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".cs" || PlSqlFileExtensions.Contains(ext);
    }

    private static bool IsMetricsSupported(Language language) => language switch
    {
        Language.CSharp or Language.Sql => true,
        _ => false,
    };

    private static string MakeRelative(string? root, string path)
    {
        if (string.IsNullOrEmpty(root)) return path;
        try { return Path.GetRelativePath(root, path); }
        catch { return path; }
    }

    private static MetricsSummary BuildSummary(IReadOnlyList<FileMetricsResult> files)
    {
        var allMethods = files.SelectMany(f => f.Methods).ToList();
        return new MetricsSummary(
            FileCount: files.Count,
            MethodCount: allMethods.Count,
            HighComplexityCount: allMethods.Count(m => m.CyclomaticComplexity >= 10),
            LongMethodCount: allMethods.Count(m => m.LengthLines >= 50),
            EmptyCatchCount: allMethods.Sum(m => m.EmptyCatchCount),
            SyncOverAsyncCount: allMethods.Sum(m => m.SyncOverAsyncCount),
            CursorTotal: allMethods.Sum(m => m.CursorDeclarationCount ?? 0),
            SwallowedWhenOthersTotal: allMethods.Sum(m => m.SwallowedWhenOthersCount ?? 0));
    }

    private static MetricsSummary EmptySummary() =>
        new(0, 0, 0, 0, 0, 0, 0, 0);

    private async Task<WorkspaceMetricsResult?> TryLoadCachedAsync(string cacheKey, CancellationToken ct)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT result_json FROM metrics_cache WHERE cache_key = $k";
            var p = cmd.CreateParameter(); p.ParameterName = "$k"; p.Value = cacheKey;
            cmd.Parameters.Add(p);
            var json = (string?)await cmd.ExecuteScalarAsync(ct);
            if (string.IsNullOrEmpty(json)) return null;
            return JsonSerializer.Deserialize<WorkspaceMetricsResult>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Metrics cache read failed for {Key}", cacheKey);
            return null;
        }
    }

    private async Task SaveCacheAsync(string cacheKey, string workspaceId, string hash, WorkspaceMetricsResult result, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(result, JsonOpts);
            await using var conn = await _db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO metrics_cache (cache_key, workspace_id, content_hash, computed_at, result_json)
                VALUES ($k, $w, $h, $t, $j)
                ON CONFLICT(cache_key) DO UPDATE SET
                  computed_at = excluded.computed_at,
                  result_json = excluded.result_json;
                """;
            Bind(cmd, "$k", cacheKey);
            Bind(cmd, "$w", workspaceId);
            Bind(cmd, "$h", hash);
            Bind(cmd, "$t", DateTime.UtcNow.Ticks);
            Bind(cmd, "$j", json);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Metrics cache write failed for {Key}", cacheKey);
        }
    }

    private static void Bind(SqliteCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
