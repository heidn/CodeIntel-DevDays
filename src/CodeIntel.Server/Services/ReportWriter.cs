using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIntel.Server.Models;
using Microsoft.Extensions.Options;

namespace CodeIntel.Server.Services;

public record ReportWriteResult(string AbsolutePath, string RelativePath);

public interface IReportWriter
{
    Task<ReportWriteResult?> WriteAsync(AnalysisResult result, Workspace workspace, string? overrideOutputPath, CancellationToken ct);
    Task<ReportWriteResult?> WriteTraceAsync(TraceResult result, Workspace workspace, string? overrideOutputPath, CancellationToken ct);
}

public class ReportWriter : IReportWriter
{
    private static readonly SemaphoreSlim _indexLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IReportGenerator _generator;
    private readonly AnalysisOptions _options;
    private readonly ILogger<ReportWriter> _logger;

    public ReportWriter(
        IReportGenerator generator,
        IOptions<AnalysisOptions> options,
        ILogger<ReportWriter> logger)
    {
        _generator = generator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ReportWriteResult?> WriteAsync(AnalysisResult result, Workspace workspace, string? overrideOutputPath, CancellationToken ct)
    {
        var repoRoot = ResolveWorkspaceRoot(workspace);
        if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
        {
            _logger.LogWarning("Workspace root not found: {Path}", workspace.ProjectPath);
            return null;
        }

        var relativeFolder = NormalizeRelativeFolder(overrideOutputPath) ?? _options.ReportOutputPath;
        var outDir = Path.Combine(repoRoot, relativeFolder);

        // Guard against escape outside the workspace root.
        var resolvedOut = Path.GetFullPath(outDir);
        var resolvedRoot = Path.GetFullPath(repoRoot);
        if (!resolvedOut.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Refused to write report outside workspace root: {Out}", resolvedOut);
            return null;
        }

        try
        {
            Directory.CreateDirectory(outDir);
            EnsureFolderReadme(outDir);

            var filename = BuildFilename(result);
            var fullPath = Path.Combine(outDir, filename);
            var md = _generator.GenerateMarkdown(result, filename);
            await File.WriteAllTextAsync(fullPath, md, ct);

            if (_options.MaintainIndex)
                await UpdateIndexAsync(outDir, filename, result, ct);

            var relative = Path
                .Combine(relativeFolder, filename)
                .Replace('\\', '/');

            _logger.LogInformation("Wrote analysis report to {Path}", fullPath);
            return new ReportWriteResult(fullPath, relative);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write report to {OutDir}", outDir);
            return null;
        }
    }

    public async Task<ReportWriteResult?> WriteTraceAsync(TraceResult result, Workspace workspace, string? overrideOutputPath, CancellationToken ct)
    {
        var repoRoot = ResolveWorkspaceRoot(workspace);
        if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
        {
            _logger.LogWarning("Workspace root not found: {Path}", workspace.ProjectPath);
            return null;
        }

        var relativeFolder = NormalizeRelativeFolder(overrideOutputPath) ?? _options.ReportOutputPath;
        var outDir = Path.Combine(repoRoot, relativeFolder);

        var resolvedOut = Path.GetFullPath(outDir);
        var resolvedRoot = Path.GetFullPath(repoRoot);
        if (!resolvedOut.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Refused to write trace report outside workspace root: {Out}", resolvedOut);
            return null;
        }

        try
        {
            Directory.CreateDirectory(outDir);
            EnsureFolderReadme(outDir);

            var filename = BuildTraceFilename(result);
            var fullPath = Path.Combine(outDir, filename);
            var md = _generator.GenerateTraceMarkdown(result, filename);
            await File.WriteAllTextAsync(fullPath, md, ct);

            if (_options.MaintainIndex)
                await UpdateIndexForTraceAsync(outDir, filename, result, ct);

            var relative = Path
                .Combine(relativeFolder, filename)
                .Replace('\\', '/');

            _logger.LogInformation("Wrote trace report to {Path}", fullPath);
            return new ReportWriteResult(fullPath, relative);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write trace report to {OutDir}", outDir);
            return null;
        }
    }

    private static string BuildTraceFilename(TraceResult r)
    {
        var date = r.CompletedAt.ToString("yyyy-MM-dd");
        var label = ShortenSymbolFqn(r.EntryPointSymbolFqn);
        var dir = r.Direction.ToString().ToLowerInvariant();
        var id = r.Id.ToString("N")[..8];
        return $"{date}-trace-{dir}-{Slugify(label)}-{id}.md";
    }

    /// <summary>
    /// Reduce a Roslyn ToDisplayString result to a short "Class.Method" label.
    /// Strips the parameter list (which itself contains dots, e.g. System.Threading.CancellationToken)
    /// and keeps only the last two name segments.
    /// </summary>
    internal static string ShortenSymbolFqn(string fqn)
    {
        var noParams = fqn;
        var paren = noParams.IndexOf('(');
        if (paren >= 0) noParams = noParams.Substring(0, paren);
        var parts = noParams.Split('.');
        if (parts.Length == 0) return fqn;
        if (parts.Length == 1) return parts[0];
        return $"{parts[^2]}.{parts[^1]}";
    }

    private async Task UpdateIndexForTraceAsync(string outDir, string filename, TraceResult result, CancellationToken ct)
    {
        await _indexLock.WaitAsync(ct);
        try
        {
            var indexJsonPath = Path.Combine(outDir, ".codeintel-index.json");
            var entries = await LoadIndexAsync(indexJsonPath, ct);

            entries.RemoveAll(e => string.Equals(e.Filename, filename, StringComparison.OrdinalIgnoreCase));
            entries.Insert(0, new IndexEntry(
                Filename: filename,
                CompletedAt: result.CompletedAt,
                Kind: "trace",
                Mode: null,
                PresetKey: null,
                FreeTextPrompt: null,
                FilesAnalyzed: null,
                FindingCount: null,
                HighSeverityCount: null,
                EntryPointFqn: result.EntryPointSymbolFqn,
                Direction: result.Direction.ToString(),
                NodeCount: result.Nodes.Count,
                Depth: result.Depth,
                Truncated: result.Truncated,
                DurationSeconds: result.Duration.TotalSeconds
            ));

            await SaveIndexJsonAsync(indexJsonPath, entries, ct);
            await SaveIndexMdAsync(Path.Combine(outDir, "INDEX.md"), entries, ct);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private static string? NormalizeRelativeFolder(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var trimmed = input.Trim().Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string ResolveWorkspaceRoot(Workspace ws) =>
        File.Exists(ws.ProjectPath)
            ? Path.GetDirectoryName(ws.ProjectPath) ?? ""
            : ws.ProjectPath;

    private static string BuildFilename(AnalysisResult r)
    {
        var date = r.CompletedAt.ToString("yyyy-MM-dd");
        var slug = r.Mode == AnalysisMode.Preset
            ? Slugify(r.PresetKey ?? "preset")
            : "custom";
        var id = r.Id.ToString("N")[..8];
        return $"{date}-{slug}-{id}.md";
    }

    private static string Slugify(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is '-' or '_' or ' ') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(slug) ? "report" : slug;
    }

    private static void EnsureFolderReadme(string outDir)
    {
        var readmePath = Path.Combine(outDir, "README.md");
        if (File.Exists(readmePath)) return;

        const string content = """
            # CodeIntel Reports

            Auto-generated analysis reports. Each file is a self-contained markdown
            report plus a "Copilot Next Step" prompt template — reference any of
            them in Copilot Chat with `#file:<filename>`.

            `INDEX.md` lists all reports newest-first. The `.codeintel-index.json`
            sidecar is the canonical source — `INDEX.md` is regenerated from it.

            These files are intended to be committed and shared with the team as
            living documentation. Delete freely; the tool will regenerate.
            """;
        File.WriteAllText(readmePath, content);
    }

    private async Task UpdateIndexAsync(string outDir, string filename, AnalysisResult result, CancellationToken ct)
    {
        await _indexLock.WaitAsync(ct);
        try
        {
            var indexJsonPath = Path.Combine(outDir, ".codeintel-index.json");
            var entries = await LoadIndexAsync(indexJsonPath, ct);

            entries.RemoveAll(e => string.Equals(e.Filename, filename, StringComparison.OrdinalIgnoreCase));
            entries.Insert(0, new IndexEntry(
                Filename: filename,
                CompletedAt: result.CompletedAt,
                Kind: "analysis",
                Mode: result.Mode.ToString(),
                PresetKey: result.PresetKey,
                FreeTextPrompt: result.FreeTextPrompt,
                FilesAnalyzed: result.AnalyzedFiles,
                FindingCount: result.Findings.Count,
                HighSeverityCount: result.Findings.Count(f => f.Severity == Severity.Bug),
                EntryPointFqn: null,
                Direction: null,
                NodeCount: null,
                Depth: null,
                Truncated: null,
                DurationSeconds: result.Duration.TotalSeconds
            ));

            await SaveIndexJsonAsync(indexJsonPath, entries, ct);
            await SaveIndexMdAsync(Path.Combine(outDir, "INDEX.md"), entries, ct);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private static async Task<List<IndexEntry>> LoadIndexAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return new();
        try
        {
            await using var stream = File.OpenRead(path);
            var list = await JsonSerializer.DeserializeAsync<List<IndexEntry>>(stream, JsonOpts, ct);
            return list ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // corrupt index — start fresh
            return new();
        }
    }

    private static async Task SaveIndexJsonAsync(string path, List<IndexEntry> entries, CancellationToken ct)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, entries, JsonOpts, ct);
    }

    private static async Task SaveIndexMdAsync(string path, List<IndexEntry> entries, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# CodeIntel Reports");
        sb.AppendLine();
        sb.AppendLine("Auto-maintained. Reference any report in Copilot Chat with `#file:<filename>`.");
        sb.AppendLine();
        sb.AppendLine("| Date | Type | Subject | Result | Report |");
        sb.AppendLine("|---|---|---|---|---|");

        foreach (var e in entries)
        {
            string type;
            string subject;
            string result;

            if (e.Kind == "trace")
            {
                var dir = e.Direction?.ToLowerInvariant() ?? "trace";
                type = $"trace-{dir}";
                subject = e.EntryPointFqn is null
                    ? "`(unknown)`"
                    : $"`{ShortenSymbolFqn(e.EntryPointFqn)}`";
                var truncatedNote = e.Truncated == true ? "+" : "";
                result = $"{e.NodeCount ?? 0} nodes{truncatedNote}";
            }
            else
            {
                type = e.Mode == nameof(AnalysisMode.FreeText)
                    ? "custom"
                    : (e.PresetKey ?? "preset");
                var files = e.FilesAnalyzed;
                subject = files is null || files.Count == 0
                    ? "—"
                    : files.Count switch
                    {
                        1 => $"`{Path.GetFileName(files[0])}`",
                        <= 3 => string.Join(", ", files.Select(f => $"`{Path.GetFileName(f)}`")),
                        _ => $"{files.Count} files",
                    };
                var fc = e.FindingCount ?? 0;
                var hi = e.HighSeverityCount ?? 0;
                result = hi > 0 ? $"{fc} ({hi} high)" : (fc > 0 ? fc.ToString() : "—");
            }

            sb.AppendLine($"| {e.CompletedAt:yyyy-MM-dd HH:mm} | {type} | {subject} | {result} | [open]({e.Filename}) |");
        }

        await File.WriteAllTextAsync(path, sb.ToString(), ct);
    }

    private record IndexEntry(
        string Filename,
        DateTime CompletedAt,
        string Kind,                    // "analysis" | "trace"
        string? Mode,                   // analysis only
        string? PresetKey,              // analysis only
        string? FreeTextPrompt,         // analysis only
        List<string>? FilesAnalyzed,    // analysis only
        int? FindingCount,              // analysis only
        int? HighSeverityCount,         // analysis only
        string? EntryPointFqn,          // trace only
        string? Direction,              // trace only
        int? NodeCount,                 // trace only
        int? Depth,                     // trace only
        bool? Truncated,                // trace only
        double DurationSeconds
    );
}
