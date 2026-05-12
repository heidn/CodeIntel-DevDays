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
    Task InitializeAsync(CancellationToken ct = default);
    IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct = default);
}

/// <summary>
/// Singleton service hosting one loaded GGUF model.
/// Inference is serialized via a semaphore - LLamaSharp contexts are not thread-safe.
/// </summary>
public sealed class LlamaSharpService : ILlmService, IDisposable
{
    private readonly ILogger<LlamaSharpService> _logger;
    private readonly LlmOptions _options;
    private readonly string _contentRoot;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);

    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private StatelessExecutor? _executor;
    private string _modelName = "(not loaded)";
    private bool _isReady;

    public bool IsReady => _isReady;
    public string ModelName => _modelName;

    public LlamaSharpService(IOptions<LlmOptions> options, IWebHostEnvironment env, ILogger<LlamaSharpService> logger)
    {
        _options = options.Value;
        _contentRoot = env.ContentRootPath;
        _logger = logger;
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
            var parameters = new ModelParams(modelPath)
            {
                ContextSize = (uint)_options.ContextSize,
                GpuLayerCount = _options.GpuLayerCount,
                Threads = _options.Threads
            };

            _weights = LLamaWeights.LoadFromFile(parameters);
            _context = _weights.CreateContext(parameters);
            _executor = new StatelessExecutor(_weights, parameters);
            _modelName = Path.GetFileNameWithoutExtension(modelPath);
            _isReady = true;
            _logger.LogInformation("Model loaded: {Name}", _modelName);
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
                MaxTokens = _options.MaxResponseTokens,
                AntiPrompts = new List<string> { "<|im_end|>", "<|endoftext|>" },
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = _options.Temperature,
                    TopP = 0.95f,
                    TopK = 40
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
        _context?.Dispose();
        _weights?.Dispose();
        _inferenceLock.Dispose();
    }
}
