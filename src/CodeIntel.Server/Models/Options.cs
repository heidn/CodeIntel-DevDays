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

    /// <summary>
    /// When true, files that exceed <see cref="MaxContextTokens"/> are split into
    /// sequential chunks (on natural language boundaries where possible) rather than
    /// silently truncated. Each chunk runs its own agentic loop; findings accumulate
    /// across chunks and a small "carry-over notes" block is fed forward so the model
    /// retains awareness of earlier symbols. Disable to fall back to single-pass
    /// truncation (useful for diagnosing chunking-related differences).
    /// </summary>
    public bool EnableAutoChunking { get; set; } = true;

    /// <summary>
    /// Token budget reserved per chunk for the carry-over notes block (the prefix
    /// summarising what earlier chunks already covered). Effective per-chunk file
    /// budget = <see cref="MaxContextTokens"/> minus this reserve.
    /// </summary>
    public int ChunkCarryOverReserveTokens { get; set; } = 300;

    /// <summary>
    /// Hard ceiling on chunks per file. Protects against pathologically large files
    /// from creating dozens of sequential agentic loops.
    /// </summary>
    public int MaxChunksPerFile { get; set; } = 8;
}

public class DataOptions
{
    /// <summary>
    /// Absolute or content-root-relative path to the SQLite file backing analysis history.
    /// </summary>
    public string DatabasePath { get; set; } = "data/codeintel.db";
}

public class LspOptions
{
    /// <summary>
    /// Master switch. When false, TypeScript workspaces load and use the file-tree
    /// path only — no semantic features (callers/callees/definition) for TS.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Command to launch the TypeScript LSP. Defaults to the typescript-language-server
    /// binary as published by npm. Override to point at vtsls, biome, or a wrapper script.
    /// On Windows the shim is `typescript-language-server.cmd`; on Linux/Mac it's
    /// `typescript-language-server` (no extension).
    /// </summary>
    public string TypeScriptCommand { get; set; } =
        OperatingSystem.IsWindows() ? "typescript-language-server.cmd" : "typescript-language-server";

    /// <summary>
    /// Args passed to the LSP binary. `--stdio` is required for LSP-over-stdio mode.
    /// </summary>
    public string[] TypeScriptArgs { get; set; } = ["--stdio"];

    /// <summary>
    /// Max seconds to wait for the `initialize` reply before giving up and falling back
    /// to text search.
    /// </summary>
    public int InitializeTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Max seconds a single LSP request waits before timing out. Defends against a
    /// hung child process taking down a long-running trace.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 15;
}
