using System.Text.RegularExpressions;
using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services;

public interface IPlSqlRepoResolver
{
    /// <summary>
    /// Resolves a single object name to a file in the workspace. Returns null on no match.
    /// </summary>
    Task<PlSqlResolution?> ResolveAsync(string workspaceId, string objectName, PlSqlObjectKind kindHint = PlSqlObjectKind.Unknown, CancellationToken ct = default);
}

/// <summary>
/// Maps a PL/SQL object name (table / view / proc / package) to a file in the loaded
/// workspace. Strategy: filename match first (very common in PL/SQL repos), then a
/// content grep for CREATE-OR-REPLACE-style DDL as fallback.
/// </summary>
public class PlSqlRepoResolver : IPlSqlRepoResolver
{
    private const int MaxFallbackFiles = 1500;

    private readonly IWorkspaceService _workspace;
    private readonly ILogger<PlSqlRepoResolver> _logger;

    public PlSqlRepoResolver(IWorkspaceService workspace, ILogger<PlSqlRepoResolver> logger)
    {
        _workspace = workspace;
        _logger = logger;
    }

    public async Task<PlSqlResolution?> ResolveAsync(
        string workspaceId,
        string objectName,
        PlSqlObjectKind kindHint = PlSqlObjectKind.Unknown,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(objectName)) return null;

        var ws = _workspace.GetWorkspace(workspaceId);
        if (ws == null) return null;

        // Strip schema prefix if present (SCHEMA.NAME -> NAME).
        var bareName = objectName;
        var dot = bareName.LastIndexOf('.');
        if (dot >= 0 && dot < bareName.Length - 1)
            bareName = bareName[(dot + 1)..];

        var sqlFiles = ws.Projects
            .SelectMany(p => p.Files)
            .Where(f => PlSqlFileExtensions.Matches(f.AbsolutePath))
            .ToList();

        if (sqlFiles.Count == 0) return null;

        // 1) Filename match: NAME.sql / NAME.pkg / NAME.pkb / ... (case-insensitive)
        foreach (var file in sqlFiles)
        {
            ct.ThrowIfCancellationRequested();
            var nameNoExt = Path.GetFileNameWithoutExtension(file.FileName);
            if (string.Equals(nameNoExt, bareName, StringComparison.OrdinalIgnoreCase))
                return await BuildResolution(file, bareName, kindHint, "filename", ct);
        }

        // 2) DDL grep across SQL files. Cap the scan to avoid pathological repo sizes.
        var pattern = BuildDdlPattern(bareName);
        var scanned = 0;
        foreach (var file in sqlFiles)
        {
            if (scanned++ >= MaxFallbackFiles) break;
            ct.ThrowIfCancellationRequested();
            string content;
            try
            {
                content = await File.ReadAllTextAsync(file.AbsolutePath, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping unreadable SQL file {Path}", file.AbsolutePath);
                continue;
            }

            if (pattern.IsMatch(content))
                return new PlSqlResolution(
                    Name: bareName,
                    Kind: kindHint,
                    AbsolutePath: file.AbsolutePath,
                    RelativePath: file.RelativePath,
                    Content: content,
                    ResolvedVia: "ddl-grep");
        }

        return null;
    }

    private async Task<PlSqlResolution> BuildResolution(
        FileNode file,
        string name,
        PlSqlObjectKind kind,
        string via,
        CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(file.AbsolutePath, ct);
        return new PlSqlResolution(
            Name: name,
            Kind: kind,
            AbsolutePath: file.AbsolutePath,
            RelativePath: file.RelativePath,
            Content: content,
            ResolvedVia: via);
    }

    private static Regex BuildDdlPattern(string name)
    {
        var n = Regex.Escape(name);
        // CREATE [OR REPLACE] PROCEDURE/FUNCTION/PACKAGE [BODY]/TABLE/VIEW/TYPE [schema.]NAME
        return new Regex(
            $@"\bCREATE\b.*?\b(?:PROCEDURE|FUNCTION|PACKAGE(?:\s+BODY)?|TABLE|VIEW|TYPE)\b\s+(?:\w+\.)?{n}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }
}
