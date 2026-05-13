using System.Security.Cryptography;
using System.Text;
using CodeIntel.Server.Data;
using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services;

public record IgnoredFinding(
    string WorkspaceRoot,
    string Signature,
    string? Title,
    string? FilePath,
    int? LineNumber,
    DateTime IgnoredAt,
    string? Note);

public interface IIgnoredFindingsStore
{
    /// <summary>
    /// Deterministic signature for a finding — used both for the persistence key and the
    /// in-memory dedup filter applied to fresh results.
    /// </summary>
    string SignatureFor(Finding f);

    Task<HashSet<string>> ListSignaturesAsync(string workspaceRoot, CancellationToken ct);
    Task<IReadOnlyList<IgnoredFinding>> ListAsync(string workspaceRoot, CancellationToken ct);
    Task IgnoreAsync(string workspaceRoot, Finding finding, string? note, CancellationToken ct);
    Task UnignoreAsync(string workspaceRoot, string signature, CancellationToken ct);
}

public class IgnoredFindingsStore : IIgnoredFindingsStore
{
    private readonly CodeIntelDb _db;
    private readonly ILogger<IgnoredFindingsStore> _logger;

    public IgnoredFindingsStore(CodeIntelDb db, ILogger<IgnoredFindingsStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    public string SignatureFor(Finding f)
    {
        // Same shape as FindingsComparer to keep the diff + ignore behaviors aligned.
        var raw = $"{f.Severity}|{f.FilePath ?? "?"}|{f.Title.Trim().ToLowerInvariant()}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..16];
    }

    public async Task<HashSet<string>> ListSignaturesAsync(string workspaceRoot, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(workspaceRoot))
            return new HashSet<string>(StringComparer.Ordinal);
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT signature FROM ignored_findings WHERE workspace_root = $root";
        Add(cmd, "$root", workspaceRoot);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct)) set.Add(reader.GetString(0));
        return set;
    }

    public async Task<IReadOnlyList<IgnoredFinding>> ListAsync(string workspaceRoot, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT signature, title, file_path, line_number, ignored_at, note " +
                          "FROM ignored_findings WHERE workspace_root = $root ORDER BY ignored_at DESC";
        Add(cmd, "$root", workspaceRoot);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<IgnoredFinding>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new IgnoredFinding(
                WorkspaceRoot: workspaceRoot,
                Signature: reader.GetString(0),
                Title: reader.IsDBNull(1) ? null : reader.GetString(1),
                FilePath: reader.IsDBNull(2) ? null : reader.GetString(2),
                LineNumber: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                IgnoredAt: new DateTime(reader.GetInt64(4), DateTimeKind.Utc),
                Note: reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return list;
    }

    public async Task IgnoreAsync(string workspaceRoot, Finding finding, string? note, CancellationToken ct)
    {
        var sig = SignatureFor(finding);
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ignored_findings (workspace_root, signature, title, file_path, line_number, ignored_at, note)
            VALUES ($root, $sig, $title, $file, $line, $at, $note)
            ON CONFLICT(workspace_root, signature) DO UPDATE SET
              title = excluded.title,
              file_path = excluded.file_path,
              line_number = excluded.line_number,
              ignored_at = excluded.ignored_at,
              note = excluded.note;
            """;
        Add(cmd, "$root", workspaceRoot);
        Add(cmd, "$sig", sig);
        Add(cmd, "$title", finding.Title);
        Add(cmd, "$file", (object?)finding.FilePath ?? DBNull.Value);
        Add(cmd, "$line", (object?)finding.LineNumber ?? DBNull.Value);
        Add(cmd, "$at", DateTime.UtcNow.Ticks);
        Add(cmd, "$note", (object?)note ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Ignored finding {Sig} in workspace {Root}", sig, workspaceRoot);
    }

    public async Task UnignoreAsync(string workspaceRoot, string signature, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ignored_findings WHERE workspace_root = $root AND signature = $sig";
        Add(cmd, "$root", workspaceRoot);
        Add(cmd, "$sig", signature);
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
