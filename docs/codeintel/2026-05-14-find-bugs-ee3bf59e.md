# Code Intelligence Report

**Generated:** 2026-05-14 17:12:39 UTC  
**Mode:** Preset — find-bugs  
**Duration:** 15.2s  
**Context tokens:** ~1,991

## Scope

- `src\CodeIntel.Server\Services\LlamaSharpService.cs`

## Findings

### 🔴 Bug (1)

#### Null deref in LlamaSharpService.StreamAsync when _executor is null
**Location:** `LlamaSharpService.cs:58`

When _executor is null (legal per the constructor at line 12), line 58 calls _executor.InferAsync and throws NullReferenceException before any validation runs.

```csharp
await foreach (var text in _executor.InferAsync(prompt, inferenceParams, ct))
```

---

## Copilot Next Step

The local model surfaced potential bugs. Use Copilot to verify, prioritize, and propose concrete fixes.

Reference this file in Copilot Chat:

```text
#file:2026-05-14-find-bugs-ee3bf59e.md
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
  "title": "Null deref in LlamaSharpService.StreamAsync when _executor is null",
  "description": "When _executor is null (legal per the constructor at line 12), line 58 calls _executor.InferAsync and throws NullReferenceException before any validation runs.",
  "filePath": "LlamaSharpService.cs",
  "lineNumber": 58,
  "codeSnippet": "await foreach (var text in _executor.InferAsync(prompt, inferenceParams, ct))"
}</finding>
```

</details>
