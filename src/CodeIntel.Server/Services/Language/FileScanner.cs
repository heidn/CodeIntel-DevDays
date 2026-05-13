using CodeIntel.Server.Models;

namespace CodeIntel.Server.Services.LanguageBackends;

/// <summary>
/// Shared file-tree builder for non-C# backends. Walks a folder, filters by
/// extension + exclude patterns, groups by top-level subfolder for the UI
/// project list, and returns a <see cref="Workspace"/>.
///
/// Mirrors the pre-B1 <c>WorkspaceService.ScanFilesAsync</c> behaviour exactly
/// so non-C# workspaces look identical to the user after the refactor.
/// </summary>
public static class FileScanner
{
    public static async Task<Workspace> ScanAsync(
        string path,
        Language language,
        IReadOnlyList<string> extensions,
        IReadOnlyList<string> excludeDirs,
        IReadOnlyList<string> excludePatterns,
        CancellationToken ct)
    {
        var rootDir = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        if (!Directory.Exists(rootDir))
            throw new DirectoryNotFoundException($"Directory not found: {rootDir}");

        var allFiles = Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (!extensions.Contains(ext)) return false;
                if (excludePatterns.Any(p => f.EndsWith(p, StringComparison.OrdinalIgnoreCase))) return false;
                var parts = f.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
                return !excludeDirs.Any(ex => parts.Any(p => p.Equals(ex, StringComparison.OrdinalIgnoreCase)));
            })
            .OrderBy(f => f)
            .ToList();

        var groups = allFiles
            .GroupBy(f =>
            {
                var rel = Path.GetRelativePath(rootDir, f);
                var parts = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
                return parts.Length > 1 ? parts[0] : ".";
            })
            .OrderBy(g => g.Key)
            .ToList();

        var projects = new List<ProjectNode>();
        foreach (var group in groups)
        {
            var fileNodes = new List<FileNode>();
            foreach (var filePath in group)
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(filePath);
                var lineCount = 0;
                try { lineCount = await Task.Run(() => File.ReadLines(filePath).Count(), ct); } catch { }
                fileNodes.Add(new FileNode(
                    AbsolutePath: filePath,
                    RelativePath: Path.GetRelativePath(rootDir, filePath),
                    FileName: Path.GetFileName(filePath),
                    LineCount: lineCount,
                    SizeBytes: info.Length
                ));
            }
            var projectName = group.Key == "."
                ? Path.GetFileName(rootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : group.Key;
            projects.Add(new ProjectNode(
                Name: projectName,
                Path: group.Key == "." ? rootDir : Path.Combine(rootDir, group.Key),
                Files: fileNodes
            ));
        }

        var workspaceId = Guid.NewGuid().ToString("N")[..12];
        return new Workspace(
            Id: workspaceId,
            ProjectPath: path,
            ProjectName: Path.GetFileName(rootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Projects: projects,
            LoadedAt: DateTime.UtcNow,
            Language: language,
            RootFolder: rootDir,
            EntryFile: File.Exists(path) ? path : null
        );
    }
}
