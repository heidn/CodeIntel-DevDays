# Code Intelligence Report

**Generated:** 2026-05-12 18:40:04 UTC  
**Mode:** Preset — find-bugs  
**Duration:** 239.5s  
**Context tokens:** ~4,991

## Scope

- `src\CodeIntel.Server\Controllers\ReportsController.cs`
- `src\CodeIntel.Server\Services\AnalysisCancellationRegistry.cs`
- `src\CodeIntel.Server\Services\InvestigationOrchestrator.cs`
- `src\CodeIntel.Server\Services\ReportWriter.cs`

## Findings

### 🟡 Warning (4)

#### Potential resource leak in `ReportWriter.WriteAsync` method
**Location:** `src\CodeIntel.Server.Services\ReportWriter.cs:47`

The `Directory.CreateDirectory(outDir);` call does not check if the directory already exists, which could lead to an exception if the directory already exists. Additionally, the `EnsureFolderReadme` method is called without checking if the file already exists, which could lead to an exception if the file already exists.

```csharp
Directory.CreateDirectory(outDir);
```

#### Potential off-by-one error in `InvestigationOrchestrator.RunAsync` method
**Location:** `src\CodeIntel.Server.Services\InvestigationOrchestrator.cs:121`

The loop condition `for (int iteration = 0; iteration < maxIters; iteration++)` is correct, but the loop body does not check if `iteration` is less than `maxIters` before accessing `iteration + 1`. This could lead to an off-by-one error if `iteration` is equal to `maxIters - 1` and `parser.IsDone` is true.

```csharp
await group.SendAsync("AnalysisEvent", AnalysisEvents.IterationStarted(iteration + 1, maxIters), ct);
```

#### Potential missing exception handling in `InvestigationOrchestrator.RunAsync` method
**Location:** `src\CodeIntel.Server.Services\InvestigationOrchestrator.cs:161`

The `catch (Exception ex)` block catches all exceptions, but it does not log the exception or rethrow it. This could make it difficult to diagnose issues that occur during the execution of the `RunAsync` method.

```csharp
catch (Exception ex)
```

#### Potential missing null check in `InvestigationOrchestrator.RunAsync` method
**Location:** `src\CodeIntel.Server.Services\InvestigationOrchestrator.cs:110`

The `context` variable is assigned the result of `_contextBuilder.BuildAsync`, but it is not checked for null before accessing its `Files` property. This could lead to a `NullReferenceException` if `_contextBuilder.BuildAsync` returns null.

```csharp
analyzedRelativePaths = context.Files.Select(f => f.RelativePath).ToList();
```

### 🔴 Bug (1)

#### Potential null reference in `ReportsController.Save` method
**Location:** `src\CodeIntel.Server.Controllers\ReportsController.cs:61`

The `body` parameter is nullable, but it is not checked for null before accessing its `OutputPath` property. This could lead to a `NullReferenceException` if `body` is null.

```csharp
var writeResult = await _writer.WriteAsync(result, workspace, body?.OutputPath, ct);
```

---

## Copilot Next Step

The local model surfaced potential bugs. Use Copilot to verify, prioritize, and propose concrete fixes.

Reference this file in Copilot Chat:

```text
#file:2026-05-12-find-bugs-fbd4c3c8.md
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
<finding>{"severity":"bug","title":"Potential null reference in `ReportsController.Save` method","description":"The `body` parameter is nullable, but it is not checked for null before accessing its `OutputPath` property. This could lead to a `NullReferenceException` if `body` is null.","filePath":"src\\CodeIntel.Server.Controllers\\ReportsController.cs","lineNumber":61,"codeSnippet":"var writeResult = await _writer.WriteAsync(result, workspace, body?.OutputPath, ct);","context":"The `SaveRequest` record is defined as `public record SaveRequest(string? OutputPath);`, which means `OutputPath` can be null. The `body?.OutputPath` expression will be null if `body` is null, but this is not checked before being passed to `_writer.WriteAsync`. If `body` is null, `body?.OutputPath` will be null, and the `WriteAsync` method will be called with a null `OutputPath` parameter."}</finding>

<finding>{"severity":"warning","title":"Potential resource leak in `ReportWriter.WriteAsync` method","description":"The `Directory.CreateDirectory(outDir);` call does not check if the directory already exists, which could lead to an exception if the directory already exists. Additionally, the `EnsureFolderReadme` method is called without checking if the file already exists, which could lead to an exception if the file already exists.","filePath":"src\\CodeIntel.Server.Services\\ReportWriter.cs","lineNumber":47,"codeSnippet":"Directory.CreateDirectory(outDir);","context":"The `Directory.CreateDirectory` method does not check if the directory already exists. If the directory already exists, this call will throw an exception. Additionally, the `EnsureFolderReadme` method is called without checking if the file already exists. If the file already exists, this call will throw an exception."}</finding>

<finding>{"severity":"warning","title":"Potential off-by-one error in `InvestigationOrchestrator.RunAsync` method","description":"The loop condition `for (int iteration = 0; iteration < maxIters; iteration++)` is correct, but the loop body does not check if `iteration` is less than `maxIters` before accessing `iteration + 1`. This could lead to an off-by-one error if `iteration` is equal to `maxIters - 1` and `parser.IsDone` is true.","filePath":"src\\CodeIntel.Server.Services\\InvestigationOrchestrator.cs","lineNumber":121,"codeSnippet":"await group.SendAsync(\"AnalysisEvent\", AnalysisEvents.IterationStarted(iteration + 1, maxIters), ct);","context":"The loop condition `for (int iteration = 0; iteration < maxIters; iteration++)` is correct, but the loop body does not check if `iteration` is less than `maxIters` before accessing `iteration + 1`. This could lead to an off-by-one error if `iteration` is equal to `maxIters - 1` and `parser.IsDone` is true."}</finding>

<finding>{"severity":"warning","title":"Potential missing exception handling in `InvestigationOrchestrator.RunAsync` method","description":"The `catch (Exception ex)` block catches all exceptions, but it does not log the exception or rethrow it. This could make it difficult to diagnose issues that occur during the execution of the `RunAsync` method.","filePath":"src\\CodeIntel.Server.Services\\InvestigationOrchestrator.cs","lineNumber":161,"codeSnippet":"catch (Exception ex)","context":"The `catch (Exception ex)` block catches all exceptions, but it does not log the exception or rethrow it. This could make it difficult to diagnose issues that occur during the execution of the `RunAsync` method."}</finding>

<finding>{"severity":"warning","title":"Potential missing null check in `InvestigationOrchestrator.RunAsync` method","description":"The `context` variable is assigned the result of `_contextBuilder.BuildAsync`, but it is not checked for null before accessing its `Files` property. This could lead to a `NullReferenceException` if `_contextBuilder.BuildAsync` returns null.","filePath":"src\\CodeIntel.Server.Services\\InvestigationOrchestrator.cs","lineNumber":110,"codeSnippet":"analyzedRelativePaths = context.Files.Select(f => f.RelativePath).ToList();","context":"The `context` variable is assigned the result of `_contextBuilder.BuildAsync`, but it is not checked for null before accessing its `Files` property. This could lead to a `NullReferenceException` if `_contextBuilder.BuildAsync` returns null."}</finding>

<finding>{"severity":"warning","title":"Potential missing null check in `InvestigationOrchestrator.RunAsync` method","description":"The `history` variable is assigned the result of `new List<ConversationTurn>();`, but
```

</details>
