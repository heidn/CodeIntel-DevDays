namespace CodeIntel.Server.Models;

public enum AnalysisMode
{
    Preset,
    FreeText
}

public enum Severity
{
    Info,
    Suggestion,
    Warning,
    Bug,
    DeadCode
}

public record PinnedSnippet(
    string AbsolutePath,
    int StartLine,
    int EndLine,
    string Text
);

public record AnalysisRequest(
    AnalysisMode Mode,
    string? PresetKey,
    string? FreeTextPrompt,
    List<string> SelectedFilePaths,
    string WorkspaceId,
    Guid? AnalysisId = null,
    PinnedSnippet? PinnedSnippet = null
);

public record Finding(
    Severity Severity,
    string Title,
    string Description,
    string? FilePath,
    int? LineNumber,
    string? CodeSnippet
);

public enum ContextRequestType
{
    File,
    Class,
    Method,
    CallersOf,
    CalleesOf,
    SearchCode,
    OracleObject,
}

public record ContextRequest(ContextRequestType Type, string Target);

public record ContextFulfillment(ContextRequest Request, string Content, bool Found);

public record AnalysisResult(
    Guid Id,
    DateTime StartedAt,
    DateTime CompletedAt,
    AnalysisMode Mode,
    string? PresetKey,
    string? FreeTextPrompt,
    List<string> AnalyzedFiles,
    List<Finding> Findings,
    string RawLlmOutput,
    int ContextTokens,
    TimeSpan Duration,
    string WorkspaceId,
    string? ReportPath = null,
    string? WorkspaceRoot = null,
    string? ContentHash = null
);
