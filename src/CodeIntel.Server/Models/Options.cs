namespace CodeIntel.Server.Models;

public enum LlmBackend { Auto, Cpu, Vulkan }

public class LlmOptions
{
    public string ModelPath { get; set; } = "";
    public string ModelSha256 { get; set; } = "";
    public int ContextSize { get; set; } = 8192;
    public int GpuLayerCount { get; set; } = 0;
    public int MaxResponseTokens { get; set; } = 2048;
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
}
