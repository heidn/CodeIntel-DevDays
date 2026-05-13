# CodeIntel — Project Memory

> Internal dev tool: web app that lets developers analyze C# code and Oracle PL/SQL stored procs using a local LLM, with handoff to **GitHub Copilot** (team's subscription) via MD reports written into the loaded repo.

## Status

**Phase 1 + Phase 2 + Trace mode v1 + PL/SQL repo mode v1 shipping.** Save-to-repo, cancellation + watchdogs, call-trail trace, and PL/SQL stored-proc analysis are all wired up; trace v1 verified end-to-end on `CodeIntel.sln`, PL/SQL v1 needs UI smoke-test on a real repo.

The agentic analysis loop runs cleanly through to `<done/>` and produces structured findings on a real .sln. On-demand "Save to repo" writes the markdown report plus a preset-aware "Copilot Next Step" prompt into `{repoRoot}/docs/codeintel/` (configurable), with INDEX.md + `.codeintel-index.json` sidecar + one-time folder README. Cancellation is wired through a per-analysis CTS registry with idle-token (90s) and overall (600s) watchdogs; partial findings survive cancel.

**Trace mode** (new): top-level **Analysis | Trace** toggle in `AnalysisPanel`. Type an entry-point method name (e.g. `OrderService.Submit`) + direction (Callers / Callees / Both) + depth (1–5), and the `TraceWalker` does a Roslyn BFS — `SymbolFinder.FindCallersAsync` for callers, semantic-model + `SyntaxWalker` on `InvocationExpressionSyntax`/`ObjectCreationExpressionSyntax` for callees. Each node gets a 1–2-sentence LLM synopsis (sequential, with full cancel/watchdog support and partial-save). Mermaid is generated programmatically (not LLM-emitted) and rendered inline via the `mermaid` npm package. Save to repo writes a trace report with a direction-aware Copilot brief (bug investigation / overview / change-impact). Smoke-tested: `ReportWriter.WriteTraceAsync` callees @ depth=1 → 8 nodes, 1m25s, all synopses accurate, MD + INDEX clean.

Tested A/B finding on the analysis side: the local 7B model produces noisy hedge-y findings — that's exactly what the Copilot Next Step handoff is for. The tightened `find-bugs.md` prompt and parser drop-counters reduce the worst FPs but the architecture deliberately leans on Copilot to verify. Trace-mode synopses are noticeably cleaner because the prompt is tighter and the scope per call is small.

Origin: dev days project, intended to grow into a team tool deployed on internal OpenShift.

---

## Architecture (one-paragraph)

ASP.NET Core (.NET 10) hosts both the API and the React 19 SPA. The .NET server runs a GGUF model in-process via LLamaSharp. A request to `/api/analysis/run` returns immediately with an `analysisId`; the `InvestigationOrchestrator` runs an agentic loop (up to `MaxAgenticIterations`, default 3): it builds context, assembles a Qwen-formatted ChatML prompt from an embedded MD template, streams tokens through `ILlmService.StreamAsync`, and the `FindingStreamParser` reads both `<finding>{...}</finding>` and `<request_context>...</request_context>` blocks out of the stream. When the LLM requests more context, `ContextRequestHandler` fulfills it via Roslyn (file, class, method, callers, callees, search); findings and raw tokens are pushed to the client over SignalR. The orchestrator combines three `CancellationTokenSource`s: a user-cancel registered in `IAnalysisCancellationRegistry`, an idle-token watchdog (reset on each token), and an overall hard ceiling. Results cache in-memory by GUID; `/api/reports/{id}/download` returns a download, and `/api/reports/{id}/save` (new) writes the markdown into `{loadedRepoRoot}/{Analysis:ReportOutputPath}` (default `docs/codeintel/`) with INDEX.md + JSON sidecar. The MD report ends with a preset-aware "Copilot Next Step" section the user can reference in Copilot Chat via `#file:` syntax. A non-agentic `AnalysisOrchestrator` also exists as a fallback but is not wired in DI.

---

## Tech Stack (locked-in versions as of build time)

| Layer | Choice | Version |
|---|---|---|
| Runtime | .NET | 10.0 (LTS, GA Nov 2025) |
| LLM | LLamaSharp + Backend.Cpu | 0.27.0 |
| Code parsing | Microsoft.CodeAnalysis | 4.12.0 |
| MSBuild loading | Microsoft.Build.Locator | 1.7.8 |
| Logging | Serilog.AspNetCore | 8.0.3 |
| Frontend | React | 19.x |
| UI library | MUI Material + MUI X | 9.0.0 |
| Build tool | Vite | 5.4.x |
| State | Zustand | 5.x |
| Server state | TanStack Query | 5.59.x |
| Streaming | @microsoft/signalr | 8.0.7 |

---

## Project Layout

```
CodeIntel/
├── CodeIntel.sln
├── CLAUDE.md                            ← you are here
├── models/                              ← gitignored GGUF files
│   └── qwen2.5-coder-7b-instruct-q4_k_m.gguf
├── test-data/sql/                       ← small PL/SQL fixture for manual smoke-testing
├── tests/CodeIntel.Server.Tests/        ← xunit 2.9 test project — parser/resolver/builder unit + integration tests
└── src/CodeIntel.Server/
    ├── CodeIntel.Server.csproj
    ├── Program.cs                       ← DI, SignalR, SPA hosting, eager LLM init
    ├── appsettings.json
    ├── Controllers/
    │   ├── WorkspaceController.cs       ← POST /api/workspace/load, GET file, GET definition
    │   ├── AnalysisController.cs        ← presets, status, run, recent, cancel
    │   ├── ReportsController.cs         ← analysis MD generation, download, POST save
    │   └── TraceController.cs           ← POST /api/trace/run, GET {id}, POST save (cancel reuses analysis endpoint)
    ├── Hubs/
    │   └── AnalysisHub.cs               ← single "AnalysisEvent" channel (analysis + trace events)
    ├── Services/
    │   ├── RoslynWorkspaceService.cs    ← MSBuildLocator + MSBuildWorkspace
    │   ├── LlamaSharpService.cs         ← singleton, semaphore-serialized inference
    │   ├── ContextBuilder.cs            ← raw-text bundling with token budget
    │   ├── PromptTemplateService.cs     ← loads embedded MD prompts, ChatML format
    │   ├── FindingStreamParser.cs       ← parses <finding> + <request_context>, tracks malformed/incomplete drops
    │   ├── AnalysisOrchestrator.cs      ← one-shot pipeline (not active in DI)
    │   ├── InvestigationOrchestrator.cs ← agentic loop + CT/watchdogs (ACTIVE)
    │   ├── AnalysisCancellationRegistry.cs ← per-analysis CTS lookup; reused by trace too
    │   ├── ContextRequestHandler.cs     ← fulfills LLM context requests via Roslyn (or PL/SQL resolver for OracleObject)
    │   ├── PlSqlObjectParser.cs         ← regex-based extractor of table/proc/package refs from PL/SQL text (comment + string aware)
    │   ├── PlSqlRepoResolver.cs         ← maps a PL/SQL object name → file in workspace (filename match + CREATE-OR-REPLACE DDL grep)
    │   ├── TraceWalker.cs               ← Roslyn BFS for callers (FindCallersAsync) + callees (SemanticModel + SyntaxWalker), programmatic Mermaid
    │   ├── TraceOrchestrator.cs         ← graph walk → per-node LLM synopsis → save; partial-save on cancel
    │   ├── TraceResultStore.cs          ← in-memory cache for TraceResults (separate from analysis store)
    │   ├── ReportGenerator.cs           ← MD output: GenerateMarkdown(AnalysisResult) + GenerateTraceMarkdown(TraceResult), preset-aware Copilot briefs
    │   ├── ReportWriter.cs              ← writes MD into target repo + unified INDEX (Kind: "analysis"|"trace") + JSON sidecar
    │   └── AnalysisResultStore.cs       ← in-memory cache for AnalysisResults
    ├── Models/
    │   ├── AnalysisModels.cs            ← Request, Result, Finding, enums (incl. ContextRequestType.OracleObject)
    │   ├── AnalysisEvents.cs            ← SignalR event factory
    │   ├── WorkspaceModels.cs           ← Workspace, ProjectNode, FileNode, CodeContext (FileContext.IsResolvedDependency flag)
    │   ├── PlSqlModels.cs               ← ParsedObjectReferences, PlSqlResolution, PlSqlObjectKind
    │   └── Options.cs                   ← LlmOptions, AnalysisOptions
    ├── Prompts/                         ← embedded MD resources
    │   ├── find-dead-code.md
    │   ├── find-bugs.md
    │   ├── find-business-rules.md
    │   ├── summarize.md
    │   ├── find-bugs-sql.md             ← PL/SQL bug hunting (cursor leaks, swallowed exceptions, etc.)
    │   ├── find-business-rules-sql.md   ← rules from DDL constraints + proc logic + triggers
    │   ├── cleanup-stored-proc.md       ← refactor briefing (dead code, magic literals, inconsistent error handling)
    │   └── efficiency-review.md         ← performance signals (row-by-row, implicit conversions, missing binds)
    └── ClientApp/                       ← React 19 + Vite + MUI v9
        ├── package.json
        ├── vite.config.ts               ← proxies /api + /hubs (ws:true) to :5000
        ├── index.html                   ← loads JetBrains Mono + Inter Tight
        └── src/
            ├── main.tsx
            ├── App.tsx                  ← top shell + SignalR wiring
            ├── theme/index.ts           ← dark dev-tool MUI theme
            ├── api/                     ← client, workspace, analysis, signalr
            ├── stores/                  ← workspaceStore, analysisStore
            ├── types/index.ts           ← shared API contracts
            └── components/
                ├── StatusDot.tsx
                ├── SolutionPanel.tsx        ← left sidebar: load + tree
                ├── FileTree.tsx             ← SimpleTreeView with checkboxes
                ├── FolderPickerDialog.tsx   ← modal filesystem browser for .sln path
                ├── AnalysisPanel.tsx        ← center column + tab bar (Analysis + file previews)
                ├── PromptSelector.tsx       ← preset cards + free text + run
                ├── ResultsView.tsx          ← streaming pane + findings cards
                ├── CodeAnnotationView.tsx   ← code with inline finding highlights
                └── FilePreviewPanel.tsx     ← readonly code display with syntax highlighting
```

---

## Key Design Decisions (and why)

- **Single-project layout** (.NET hosts React via `SpaProxy`) — picked over separate projects to simplify deployment to OpenShift. One container, one URL.
- **In-process LLM via LLamaSharp** — picked over Ollama/LM Studio to make deployment a single artifact. Trade-off accepted: model load is part of app startup; only one inference at a time per pod.
- **Inference serialized by `SemaphoreSlim(1,1)`** — LLamaSharp contexts are not thread-safe. For dev days this is fine; OpenShift scales horizontally with multiple pods.
- **SignalR with single event channel** — all analysis events flow through `OnAsync("AnalysisEvent", ...)` with a `type` discriminator. Client switches on it. Avoids a proliferation of named events.
- **`<finding>` tag wrapping** — chosen over raw JSON-per-line because it's robust against the LLM prepending prose, easier to parse with regex, and degrades gracefully if the model gets confused mid-token.
- **Raw file text for initial context; Roslyn for follow-up requests** — initial context is assembled from raw file text. When the agentic loop requests a class/method/callers/callees, `ContextRequestHandler` fulfills those via Roslyn symbol resolution. Upgrade initial context assembly to Roslyn extraction if token budget becomes the bottleneck.
- **In-memory result store** — sessions don't persist. DB persistence is a future phase.
- **No auth for MVP** — explicit choice. Add Windows Auth via IIS passthrough when deploying internally.
- **Save reports into the loaded repo** (not auto-download) — Copilot Chat's `#file:` syntax wants the file in the workspace. The MD lives at `docs/codeintel/{date}-{preset}-{shortId}.md` by default, configurable per save. Each report ends with a preset-aware prompt the user can paste into Copilot.
- **Local model = briefing officer, Copilot = analyst** — a 7B Qwen produces noisy findings. The architecture deliberately offloads verification to Copilot via the embedded "Copilot Next Step" prompt. Don't try to make the local model good; make it good enough that Copilot's verification round is fast.
- **Cancellation via linked CTS triad** — user cancel (registry), idle-token watchdog (reset per token), overall hard ceiling. All three feed a linked CTS; the catch block inspects which fired to emit a specific reason. Partial findings are still saved.

---

## Setup (clean machine)

```powershell
# 1. Prerequisites
# - .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0
# - Node.js 20+ from https://nodejs.org

# 2. Download model (~4.7 GB) to ./models/
# Source: https://huggingface.co/bartowski/Qwen2.5-Coder-7B-Instruct-GGUF
# File: qwen2.5-coder-7b-instruct-q4_k_m.gguf

# 3. Restore + install
cd CodeIntel
dotnet restore
cd src\CodeIntel.Server\ClientApp
npm install
cd ..

# 4. Run (dev mode — Vite dev server + .NET both start)
dotnet run

# Open http://localhost:5000
```

**First run notes:**
- LLM model loads on app startup in a background task. The status dot in the top-right turns from amber (loading) to green (ready). First load takes 30-60s on CPU.
- LLamaSharp will download the native llama.cpp binary on first build; this takes a minute.
- The Roslyn `MSBuildLocator.RegisterDefaults()` call must happen exactly once before any workspace code runs — it's gated in `RoslynWorkspaceService` static constructor.

---

## Hardware → Model Strategy

The architecture handles model swaps via `appsettings.json` (`Llm:ModelPath`). No code changes needed to upgrade.

| Stage | Machine | Model | RAM | Notes |
|---|---|---|---|---|
| Home dev | i7 + iRIS Xe iGPU | Qwen2.5-Coder-7B Q4_K_M | ~8GB | CPU-only, GpuLayerCount=0 |
| Work ZBook | vPro i7 workstation | Qwen2.5-Coder-14B Q5_K_M | ~11GB | Still CPU. Noticeably better quality. |
| OpenShift prod | container, possibly GPU node | Qwen2.5-Coder-32B Q4_K_M **or** Devstral Small 24B | 20-30GB | Devstral is purpose-built for agentic tool-calling — matches the spiderweb feature roadmap |

For OpenShift, model file lives on a persistent volume (don't bake into image — too big and changes independently).

---

## Known Risks / Gotchas

1. **LLamaSharp 0.27 API stability** — version churns frequently. Used `StatelessExecutor` + `DefaultSamplingPipeline`. If compile fails, check `InferenceParams` shape and `LLama.Sampling` namespace.
2. **MUI v9** — only ~1 month old at build time. Used `SimpleTreeView` from `@mui/x-tree-view`. If imports break, check v9 changelog — they jumped from v7 to v9 to align with MUI X v9.
3. **MSBuildLocator** — must register *before* any `Microsoft.CodeAnalysis.MSBuild` code runs. Don't move it out of `RoslynWorkspaceService`.
4. **SignalR + Vite proxy** — needs `ws: true` in the proxy config for WebSocket upgrade. Already set.
5. **First inference is slow** — CPU model warmup takes 30-60s. Don't conclude it's broken before letting it complete one cycle.
6. **Embedded prompt resources** — `.md` files in `Prompts/` are embedded via `<EmbeddedResource>`. If you add new presets, update both the MD file and the `_presets` list in `PromptTemplateService.cs`.

---

## What's Built vs Not Built

### ✅ Built and verified end-to-end

**Analysis pipeline**
- Solution loading via Roslyn workspace (`MSBuildWorkspace`); TS/Java/SQL via file scan
- File tree UI with project + file selection; folder picker dialog; file preview tabs
- 8 preset prompts: 4 C#-tuned (`find-dead-code`, `find-bugs`, `find-business-rules`, `summarize`) + 4 PL/SQL-tuned (`find-bugs-sql`, `find-business-rules-sql`, `cleanup-stored-proc`, `efficiency-review`). Each `PresetInfo` carries `ApplicableLanguages`; the `PromptSelector` filters by `workspace.language` so users only see relevant presets.
- Free-text mode + pinned snippet support
- SignalR token streaming with live UI updates (started, token, finding, status, iteration, contextRequested/Fulfilled, completed, cancelled, error, traceGraphReady, traceNodeSynopsis)
- Structured finding extraction during stream (`<finding>` blocks) with malformed/incomplete drop counters
- **Agentic investigation loop** — `InvestigationOrchestrator` (default 3 iterations); LLM emits `<request_context>` to pull additional file/class/method/callers/callees/search via Roslyn, or **`oracle_object`** for PL/SQL workspaces (resolved via `IPlSqlRepoResolver`)
- Inline code annotations (`CodeAnnotationView`)
- Tightened `find-bugs.md` prompt with anti-hedging + safe-API allowlists (suppresses most of the common Qwen 7B FP classes)

**PL/SQL repo mode (v1, repo-only — no live Oracle yet)**
- Load any folder of `.sql/.pkg/.pkb` files as a `Language.Sql` workspace (existing file-scan path; no new loader needed)
- `PlSqlObjectParser` strips PL/SQL comments + string literals, then regex-extracts referenced objects from DML keywords (`FROM`/`JOIN`/`INTO`/`UPDATE`/`DELETE`/`MERGE`/`USING`), explicit `EXECUTE`/`CALL`, and `package.proc(...)` invocation syntax
- `PlSqlRepoResolver` maps an object name → file in the same workspace: filename match (case-insensitive, schema-prefix stripped) first, then a CREATE-OR-REPLACE DDL grep across all SQL files as fallback
- `ContextBuilder` auto-attaches resolved object definitions as `FileContext { IsResolvedDependency: true }` when any seed file is PL/SQL, respecting the same `MaxContextTokens` budget. `BuildContextBlock` renders them under a separate `--- PL/SQL OBJECT DEFINITIONS ---` header so the model treats them as supporting material.
- 4 PL/SQL-tuned presets (`find-bugs-sql.md`, `find-business-rules-sql.md`, `cleanup-stored-proc.md`, `efficiency-review.md`) follow the existing anti-hedging Qwen structure, with 4 matching preset-aware **Copilot Next Step** branches in `ReportGenerator` (Jira-ready bugs / Confluence-ready rules / sequenced refactor plan / EXPLAIN-PLAN-validated efficiency review)
- During the agentic loop, the LLM can emit `<request_context type="oracle_object">NAME</request_context>` to pull a specific table / view / proc / package on demand

**Call-trail trace mode (v1)**
- Top-level **Analysis | Trace** toggle in `AnalysisPanel.tsx` (pane mode now lives in `workspaceStore`)
- `TracePanel.tsx` — entry-point method name input OR location chip (from "Trace from here"), direction radio (Callers/Callees/Both), depth 1–5, Run button
- **"Trace from here"** in `FilePreviewPanel.tsx`: click a symbol in any open file → sibling button next to "Pin to analysis" → resolves the declaration via `getDefinition`, sets `traceStore.entryPointLocation`, switches the pane to Trace, pre-populates a location chip the user can clear
- Backend `TraceWalker`: Roslyn BFS for callers (`SymbolFinder.FindCallersAsync`) and callees (`SemanticModel` walk of `InvocationExpressionSyntax`/`ObjectCreationExpressionSyntax`); per-node fan-out cap of 8 with `truncated` flag. Entry-point resolution accepts either a method name or `{filePath, line, character}`.
- **NodeKind classification** (`TraceWalker.InferNodeKind`): each node is tagged `Normal | DbAccess | HttpCall` by walking the symbol's type hierarchy + method-name heuristics. DbContext/DbSet/IQueryable/SaveChangesAsync/FromSql/ExecuteSql → DB; HttpClient/HttpMessageHandler/IHttpClientFactory + BCL HTTP verbs (GetAsync, SendAsync, etc.) → HTTP.
- Backend `TraceOrchestrator`: graph walk → sequential per-node LLM synopsis (with idle/overall watchdogs + cancel + partial-save) → save `TraceResult`
- Programmatic Mermaid generation in `TraceWalker.BuildMermaid` (not LLM-emitted — more reliable). Shapes vary by NodeKind: rectangle (Normal), cylinder (DbAccess), hexagon (HttpCall). Colors classDef'd to blue/green for DB/HTTP; entry node highlighted purple.
- `TraceResultsView.tsx`: chips for elapsed/synopses/cancelled, Cancel button, Mermaid render via `MermaidDiagram` (dark theme, with fullscreen + SVG/PNG download), per-node synopsis cards with DB/HTTP badge + file:line + click-to-open
- `POST /api/trace/run` + `GET /api/trace/{id}` + `POST /api/trace/{id}/save`; cancel reuses `POST /api/analysis/{id}/cancel` (same Guid registry)
- Direction-aware **Copilot Next Step** brief: bug-investigation (callers), feature overview (callees), change-impact (both)
- Smoke-tested via UI on `ReportWriter.WriteTraceAsync` callees depth=1 — 8 nodes, ~1m25s, clean synopses, Mermaid renders inline, save writes a clean MD. Re-tested with Both at depth=2 — 20 nodes, ~3m, no truncation.

**Save to repo flow** (shared by analysis + trace)
- `POST /api/reports/{id}/save` and `POST /api/trace/{id}/save` write into `{repoRoot}/{Analysis:ReportOutputPath}` (default `docs/codeintel/`)
- Unified `INDEX.md` + `.codeintel-index.json` sidecar with `Kind: "analysis"|"trace"` discriminator
- One-time folder README; path-escape guard against writes outside workspace root
- UI: Save to repo button + path-override input + post-save banner with copy-path / copy-`#file:` reference buttons
- Download endpoint kept as fallback (`/api/reports/{id}/download`)

**Cancellation + watchdogs**
- Per-analysis/trace `CancellationTokenSource` registry (`IAnalysisCancellationRegistry`)
- Linked CTS triad: user-cancel + idle-token (90s default, reset per token) + overall (600s default), all configurable in `appsettings.json`
- Distinct `cancelled` SignalR event; orchestrator inspects which CTS fired and emits reason (`user`/`idle`/`timeout`/`unknown`)
- Partial findings/synopses preserved on cancel — Save to repo still works
- UI: Cancel button while running, live elapsed seconds, idle warn chip (≥30s), distinct cancelled-state styling

**Other**
- Dark dev-tool aesthetic (JetBrains Mono + Inter Tight)
- In-memory result history (`AnalysisResultStore`, `TraceResultStore`)
- Mermaid via `mermaid` npm package (dark theme, JetBrains Mono labels, htmlLabels)

### ❌ Deferred (next sessions)

- **Multi-language abstraction** — LSP client + tree-sitter for non-C# repos (TypeScript first). Roslyn becomes "the C# LSP backend," not a special case. Trace mode is C#-only today via Roslyn; LSP would unlock it for TS/Python/etc.
- **Live Oracle introspection (PL/SQL v2)** — `Oracle.ManagedDataAccess.Core` + connection string + queries against `ALL_TABLES` / `ALL_TAB_COLUMNS` / `USER_SOURCE` / `ALL_PROCEDURES` to fetch schemas and stored-proc bodies from a live DB when the repo doesn't have the DDL committed. Also unlocks cross-workspace augmentation (a C# workspace's `DbAccess` trace nodes pulling schema from a sibling Oracle connection). v1 (repo-only) is shipping.
- **Whole-repo dead-code detection** — tree-sitter to enumerate functions, LSP for references, LLM only for ambiguous cases (reflection, DI, dynamic dispatch).
- **Business documentation mode** — walk top-level entry points (controllers, handlers), trace flows, produce a feature catalog. Builds on the existing trace-mode pipeline.
- **Skills system** — folder of `SKILL.md` files (csharp-bug-hunting, stored-proc-analysis, etc.) routed into prompts by mode + keyword.
- **Roslyn-extracted initial context** — currently raw file text for the first iteration. Upgrade to method-level extraction when context budget gets tight.
- **Optional finding-validation pass** — config flag to run a second LLM pass that critiques iter-1 findings and drops the ones it can't defend. Doubles run time but kills residual FPs.
- **Trace v1.5 polish** — cycle detection in `TraceWalker` (currently relies on per-node fan-out cap + visited-set dedup), branch limits per direction, batched synopsis (single LLM call for several small nodes). (DB/HTTP node classification has shipped — see above.)
- **OpenShift deployment** — Dockerfile, persistent volume for model, ConfigMap for `appsettings`. The single-project layout was designed for this; never done.
- **Auth** — Windows Auth via IIS passthrough on the internal network.
- **Persistence** — DB tables for analysis history, saved reports, prompt template versions.

---

## Pickup Priorities (when resuming)

In recommended order:

1. **Verify trace-mode in the UI on a richer C# project.** Local smoke-tests passed on `CodeIntel.sln` (Callees depth=1 → 8 nodes ~1m25s; Both depth=2 → 20 nodes ~3m). Want to confirm the experience on something bigger — controller/service entry points in a real LOB app. Watch for performance and graph readability, plus that the DB/HTTP NodeKind classification fires correctly on EF/HttpClient code.
2. **LSP + tree-sitter abstraction.** Replace direct Roslyn calls in `ContextRequestHandler` + `TraceWalker` with an LSP client interface; add tree-sitter for fast structural scans. Ships TypeScript support (and the trace-mode TS support) for ~free once done. The single biggest compounding lever — analysis, trace, dead-code, business-docs all benefit.
3. **PL/SQL repo-mode UI smoke-test + Oracle live (v2).** v1 (repo-only) is shipping — load a real PL/SQL repo, pick a stored proc, run each of the 4 SQL presets, verify the parser/resolver attaches the right object definitions and the Copilot Next Step briefs read cleanly. Then v2: add `Oracle.ManagedDataAccess.Core`, wire a `SqlOptions` (connection string) + `IOracleSchemaService` that fulfils the `OracleObject` context-request with live `ALL_TABLES` / `USER_SOURCE` data when the repo doesn't have the DDL committed. Pairs with the trace-mode DbAccess NodeKind (cross-workspace augmentation) once the LSP rewrite lands.
4. **Whole-repo dead-code detection.** Tree-sitter to enumerate functions, LSP for references, LLM only for ambiguous cases (reflection, DI, dynamic dispatch). Cheap once LSP is in place.
5. **Business documentation mode.** Walk top-level entry points → trace → catalog. Builds directly on the trace pipeline.
6. **Skills system.** Specialized prompts (`bug-async.md`, `bug-sql-injection.md`, etc.) that get routed in based on file content. Biggest single quality boost on a 7B local model.
7. **Dockerfile + OpenShift** — multi-stage build (npm → dotnet publish), volume mount for `/models`, ConfigMap for runtime tuning.

---

## Commands Cheat Sheet

```powershell
# Dev run
dotnet run --project src\CodeIntel.Server

# Tests (xunit, PL/SQL parser/resolver/builder)
dotnet test tests\CodeIntel.Server.Tests

# Build production bundle
dotnet publish src\CodeIntel.Server -c Release -o publish

# Run published artifact
cd publish && dotnet CodeIntel.Server.dll

# React-only dev (rare — usually `dotnet run` covers it)
cd src\CodeIntel.Server\ClientApp && npm run dev

# Check what npm has
cd src\CodeIntel.Server\ClientApp && npm ls --depth=0
```

---

## Conventions

- **C# nullable + ImplicitUsings enabled.** No `using System;` at top of files.
- **Records over classes** for DTOs and event payloads.
- **CamelCase JSON** — configured in both controllers and SignalR.
- **Enums serialized as camelCase strings** — clients see `"bug"` not `4`.
- **Service interfaces** prefixed with `I`, implementations not.
- **MUI**: prefer `sx` over `styled` for one-offs. Use theme tokens, never hex literals in components (except deliberate brand accents like the gradient logo).
- **No emoji in C# source.** OK in MD reports (severity icons), OK in UI.
- **Logging via Serilog.** Use structured properties (`logger.LogInformation("Loaded {Count} files", n)`), never string interpolation.

---

## Origin Context

Built during dev days planning to demo a self-hosted code intelligence tool. Three-tier vision:

1. **Local LLM** — does the heavy file-reading, pattern matching, and context curation
2. **MD reports** — structured handoff document committed into the team's repo, the bridge layer
3. **GitHub Copilot** (team's existing subscription) — consumes the MD report (via Copilot Chat `#file:` reference) to produce Jira tickets, fix plans, PR descriptions, risk assessments

The MD bridge is the deliberate architectural choice that distinguishes this from just running a local LLM yourself: it produces a durable artifact that's human-readable, AI-consumable, and committable as living documentation. Initial design imagined a Claude Opus handoff; pivoted to Copilot because the team already pays for Copilot and the MD bridge works the same — paste, reference, ask.

Eventual deployment: internal URL on OpenShift, every dev on the team uses it from their browser, no installs.
