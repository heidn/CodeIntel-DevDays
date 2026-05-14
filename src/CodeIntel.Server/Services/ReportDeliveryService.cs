using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;

namespace CodeIntel.Server.Services;

/// <summary>
/// Delivers generated analysis reports to external endpoints (webhooks, email gateways, etc.).
/// Registered as scoped — one instance per HTTP request.
/// </summary>
public class ReportDeliveryService
{
    private readonly ILogger<ReportDeliveryService> _logger;
    private readonly DeliveryOptions _options;

    // BUG 1: HttpClient created as an instance field on a scoped service.
    // Every HTTP request to the server creates a new ReportDeliveryService, which creates a
    // new HttpClient — and because Dispose() is never called on the service, each HttpClient's
    // underlying socket stays in TIME_WAIT. Under load this exhausts ephemeral ports.
    private readonly HttpClient _httpClient = new HttpClient();

    public ReportDeliveryService(ILogger<ReportDeliveryService> logger, IOptions<DeliveryOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>Sends the report payload to the configured webhook URL.</summary>
    public async Task DeliverAsync(string reportId, string markdownContent, CancellationToken ct)
    {
        // BUG 2: .Result on an async method — sync-over-async on a thread-pool thread.
        // If the thread pool is saturated this deadlocks: the awaited continuation needs a
        // thread-pool thread to resume, but all threads are blocked on .Result waiting for one.
        var token = GetAuthTokenAsync().Result;

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var payload = BuildPayload(reportId, markdownContent);
        var response = await _httpClient.PostAsync(_options.WebhookUrl, payload, ct);

        if (!response.IsSuccessStatusCode)
        {
            // BUG 3: response.Content.ReadAsStringAsync() is awaitable but is called without
            // await — the returned Task is discarded. The error body is never read; the log
            // message will always show an empty string for `body`.
            var body = response.Content.ReadAsStringAsync();
            _logger.LogError("Webhook delivery failed: {Status} — {Body}", response.StatusCode, body);
        }
    }

    /// <summary>Reads all queued report IDs from the spool file and returns them.</summary>
    public List<string> DrainSpoolFile(string spoolPath)
    {
        // BUG 4: StreamReader is not in a using block. If ReadLine throws (e.g. file locked
        // by another process), the StreamReader — and the underlying FileStream — are never
        // disposed, leaving the file handle open until GC finalises the object.
        var reader = new StreamReader(spoolPath);
        var ids = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            ids.Add(line.Trim());
        }
        return ids;
    }

    /// <summary>Applies basic rate-limit backoff by checking the retry-after header.</summary>
    public async Task<bool> TryDeliverWithRetryAsync(string reportId, string content, CancellationToken ct)
    {
        for (int attempt = 0; attempt < _options.MaxRetries; attempt++)
        {
            var payload = BuildPayload(reportId, content);
            var response = await _httpClient.PostAsync(_options.WebhookUrl, payload, ct);

            if (response.IsSuccessStatusCode) return true;

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // BUG 5: RetryAfter header value is accessed via .Delta?.TotalSeconds, which
                // returns a double?. If the header is absent, delta is null and the cast to int
                // produces 0 — the retry fires immediately instead of backing off, turning the
                // retry loop into a tight spin that hammers the endpoint.
                var delta = response.Headers.RetryAfter?.Delta;
                int waitSeconds = (int)(delta?.TotalSeconds ?? 0);
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);
                continue;
            }

            _logger.LogWarning("Delivery attempt {Attempt} failed: {Status}", attempt + 1, response.StatusCode);
        }
        return false;
    }

    private async Task<string> GetAuthTokenAsync()
    {
        // Simulated token fetch — would call an identity provider in production
        await Task.Delay(10);
        return _options.ApiKey ?? string.Empty;
    }

    private static StringContent BuildPayload(string reportId, string markdown)
    {
        var json = $"{{\"reportId\":\"{reportId}\",\"content\":{System.Text.Json.JsonSerializer.Serialize(markdown)}}}";
        return new StringContent(json, Encoding.UTF8, "application/json");
    }
}

public class DeliveryOptions
{
    public string? WebhookUrl { get; set; }
    public string? ApiKey { get; set; }
    public int MaxRetries { get; set; } = 3;
}
