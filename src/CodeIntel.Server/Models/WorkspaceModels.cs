namespace CodeIntel.Server.Models;

public enum Language { CSharp, TypeScript, Java, Sql }

public record Workspace(
    string Id,
    string ProjectPath,
    string ProjectName,
    List<ProjectNode> Projects,
    DateTime LoadedAt,
    Language Language = Language.CSharp
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
    bool IsResolvedDependency = false
);

public record DefinitionLocation(
    string FilePath,
    int Line,
    int Character,
    string SymbolName
);
