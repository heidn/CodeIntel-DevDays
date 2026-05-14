using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using CodeIntel.Server.Models;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Hosting;

namespace CodeIntel.Server.Services;

public interface ILlmService
{
    bool IsReady { get; }
    string ModelName { get; }
    string BackendName { get; }
    Task InitializeAsync(CancellationToken ct = default);
    IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct = default);
}

/// <summary>
/// Singleton service hosting one loaded GGUF model.
/// Inference is serialized via a semaphore - LLamaSharp contexts are not thread-safe.
/// On startup, attempts to use the most powerful available compute (Vulkan → CPU).
/// </summary>
public sealed class LlamaSharpService : ILlmService, IDisposable
{
    private readonly ILogger<LlamaSharpService> _logger;
    private readonly LlmOptions _options;
    private readonly string _contentRoot;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);

    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private string _modelName   = "(not loaded)";
    private string _backendName = "cpu";
    private bool _isReady;
    private bool _disposed;

    public bool IsReady      => _isReady;
    public string ModelName  => _modelName;
    public string BackendName => _backendName;

    public LlamaSharpService(IOptions<LlmOptions> options, IWebHostEnvironment env, ILogger<LlamaSharpService> logger)
    {
        _options     = options.Value;
        _contentRoot = env.ContentRootPath;
        _logger      = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_isReady) return;

        var modelPath = ResolveModelPath(_options.ModelPath);
        if (!File.Exists(modelPath))
        {
            _logger.LogWarning(
                "Model file not found at {Path}. LLM features will be unavailable until a GGUF model is placed there.",
                modelPath);
            return;
        }

        await VerifyModelHashAsync(modelPath, ct);

        await Task.Run(() =>
        {
            _logger.LogInformation("Loading model from {Path}", modelPath);

            var (parameters, backendName, probedWeights) = ResolveBackend(modelPath);

            // Reuse the probe's loaded weights if the backend resolver already paid
            // the cost of reading the 4.7 GB GGUF from disk; otherwise load now.
            _weights     = probedWeights ?? LLamaWeights.LoadFromFile(parameters);
            _executor    = new StatelessExecutor(_weights, parameters);
            _modelName   = Path.GetFileNameWithoutExtension(modelPath);
            _backendName = backendName;
            _isReady     = true;
            _logger.LogInformation("Model loaded: {Name} [{Backend}]", _modelName, _backendName);
        }, ct);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_isReady || _executor == null)
            throw new InvalidOperationException(
                "LLM is not initialized. Ensure a GGUF model is present and InitializeAsync has completed.");

        await _inferenceLock.WaitAsync(ct);
        try
        {
            var inferenceParams = new InferenceParams
            {
                MaxTokens  = _options.MaxResponseTokens,
                AntiPrompts = new List<string> { "<|im_end|>", "<|endoftext|>" },
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = _options.Temperature,
                    TopP        = 0.95f,
                    TopK        = 40
                }
            };

            await foreach (var text in _executor.InferAsync(prompt, inferenceParams, ct))
            {
                yield return text;
            }
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    /// <summary>
    /// Determines the compute backend to use and returns the parameters plus any
    /// already-loaded weights from the probe. When the probe succeeds we hand the
    /// loaded weights back to the caller — loading a 4.7 GB GGUF twice on startup
    /// was the previous behaviour and a noticeable cold-start tax.
    /// </summary>
    private (ModelParams parameters, string backendName, LLamaWeights? probedWeights) ResolveBackend(string modelPath)
    {
        ModelParams Make(int gpuLayers) => new ModelParams(modelPath)
        {
            ContextSize   = (uint)_options.ContextSize,
            GpuLayerCount = gpuLayers,
            Threads       = _options.Threads
        };

        if (_options.Backend == LlmBackend.Cpu)
        {
            _logger.LogInformation("Backend: cpu (explicit), GPU layers: 0");
            return (Make(0), "cpu", null);
        }

        // Auto or Vulkan — attempt GPU, fall back to CPU on failure.
        int gpuLayers = _options.GpuLayerCount > 0 ? _options.GpuLayerCount : 20;
        LLamaWeights? probe = null;
        try
        {
            var p = Make(gpuLayers);
            // Probe load confirms the GPU path works. We keep the weights — the
            // outer InitializeAsync will reuse them instead of paying the disk
            // cost a second time.
            probe = LLamaWeights.LoadFromFile(p);
            _logger.LogInformation("Backend: vulkan, GPU layers: {Layers}", gpuLayers);
            return (p, "vulkan", probe);
        }
        catch (Exception ex)
        {
            probe?.Dispose();
            if (_options.Backend == LlmBackend.Vulkan)
                throw new InvalidOperationException(
                    $"Vulkan backend explicitly requested but failed to load: {ex.Message}", ex);

            _logger.LogWarning("Vulkan unavailable ({Reason}), falling back to CPU", ex.Message);
            return (Make(0), "cpu", null);
        }
    }

    private async Task VerifyModelHashAsync(string modelPath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.ModelSha256))
        {
            _logger.LogWarning(
                "Llm:ModelSha256 is not configured. Set it to the SHA-256 of the model file to enable integrity verification. " +
                "Run: Get-FileHash \"{Path}\" -Algorithm SHA256", modelPath);
            return;
        }

        _logger.LogInformation("Verifying model file integrity...");
        await using var fs = File.OpenRead(modelPath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
        if (!hash.Equals(_options.ModelSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Model file hash mismatch — the file may have been replaced or tampered with. " +
                $"Expected {_options.ModelSha256}, got {hash}.");

        _logger.LogInformation("Model integrity verified");
    }

    private string ResolveModelPath(string configured)
    {
        if (Path.IsPathRooted(configured)) return configured;
        return Path.GetFullPath(Path.Combine(_contentRoot, configured));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Flip readiness first so any racing StreamAsync caller fails fast on the
        // explicit check rather than NPE'ing on a freed native handle.
        _isReady = false;

        // StatelessExecutor holds no native handles of its own — it borrows from
        // _weights, so dropping the reference is sufficient before disposing weights.
        _executor = null;
        _weights?.Dispose();
        _inferenceLock.Dispose();
    }
}
