using System.Collections.Concurrent;
using CodeIntel.Server.Models;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Workspace = CodeIntel.Server.Models.Workspace;

namespace CodeIntel.Server.Services;

public interface IWorkspaceService
{
    Task<Workspace> LoadAsync(string path, CancellationToken ct = default);
    Workspace? GetWorkspace(string workspaceId);
    Task<string> ReadFileAsync(string workspaceId, string relativeOrAbsolutePath, CancellationToken ct = default);
    Solution? GetRoslynSolution(string workspaceId);
}

public sealed class WorkspaceService : IWorkspaceService, IDisposable
{
    private static bool _msbuildRegistered;
    private static readonly object _msbuildLock = new();

    private readonly ILogger<WorkspaceService> _logger;
    private readonly ConcurrentDictionary<string, LoadedWorkspace> _workspaces = new();

    private static readonly string[] TsExtensions = [".ts", ".tsx", ".js", ".jsx"];
    private static readonly string[] JavaExtensions = [".java"];
    private static readonly string[] CsExtensions = [".cs"];
    private static readonly string[] TsExcludeDirs = ["node_modules", "dist", ".next", "out", ".cache"];
    private static readonly string[] JavaExcludeDirs = ["target", "build", ".gradle"];
    private static readonly string[] TsExcludePatterns = [".min.js", ".d.ts"];

    public WorkspaceService(ILogger<WorkspaceService> logger) => _logger = logger;

    public async Task<Workspace> LoadAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty", nameof(path));

        var language = DetectLanguage(path);
        _logger.LogInformation("Loading {Path} as {Language}", path, language);

        return language == Language.CSharp
            ? await LoadCSharpAsync(path, ct)
            : await ScanFilesAsync(path, language, ct);
    }

    private static Language DetectLanguage(string path)
    {
        var lower = path.ToLowerInvariant();

        if (lower.EndsWith(".sln") || lower.EndsWith(".csproj"))
            return Language.CSharp;
        if (lower.EndsWith("tsconfig.json") || lower.EndsWith("package.json"))
            return Language.TypeScript;
        if (lower.EndsWith("pom.xml") || lower.EndsWith("build.gradle") || lower.EndsWith("build.gradle.kts"))
            return Language.Java;

        if (!Directory.Exists(path)) return Language.CSharp;

        if (Directory.GetFiles(path, "*.sln").Length > 0 || Directory.GetFiles(path, "*.csproj").Length > 0)
            return Language.CSharp;
        if (File.Exists(Path.Combine(path, "tsconfig.json")) || File.Exists(Path.Combine(path, "package.json")))
            return Language.TypeScript;
        if (File.Exists(Path.Combine(path, "pom.xml")) || Directory.GetFiles(path, "build.gradle*").Length > 0)
            return Language.Java;

        return CountDominantLanguage(path);
    }

    private static Language CountDominantLanguage(string dir)
    {
        var counts = new Dictionary<Language, int>
        {
            [Language.CSharp] = 0, [Language.TypeScript] = 0, [Language.Java] = 0,
        };
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (CsExtensions.Contains(ext)) counts[Language.CSharp]++;
                else if (TsExtensions.Contains(ext)) counts[Language.TypeScript]++;
                else if (JavaExtensions.Contains(ext)) counts[Language.Java]++;
            }
        }
        catch { /* best effort */ }
        return counts.OrderByDescending(kv => kv.Value).First().Key;
    }

    private async Task<Workspace> LoadCSharpAsync(string path, CancellationToken ct)
    {
        EnsureMsBuildRegistered();

        string slnPath;
        if (File.Exists(path))
        {
            slnPath = path;
        }
        else
        {
            slnPath = Directory.GetFiles(path, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault()
                ?? Directory.GetFiles(path, "*.csproj", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new FileNotFoundException($"No .sln or .csproj found in {path}");
        }

        _logger.LogInformation("Loading C# solution {Path}", slnPath);

        var msWorkspace = MSBuildWorkspace.Create();
        msWorkspace.WorkspaceFailed += (_, args) =>
            _logger.LogWarning("Workspace warning: {Diagnostic}", args.Diagnostic.Message);

        Solution solution;
        if (slnPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            solution = await msWorkspace.OpenSolutionAsync(slnPath, cancellationToken: ct);
        else
        {
            var project = await msWorkspace.OpenProjectAsync(slnPath, cancellationToken: ct);
            solution = project.Solution;
        }

        var projects = new List<ProjectNode>();
        foreach (var project in solution.Projects.OrderBy(p => p.Name))
        {
            var files = new List<FileNode>();
            foreach (var doc in project.Documents.OrderBy(d => d.FilePath))
            {
                if (string.IsNullOrEmpty(doc.FilePath) || !File.Exists(doc.FilePath)) continue;
                var info = new FileInfo(doc.FilePath);
                var lineCount = 0;
                try { lineCount = File.ReadLines(doc.FilePath).Count(); } catch { }
                files.Add(new FileNode(
                    AbsolutePath: doc.FilePath,
                    RelativePath: GetRelativePath(slnPath, doc.FilePath),
                    FileName: Path.GetFileName(doc.FilePath),
                    LineCount: lineCount,
                    SizeBytes: info.Length
                ));
            }
            projects.Add(new ProjectNode(Name: project.Name, Path: project.FilePath ?? "", Files: files));
        }

        var workspaceId = Guid.NewGuid().ToString("N")[..12];
        var workspace = new Workspace(
            Id: workspaceId,
            ProjectPath: slnPath,
            ProjectName: Path.GetFileNameWithoutExtension(slnPath),
            Projects: projects,
            LoadedAt: DateTime.UtcNow,
            Language: Language.CSharp
        );
        _workspaces[workspaceId] = new LoadedWorkspace(workspace, msWorkspace, solution);
        _logger.LogInformation("Loaded {ProjectCount} projects, {FileCount} files in workspace {Id}",
            projects.Count, projects.Sum(p => p.Files.Count), workspaceId);
        return workspace;
    }

    private async Task<Workspace> ScanFilesAsync(string path, Language language, CancellationToken ct)
    {
        var rootDir = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        if (!Directory.Exists(rootDir))
            throw new DirectoryNotFoundException($"Directory not found: {rootDir}");

        var extensions = language == Language.Java ? JavaExtensions : TsExtensions;
        var excludeDirs = language == Language.Java ? JavaExcludeDirs : TsExcludeDirs;
        var excludePatterns = language == Language.TypeScript ? TsExcludePatterns : [];

        _logger.LogInformation("Scanning {Root} for {Language} files", rootDir, language);

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
        var workspace = new Workspace(
            Id: workspaceId,
            ProjectPath: path,
            ProjectName: Path.GetFileName(rootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Projects: projects,
            LoadedAt: DateTime.UtcNow,
            Language: language
        );
        _workspaces[workspaceId] = new LoadedWorkspace(workspace, null, null);
        _logger.LogInformation("Scanned {FileCount} {Language} files in workspace {Id}",
            projects.Sum(p => p.Files.Count), language, workspaceId);
        return workspace;
    }

    public Workspace? GetWorkspace(string workspaceId) =>
        _workspaces.TryGetValue(workspaceId, out var loaded) ? loaded.Workspace : null;

    public Solution? GetRoslynSolution(string workspaceId) =>
        _workspaces.TryGetValue(workspaceId, out var loaded) ? loaded.Solution : null;

    public async Task<string> ReadFileAsync(string workspaceId, string path, CancellationToken ct = default)
    {
        var ws = GetWorkspace(workspaceId) ?? throw new InvalidOperationException("Workspace not loaded");
        var rootDir = File.Exists(ws.ProjectPath) ? Path.GetDirectoryName(ws.ProjectPath)! : ws.ProjectPath;

        var absolutePath = Path.IsPathRooted(path) ? path : Path.Combine(rootDir, path);
        var resolved = Path.GetFullPath(absolutePath);
        var resolvedRoot = Path.GetFullPath(rootDir);

        if (!resolved.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path is outside workspace root.");

        if (!File.Exists(resolved))
            throw new FileNotFoundException($"File not found: {resolved}");

        return await File.ReadAllTextAsync(resolved, ct);
    }

    private static string GetRelativePath(string basePath, string filePath)
    {
        var baseDir = Directory.Exists(basePath) ? basePath : Path.GetDirectoryName(basePath);
        if (string.IsNullOrEmpty(baseDir)) return filePath;
        try { return Path.GetRelativePath(baseDir, filePath); }
        catch { return filePath; }
    }

    private static void EnsureMsBuildRegistered()
    {
        if (_msbuildRegistered) return;
        lock (_msbuildLock)
        {
            if (_msbuildRegistered) return;
            if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
            _msbuildRegistered = true;
        }
    }

    public void Dispose()
    {
        foreach (var loaded in _workspaces.Values)
            loaded.MsWorkspace?.Dispose();
        _workspaces.Clear();
    }

    private record LoadedWorkspace(Workspace Workspace, MSBuildWorkspace? MsWorkspace, Solution? Solution);
}
