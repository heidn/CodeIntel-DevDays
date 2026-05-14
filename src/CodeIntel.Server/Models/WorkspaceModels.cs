namespace CodeIntel.Server.Models;

public enum Language { CSharp, TypeScript, Java, Sql }

public record Workspace(
    string Id,
    /// <summary>
    /// User-facing path the workspace was loaded from. May be either a folder
    /// (TS/Java/SQL repo mode) or a project file (.sln / .csproj). Preserved
    /// for round-tripping and display; consumers that need the workspace folder
    /// MUST use <see cref="RootFolder"/> to avoid file-vs-folder ambiguity.
    /// </summary>
    string ProjectPath,
    string ProjectName,
    List<ProjectNode> Projects,
    DateTime LoadedAt,
    Language Language = Language.CSharp,
    /// <summary>
    /// Always a directory. Equals the parent of <see cref="EntryFile"/> when one exists,
    /// otherwise the user-supplied folder.
    /// </summary>
    string? RootFolder = null,
    /// <summary>
    /// The .sln / .csproj / tsconfig.json the workspace was loaded from, if any.
    /// Null for plain-folder loads (PL/SQL, JS without a tsconfig).
    /// </summary>
    string? EntryFile = null
);

public record ProjectNode(
    string Name,
    string Path,
    List<FileNode> Files
);

public record FileNode(
    string AbsolutePath,
    string RelativePath,
    string FileName,
    int LineCount,
    long SizeBytes
);

/// <summary>
/// Assembled context passed to the LLM. May include extracted metadata
/// or raw file text, depending on mode and token budget.
/// </summary>
public record CodeContext(
    List<FileContext> Files,
    int EstimatedTokens,
    Language Language = Language.CSharp
);

public record FileContext(
    string FilePath,
    string RelativePath,
    string Content,
    bool IsExtractedSummary,
    bool IsResolvedDependency = false,
    /// <summary>
    /// When non-null, this FileContext holds a slice of a larger file (lines
    /// <see cref="ChunkStartLine"/>–<see cref="ChunkEndLine"/> of <see cref="ChunkTotalLines"/>).
    /// PromptTemplateService labels the file accordingly so the model knows it's not seeing the whole file.
    /// </summary>
    int? ChunkStartLine = null,
    int? ChunkEndLine = null,
    int? ChunkTotalLines = null
);

/// <summary>
/// Per-chunk metadata threaded through the orchestrator and prompt builder when
/// a single file is split into multiple sequential analysis passes.
/// </summary>
public record ChunkContext(
    int Index,                  // 1-based
    int Total,                  // total chunks for this file
    string FilePath,            // the file being chunked (relative-or-absolute, used for display)
    int StartLine,
    int EndLine,
    int TotalLines,
    string? CarryOverNotes      // narrative summary of prior chunks; null on the first chunk
);

public record DefinitionLocation(
    string FilePath,
    int Line,
    int Character,
    string SymbolName
);
