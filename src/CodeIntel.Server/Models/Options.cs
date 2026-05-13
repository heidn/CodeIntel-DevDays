namespace CodeIntel.Server.Models;

public enum LlmBackend { Auto, Cpu, Vulkan }

public class LlmOptions
{
    public string ModelPath { get; set; } = "";
    public string ModelSha256 { get; set; } = "";
    public int ContextSize { get; set; } = 8192;
    public int GpuLayerCount { get; set; } = 0;
    public int MaxResponseTokens { get; set; } = 1024;
    public float Temperature { get; set; } = 0.2f;
    public int? Threads { get; set; }
    public LlmBackend Backend { get; set; } = LlmBackend.Auto;
}

public class AnalysisOptions
{
    public int MaxContextTokens { get; set; } = 5000;
    public double TokensPerCharEstimate { get; set; } = 0.25;
    public string ReportOutputPath { get; set; } = "docs/codeintel";
    public bool MaintainIndex { get; set; } = true;
    public int MaxAgenticIterations { get; set; } = 3;
    public int IdleTokenTimeoutSeconds { get; set; } = 90;
    public int OverallTimeoutSeconds { get; set; } = 600;
    /// <summary>
    /// LRU cap on persisted analysis/trace rows. Older rows are pruned after each save.
    /// </summary>
    public int MaxPersistedResults { get; set; } = 200;
    /// <summary>
    /// LRU cap on loaded Roslyn workspaces. Evicting frees the (often large) MSBuildWorkspace.
    /// </summary>
    public int MaxLoadedWorkspaces { get; set; } = 3;
    /// <summary>
    /// When true, repeat runs with the same {presetKey, modelName, file-content hashes}
    /// short-circuit to the cached result instead of re-prompting.
    /// </summary>
    public bool EnableResultCache { get; set; } = true;
    /// <summary>
    /// Maximum age of a cache hit before it's ignored and the analysis re-runs.
    /// </summary>
    public int ResultCacheTtlHours { get; set; } = 24 * 7;
    /// <summary>
    /// Per-IP rate limit: how many runs in the window before further requests are rejected.
    /// </summary>
    public int RateLimitRunsPerMinute { get; set; } = 5;
}

public class DataOptions
{
    /// <summary>
    /// Absolute or content-root-relative path to the SQLite file backing analysis history.
    /// </summary>
    public string DatabasePath { get; set; } = "data/codeintel.db";
}
