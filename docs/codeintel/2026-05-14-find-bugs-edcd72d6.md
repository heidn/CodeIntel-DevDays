# Code Intelligence Report

**Generated:** 2026-05-14 19:15:34 UTC  
**Mode:** Preset — find-bugs  
**Duration:** 46.9s  
**Context tokens:** ~1,284

## Scope

- `src\CodeIntel.Server\Services\ReportDeliveryService.cs`

## Findings

_No structured findings emitted. See raw output below._
---

## Copilot Next Step

The local model surfaced potential bugs. Use Copilot to verify, prioritize, and propose concrete fixes.

Reference this file in Copilot Chat:

```text
#file:2026-05-14-find-bugs-edcd72d6.md
```

Then ask:

```text
Review each finding above. For every high-severity item:
  1. Open the referenced file and confirm the bug actually exists.
  2. If real, propose a precise code edit (show the diff).
  3. Explain the failure mode and why the fix is correct.
  4. Note any related code paths that should also be checked.

If a finding is a false positive, say so and explain.
Group your output by file. End with a one-line recommendation for next action.
```

<details>
<summary>Raw LLM output</summary>

```
<finding>{
  "severity": "bug",
  "confidence": "high",
  "title": "HttpClient created as an instance field on a scoped service",
  "description": "When a new ReportDeliveryService is created (one per HTTP request), it instantiates a new HttpClient. Under load this exhausts ephemeral ports.",
  "filePath": "src\\CodeIntel.Server\Services\\ReportDeliveryService.cs",
  "lineNumber": 10,
  "codeSnippet": "private readonly HttpClient _httpClient = new HttpClient();"
}</finding>

<finding>{
  "severity": "bug",
  "confidence": "high",
  "title": "Sync-over-async with .Result on an async method",
  "description": "When DeliverAsync is called, it synchronously waits for GetAuthTokenAsync to complete using .Result, which deadlocks if the thread pool is saturated.",
  "filePath": "src\\CodeIntel.Server\Services\\ReportDeliveryService.cs",
  "lineNumber": 27,
  "codeSnippet": "var token = GetAuthTokenAsync().Result;"
}</finding>

<finding>{
  "severity": "bug",
  "confidence": "high",
  "title": "Discarded await on response.Content.ReadAsStringAsync()",
  "description": "When DeliverAsync fails, it discards the await on response.Content.ReadAsStringAsync(), which means the error body is never read and the log message will always show an empty string for `body`.",
  "filePath": "src\\CodeIntel.Server\Services\\ReportDeliveryService.cs",
  "lineNumber": 34,
  "codeSnippet": "var body = response.Content.ReadAsStringAsync();"
}</finding>

<finding>{
  "severity": "bug",
  "confidence": "high",
  "title": "Unmanaged StreamReader file handle",
  "description": "When DrainSpoolFile is called, the StreamReader is not in a using block, which means the file handle is never disposed if an exception occurs.",
  "filePath": "src\\CodeIntel.Server\Services\\ReportDeliveryService.cs",
  "lineNumber": 52,
  "codeSnippet": "var reader = new StreamReader(spoolPath);"
}</finding>

<finding>{
  "severity": "bug",
  "confidence": "high",
  "title": "Incorrect retry logic due to null RetryAfter header",
  "description": "When TryDeliverWithRetryAsync is called and the Retry-After header is absent, the retry fires immediately instead of backing off, turning the retry loop into a tight spin.",
  "filePath": "src\\CodeIntel.Server\Services\\ReportDeliveryService.cs",
  "lineNumber": 72,
  "codeSnippet": "var delta = response.Headers.RetryAfter?.Delta;"
}</finding>

<done />
```

</details>
