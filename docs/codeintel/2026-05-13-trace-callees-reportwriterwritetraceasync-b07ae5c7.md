# Call-Trail Trace

**Generated:** 2026-05-13 13:51:40 UTC  
**Entry point:** `CodeIntel.Server.Services.ReportWriter.WriteTraceAsync(CodeIntel.Server.Models.TraceResult, CodeIntel.Server.Models.Workspace, string?, System.Threading.CancellationToken)`  
**Direction:** Callees  
**Depth:** 1  
**Nodes:** 8 · **Edges:** 7  
**Duration:** 82.5s

## Call graph

```mermaid
flowchart TD
  n0["ReportWriter.WriteTraceAsync"]
  n1["IReportGenerator.GenerateTraceMarkdown"]
  n2["new ReportWriteResult"]
  n3["ReportWriter.BuildTraceFilename"]
  n4["ReportWriter.EnsureFolderReadme"]
  n5["ReportWriter.NormalizeRelativeFolder"]
  n6["ReportWriter.ResolveWorkspaceRoot"]
  n7["ReportWriter.UpdateIndexForTraceAsync"]
  n0 --> n6
  n0 --> n5
  n0 --> n4
  n0 --> n3
  n0 --> n1
  n0 --> n7
  n0 --> n2
  style n0 fill:#7c3aed,stroke:#4f46e5,color:#fff
```

## Node synopses

### ReportWriter.WriteTraceAsync
**Location:** `c:\Users\heidn\Repos\Devdays\CodeIntel\src\CodeIntel.Server\Services\ReportWriter.cs:90`  
**Symbol:** `CodeIntel.Server.Services.ReportWriter.WriteTraceAsync(CodeIntel.Server.Models.TraceResult, CodeIntel.Server.Models.Workspace, string?, System.Threading.CancellationToken)`

This method asynchronously writes a trace report to a specified output directory within a workspace. It generates a Markdown file containing the trace results, saves it to the directory, and updates an index file if configured.

### IReportGenerator.GenerateTraceMarkdown
**Location:** `c:\Users\heidn\Repos\Devdays\CodeIntel\src\CodeIntel.Server\Services\ReportGenerator.cs:9`  
**Symbol:** `CodeIntel.Server.Services.IReportGenerator.GenerateTraceMarkdown(CodeIntel.Server.Models.TraceResult, string?)`

This method generates a Markdown formatted string representing a trace report based on the provided `TraceResult` object. If a `referenceFilename` is provided, it includes a reference to that file in the generated Markdown.

### new ReportWriteResult
**Location:** `c:\Users\heidn\Repos\Devdays\CodeIntel\src\CodeIntel.Server\Services\ReportWriter.cs:9`  
**Symbol:** `CodeIntel.Server.Services.ReportWriteResult.ReportWriteResult(string, string)`

This method defines a record type named `ReportWriteResult` with two properties: `AbsolutePath` and `RelativePath`, both of type `string`.

### ReportWriter.BuildTraceFilename
**Location:** `c:\Users\heidn\Repos\Devdays\CodeIntel\src\CodeIntel.Server\Services\ReportWriter.cs:137`  
**Symbol:** `CodeIntel.Server.Services.ReportWriter.BuildTraceFilename(CodeIntel.Server.Models.TraceResult)`

This method constructs a filename for a trace report based on the `TraceResult` object. It uses the completion date, shortened symbol fully qualified name, direction, and a truncated ID to generate a unique filename in the format `yyyy-MM-dd-trace-dir-label-id.md`.

### ReportWriter.EnsureFolderReadme
**Location:** `c:\Users\heidn\Repos\Devdays\CodeIntel\src\CodeIntel.Server\Services\ReportWriter.cs:232`  
**Symbol:** `CodeIntel.Server.Services.ReportWriter.EnsureFolderReadme(string)`

This method checks if a `README.md` file exists in the specified output directory. If it does not exist, it creates the file with predefined content that provides an overview of the code analysis reports and their structure.

### ReportWriter.NormalizeRelativeFolder
**Location:** `c:\Users\heidn\Repos\Devdays\CodeIntel\src\CodeIntel.Server\Services\ReportWriter.cs:198`  
**Symbol:** `CodeIntel.Server.Services.ReportWriter.NormalizeRelativeFolder(string?)`

This method normalizes a relative folder path by trimming any leading or trailing whitespace, replacing backslashes with forward slashes, and then trimming any remaining forward slashes. If the resulting string is empty or null, it returns null; otherwise, it returns the normalized folder path.

### ReportWriter.ResolveWorkspaceRoot
**Location:** `c:\Users\heidn\Repos\Devdays\CodeIntel\src\CodeIntel.Server\Services\ReportWriter.cs:205`  
**Symbol:** `CodeIntel.Server.Services.ReportWriter.ResolveWorkspaceRoot(CodeIntel.Server.Models.Workspace)`

This method checks if the project path of a given workspace exists as a file. If it does, it returns the directory containing the project file; otherwise, it returns the project path as is.

### ReportWriter.UpdateIndexForTraceAsync
**Location:** `c:\Users\heidn\Repos\Devdays\CodeIntel\src\CodeIntel.Server\Services\ReportWriter.cs:162`  
**Symbol:** `CodeIntel.Server.Services.ReportWriter.UpdateIndexForTraceAsync(string, string, CodeIntel.Server.Models.TraceResult, System.Threading.CancellationToken)`

This method updates an index file for a trace result. It loads the existing index, removes any entry for the specified filename, adds a new entry with the trace result details, and then saves the updated index to both a JSON and Markdown file.

---

## Copilot Next Step

The call graph above shows what `CodeIntel.Server.Services.ReportWriter.WriteTraceAsync(CodeIntel.Server.Models.TraceResult, CodeIntel.Server.Models.Workspace, string?, System.Threading.CancellationToken)` does internally.
Ask Copilot:

```text
Using the call graph and per-node synopses above, produce a one-page developer-facing
overview of CancellationToken): what it does, what it touches
(DBs, files, external services), and any risks or surprises a new dev should know.
```

Reference this file in Copilot Chat:

```text
#file:2026-05-13-trace-callees-reportwriterwritetraceasync-b07ae5c7.md
```

