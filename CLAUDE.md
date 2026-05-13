# CodeIntel — Project Memory

> Internal dev tool: web app that lets developers analyze C# code and Oracle PL/SQL stored procs using a local LLM, with handoff to **GitHub Copilot** (team's subscription) via MD reports written into the loaded repo.

## Status

**Phase 3 hardening pass shipping** — most of the items in [docs/REVIEW-ENHANCEMENTS.md](docs/REVIEW-ENHANCEMENTS.md) are now closed. The tool has crossed from "demo on my laptop" toward "team tool on OpenShift": SQLite-backed history + ignored findings + result cache, per-IP rate limiting on run endpoints, LRU caps on workspaces and result rows, ANTLR-backed PL/SQL parser, secret scrubbing before save, cost/time estimator, findings diff + aggregator, content-keyed skill router, VS Code deep-link, trace cycle detection + overload disambiguation + total-node cap, `/healthz` + `/readyz` probes. See [docs/REVIEW-FEATURES.md](docs/REVIEW-FEATURES.md) for the reviewer-oriented walkthrough.

**Phase 1 + Phase 2 + Trace mode v1 + PL/SQL repo mode v1 also shipping.** Save-to-repo, cancellation + watchdogs, call-trail trace, and PL/SQL stored-proc analysis are all wired up; trace v1 verified end-to-end on `CodeIntel.sln`, PL/SQL v1 still wants UI smoke-test on a real repo.

The agentic analysis loop runs cleanly through to `<done/>` and produces structured findings on a real .sln. On-demand "Save to repo" writes the markdown report plus a preset-aware "Copilot Next Step" prompt into `{repoRoot}/docs/codeintel/` (configurable), with INDEX.md + `.codeintel-index.json` sidecar + one-time folder README. Cancellation is wired through a per-analysis CTS registry with idle-token (90s) and overall (600s) watchdogs; partial findings survive cancel.

**Trace mode**: top-level **Analysis | Trace** toggle in `AnalysisPanel`. Type an entry-point method name (e.g. `OrderService.Submit`) + direction (Callers / Callees / Both) + depth (1–5), and the `TraceWalker` does a Roslyn BFS — `SymbolFinder.FindCallersAsync` (memoized per run via `CallersCache`) for callers, semantic-model + `SyntaxWalker` on `InvocationExpressionSyntax`/`ObjectCreationExpressionSyntax` for callees. Total-node ceiling of 100 with `truncated=true`, dashed Mermaid back-edges for cycles, and an overload-disambiguation step via `POST /api/trace/candidates` when the entry-point name matches more than one method. Each node gets a 1–2-sentence LLM synopsis (sequential, with full cancel/watchdog support and partial-save). Mermaid is generated programmatically (not LLM-emitted) and rendered inline via the `mermaid` npm package. Save to repo writes a trace report with a direction-aware Copilot brief (bug investigation / overview / change-impact). Smoke-tested: `ReportWriter.WriteTraceAsync` callees @ depth=1 → 8 nodes, 1m25s, all synopses accurate, MD + INDEX clean.

Tested A/B finding on the analysis side: the local 7B model produces noisy hedge-y findings — that's exactly what the Copilot Next Step handoff is for. The tightened `find-bugs.md` prompt, the `FindingsAggregator` collapse step, the parser drop-counters, the `SecretScrubber` pre-write pass, and the per-workspace ignored-findings store reduce the worst FPs but the architecture deliberately leans on Copilot to verify. Trace-mode synopses are noticeably cleaner because the prompt is tighter and the scope per call is small.

Origin: dev days project, intended to grow into a team tool deployed on internal OpenShift.

---

## Architecture (one-paragraph)

ASP.NET Core (.NET 10) hosts both the API and the React 19 SPA. The .NET server runs a GGUF model in-process via LLamaSharp (singleton, `SemaphoreSlim(1,1)`-serialized, SHA-256 integrity-checked, Vulkan→CPU auto-fallback that reuses the probe-loaded weights to avoid a 4.7 GB double-read). A request to `/api/analysis/run` (rate-limited per IP to `Analysis:RateLimitRunsPerMinute`, default 5/min) returns immediately with an `analysisId`; the `InvestigationOrchestrator` first tries `IResultCache` for a hit on `{presetKey, modelName, file-content sha256}` and short-circuits if present. Otherwise it runs an agentic loop (up to `MaxAgenticIterations`, default 3): it builds context, asks `ISkillRouter` to attach a content-keyed prompt addendum (concurrency / raw-SQL / http-client / auth / PL/SQL cursors), assembles a Qwen-formatted ChatML prompt from an embedded MD template, streams tokens through `ILlmService.StreamAsync`, and the `FindingStreamParser` (now a single-pass O(n) scanner) reads both `<finding>{...}</finding>` and `<request_context>...</request_context>` blocks out of the stream. When the LLM requests more context, `ContextRequestHandler` fulfills it via Roslyn — methods now emit the symbol's full syntax via `DeclaringSyntaxReferences` rather than counting lines. Findings and raw tokens are pushed to the client over SignalR. The orchestrator combines three `CancellationTokenSource`s: a user-cancel registered in `IAnalysisCancellationRegistry`, an idle-token watchdog (reset on each token), and an overall hard ceiling. After the loop, `FindingsAggregator.Collapse` merges near-duplicate findings the 7B model often re-states across iterations. Results persist to SQLite via `SqliteAnalysisResultStore` (`data/codeintel.db`, WAL-mode, LRU-pruned to `MaxPersistedResults=200`); `/api/reports/{id}/download` returns a download and `/api/reports/{id}/save` writes the markdown into `{loadedRepoRoot}/{Analysis:ReportOutputPath}` (default `docs/codeintel/`) after a `SecretScrubber` pass and a `PathSafety.IsInside` containment check. INDEX.md + JSON sidecar are maintained; the MD report ends with a preset-aware "Copilot Next Step" section the user can reference in Copilot Chat via `#file:` syntax. The Roslyn `WorkspaceService` itself is LRU-capped at `MaxLoadedWorkspaces=3` to bound MSBuildWorkspace memory. `/healthz` (liveness) and `/readyz` (LLM + SQLite readiness) live outside `/api` for OpenShift probes.

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
├── docs/
│   ├── REVIEW-ENHANCEMENTS.md           ← reviewer-noted flaws + suggested enhancements (most now ✅)
│   └── REVIEW-FEATURES.md               ← reviewer-oriented walkthrough of every shipping feature
├── models/                              ← gitignored GGUF files
│   └── qwen2.5-coder-7b-instruct-q4_k_m.gguf
├── data/                                ← gitignored SQLite database lives here (codeintel.db)
├── test-data/sql/                       ← small PL/SQL fixture for manual smoke-testing
├── tests/CodeIntel.Server.Tests/        ← xunit 2.9 test project — parser/resolver/builder unit + integration tests
└── src/CodeIntel.Server/
    ├── CodeIntel.Server.csproj
    ├── Program.cs                       ← DI, SignalR, SPA hosting, eager LLM init, rate limiter
    ├── appsettings.json
    ├── Controllers/
    │   ├── HealthController.cs          ← /healthz (liveness), /readyz (LLM + SQLite readiness) outside /api
    │   ├── WorkspaceController.cs       ← POST /api/workspace/load, GET file, GET definition
    │   ├── AnalysisController.cs        ← presets, status, run (rate-limited), recent, cancel, estimate, diff
    │   ├── IgnoredFindingsController.cs ← GET/POST/DELETE /api/ignored-findings — per-workspace FP suppression
    │   ├── ReportsController.cs         ← analysis MD generation, download, POST save (returns redaction counts)
    │   └── TraceController.cs           ← POST /api/trace/run (rate-limited), candidates (overload disambig), save
    ├── Grammar/
    │   └── PlSqlRefs.g4                 ← ANTLR grammar; codegen via Antlr4BuildTasks NuGet at build time
    ├── Data/
    │   └── CodeIntelDb.cs               ← SQLite connection factory; creates analyses/traces/ignored/cache tables (WAL)
    ├── Hubs/
    │   └── AnalysisHub.cs               ← single "AnalysisEvent" channel (analysis + trace events)
    ├── Services/
    │   ├── RoslynWorkspaceService.cs    ← MSBuildLocator + MSBuildWorkspace, LRU-capped at MaxLoadedWorkspaces=3
    │   ├── LlamaSharpService.cs         ← singleton, semaphore-serialized; reuses Vulkan-probe weights to avoid double load
    │   ├── ContextBuilder.cs            ← raw-text bundling with token budget; PL/SQL dep attachment
    │   ├── PromptTemplateService.cs     ← loads embedded MD prompts, ChatML format
    │   ├── FindingStreamParser.cs       ← O(n) single-pass scanner for <finding> + <request_context>; tracks drops
    │   ├── InvestigationOrchestrator.cs ← agentic loop + CT/watchdogs + cache lookup + skill router + aggregator (ACTIVE)
    │   ├── AnalysisCancellationRegistry.cs ← per-analysis CTS lookup; reused by trace too
    │   ├── ContextRequestHandler.cs     ← fulfills LLM context requests via Roslyn (or PL/SQL resolver for OracleObject)
    │   ├── PlSqlObjectParser.cs         ← ANTLR-backed extractor (replaces regex predecessor)
    │   ├── PlSqlRepoResolver.cs         ← maps a PL/SQL object name → file in workspace (filename + CREATE-OR-REPLACE grep)
    │   ├── TraceWalker.cs               ← Roslyn BFS, memoized FindCallersAsync, dashed back-edges, MaxTotalNodes=100
    │   ├── TraceOrchestrator.cs         ← graph walk → per-node LLM synopsis → save; partial-save on cancel
    │   ├── TraceResultStore.cs          ← SQLite-backed cache for TraceResults
    │   ├── ReportGenerator.cs           ← MD output: GenerateMarkdown(AnalysisResult) + GenerateTraceMarkdown(TraceResult)
    │   ├── ReportWriter.cs              ← writes MD into target repo, runs SecretScrubber, PathSafety-checked
    │   ├── AnalysisResultStore.cs       ← SqliteAnalysisResultStore (LRU pruned to MaxPersistedResults)
    │   ├── AnalysisEstimator.cs         ← POST /api/analysis/estimate — token + seconds projection from recent runs
    │   ├── ContentHasher.cs             ← SHA-256 over file set + cache-key construction (F2)
    │   ├── ResultCache.cs               ← short-circuits a run when {preset, model, content-hash} has been seen
    │   ├── FindingsAggregator.cs        ← collapses near-dupes across iterations on (severity, file, title)
    │   ├── FindingsDiff.cs              ← Added/Resolved/Persisted between two analyses for /diff endpoint
    │   ├── IgnoredFindingsStore.cs      ← SQLite-backed FP signature ignore-list (per workspace root)
    │   ├── PathSafety.cs                ← Path.GetFullPath + separator-aware containment check
    │   ├── SecretScrubber.cs            ← regex pass for AWS/GitHub PAT/JWT/PEM/bearer secrets before saving MD
    │   └── SkillRouter.cs               ← content-keyed prompt addenda (concurrency/raw-sql/http-client/auth/plsql-cursor)
    ├── Models/
    │   ├── AnalysisModels.cs            ← Request, Result (incl. ContentHash), Finding, enums (incl. OracleObject)
    │   ├── AnalysisEvents.cs            ← SignalR event factory
    │   ├── WorkspaceModels.cs           ← Workspace (split into RootFolder + EntryFile), ProjectNode, FileNode, CodeContext
    │   ├── TraceModels.cs               ← TraceRequest (incl. PreferredFqn), TraceEdge (IsBackEdge), EntryPointCandidate
    │   ├── PlSqlModels.cs               ← ParsedObjectReferences, PlSqlResolution, PlSqlObjectKind
    │   └── Options.cs                   ← LlmOptions, AnalysisOptions (cache/LRU/rate-limit tunables), DataOptions
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
            ├── api/                     ← client, workspace, analysis (+ estimateRun), trace, signalr
            ├── stores/                  ← workspaceStore, analysisStore
            ├── types/index.ts           ← shared API contracts
            ├── utils/
            │   └── openInVsCode.ts      ← vscode:// deep-link helper for finding/node "open in editor" buttons
            └── components/
                ├── StatusDot.tsx
                ├── SolutionPanel.tsx        ← left sidebar: load + tree
                ├── FileTree.tsx             ← SimpleTreeView with checkboxes
                ├── FolderPickerDialog.tsx   ← modal filesystem browser for .sln path
                ├── AnalysisPanel.tsx        ← center column + tab bar (Analysis + file previews)
                ├── PromptSelector.tsx       ← preset cards + free text + run + estimate chip
                ├── ResultsView.tsx          ← streaming pane + findings cards + ignore button + open-in-VS-Code
                ├── TracePanel.tsx           ← trace entry-point input + overload candidate picker
                ├── TraceResultsView.tsx     ← Mermaid render + node synopsis cards + idle/elapsed chips
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
- **Raw file text for initial context; Roslyn for follow-up requests** — initial context is assembled from raw file text. When the agentic loop requests a class/method/callers/callees, `ContextRequestHandler` fulfills those via Roslyn symbol resolution; methods now emit `DeclaringSyntaxReferences[0].GetSyntax().ToFullString()` rather than a line-counted approximation. Upgrade initial context assembly to Roslyn extraction if token budget becomes the bottleneck.
- **SQLite over Postgres for persistence** — `Microsoft.Data.Sqlite` with WAL mode, schema created on first open. One file (`data/codeintel.db`), zero ops. Tables: `analyses`, `traces`, `ignored_findings`, `result_cache`. On OpenShift, mount the data directory as a `PersistentVolumeClaim`.
- **Result cache keyed on file content, not file mtime** — `{presetKey, modelName, sha256-of-files}` short-circuits a repeat run. Free-text mode never caches; TTL defaulted to 7 days; eviction lazy on lookup.
- **Findings deduped post-hoc, not during streaming** — `FindingsAggregator.Collapse` merges by `(severity, file, lowercased title)` after the agentic loop ends. Picked over inline dedup so cross-iteration duplicates (the common case) get caught without complicating the parser.
- **Secret scrubbing at write boundary, not at parse** — `SecretScrubber.Scrub` runs in `ReportWriter` right before `File.WriteAllTextAsync`. Per-pattern hit counts are returned to the UI and logged. Doing it later means even paste-as-context user input can be caught.
- **PathSafety helper** — `Path.GetFullPath` + separator-aware containment. Replaces the prior `StartsWith(root)` check that accepted `repofoo` against `repo`.
- **ANTLR over regex for PL/SQL** — the regex predecessor mishandled comments/strings/multi-line statements. The grammar at [Grammar/PlSqlRefs.g4](src/CodeIntel.Server/Grammar/PlSqlRefs.g4) is intentionally narrow — only object references, not full PL/SQL — so codegen is fast and the visitor stays small.
- **Skill router is content-keyed, not preset-keyed** — `SkillRouter` adds prompt addenda when regex hits fire in the context. Stronger than picking a skill from the preset because real files often mix concerns (e.g. an HTTP controller that also calls a DbContext).
- **No auth for MVP, rate limiting today** — explicit choice. The rate limiter (`Analysis:RateLimitRunsPerMinute`, default 5/min per IP) buys back the OOM-risk that no-auth opened up. Windows Auth via IIS passthrough still needed before the OpenShift cutover.
- **Save reports into the loaded repo** (not auto-download) — Copilot Chat's `#file:` syntax wants the file in the workspace. The MD lives at `docs/codeintel/{date}-{preset}-{shortId}.md` by default, configurable per save. Each report ends with a preset-aware prompt the user can paste into Copilot.
- **Local model = briefing officer, Copilot = analyst** — a 7B Qwen produces noisy findings. The architecture deliberately offloads verification to Copilot via the embedded "Copilot Next Step" prompt. Don't try to make the local model good; make it good enough that Copilot's verification round is fast.
- **Cancellation via linked CTS triad** — user cancel (registry), idle-token watchdog (reset per token), overall hard ceiling. All three feed a linked CTS; the catch block inspects which fired to emit a specific reason. Partial findings are still saved.
- **Trace overload disambiguation in the UI, not the server** — `POST /api/trace/candidates` returns all matching methods; the UI picks one and passes `preferredFqn` back to `/run`. Server-side first-match-wins was producing confusing behavior on overloads.

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
7. **ANTLR codegen at build time** — `Antlr4BuildTasks` generates lexer/parser/visitor from [Grammar/PlSqlRefs.g4](src/CodeIntel.Server/Grammar/PlSqlRefs.g4) on every build. If `PlSqlRefsLexer`/`PlSqlRefsParser` show as missing, run `dotnet build` once to regenerate the obj/ output.
8. **SQLite path resolution** — `Data:DatabasePath` is resolved relative to `ContentRootPath`, not CWD. Running tests from elsewhere will create a fresh DB in that directory; tests use a tempfile to avoid cross-contamination.
9. **Rate limiter is per IP, not per user** — on shared infra (NAT, internal proxy, OpenShift Service mesh) one user can starve everyone behind the same source IP. Raise `Analysis:RateLimitRunsPerMinute` or partition on a header when auth lands.
10. **Result cache TTL is per-row, not per-content** — editing `appsettings.json` to lower `ResultCacheTtlHours` does **not** evict existing rows; they're only filtered on lookup. Delete `data/codeintel.db` (or the `result_cache` table) for an immediate flush.

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

**Phase 3 hardening pass** (driven by [docs/REVIEW-ENHANCEMENTS.md](docs/REVIEW-ENHANCEMENTS.md))
- **Persistence (D2)** — `CodeIntelDb` (SQLite, WAL, schema-on-first-open) backs `SqliteAnalysisResultStore`, `SqliteTraceResultStore`, `IgnoredFindingsStore`, and `ResultCache`. Default path `data/codeintel.db` (configurable via `Data:DatabasePath`). LRU pruning to `MaxPersistedResults=200` on every save.
- **Workspace LRU cap (A4)** — `MaxLoadedWorkspaces=3` evicts and disposes the oldest `MSBuildWorkspace` so big solutions don't accumulate in memory.
- **Result cache (F2)** — `ResultCache` short-circuits a repeat run when `{presetKey, modelName, sha256(files)}` is unchanged within `ResultCacheTtlHours` (default 168h). Free-text mode never caches. Cache hit surfaces as an `AnalysisEvent` with `status: "Cache hit — reusing result from HH:MM (N findings)"`.
- **Findings diff (F1)** — `GET /api/analysis/{id}/diff/{previousId}` returns `{added, resolved, persisted}` keyed by `(severity, file, lowercased title)`.
- **Findings aggregator (F8)** — post-loop collapse of near-duplicate findings the 7B model often re-states across iterations.
- **Ignored findings (D1)** — `POST /api/ignored-findings` records a SHA-256 signature against the workspace root; `GET` lists, `DELETE /{signature}` removes. UI surfaces ignore counts and filters out matches on subsequent runs.
- **Skill router (F10)** — `SkillRouter` contributes a content-keyed prompt addendum when any of `concurrency / raw-sql / http-client / auth / plsql-cursor` patterns match the assembled context. Active skills are emitted as a `status` event (`"Skills active: concurrency, http-client"`).
- **Run estimator (F14)** — `POST /api/analysis/estimate` returns `{ estimatedTokens, estimatedSeconds, sampleSize, explanation }` based on the median seconds-per-token across recent runs, with a coarse fallback when history is short.
- **Secret scrubbing (C4)** — `SecretScrubber` runs in `ReportWriter` before write; redacts AWS keys, GitHub PATs, JWTs, PEM blocks, bearer tokens, Slack tokens, `key=value` patterns. Hit counts surface in the save response so the UI can warn.
- **PathSafety (A1)** — `Path.GetFullPath` + separator-aware containment check; used by `ReportWriter` (both analysis and trace writes) and `RoslynWorkspaceService.ReadFileAsync`.
- **Rate limiting (C2)** — `Microsoft.AspNetCore.RateLimiting` fixed-window limiter at `Analysis:RateLimitRunsPerMinute=5/min` per IP on `POST /api/analysis/run` and `POST /api/trace/run`. Rejected requests get a structured 429 body.
- **Health probes (F11 part 1)** — `GET /healthz` (always 200) for liveness; `GET /readyz` (LLM + SQLite) for OpenShift readiness gating. Both live outside `/api`.
- **VS Code deep-link (D3)** — `openInVsCode(absolutePath, line, column)` builds a `vscode://file/...` URL; wired into finding cards (analysis) and node cards (trace).
- **ANTLR PL/SQL parser (B2)** — `Grammar/PlSqlRefs.g4` + `Antlr4BuildTasks` codegen replaces the regex predecessor. Handles quoted identifiers, multi-line statements, and comments/strings correctly.
- **Trace cycle rendering (A11)** — `TraceEdge.IsBackEdge` flag drives a dashed `-.->` arrow in Mermaid so cycles are visually distinguishable.
- **Trace overload disambiguation (A12)** — `POST /api/trace/candidates` returns all matching `IMethodSymbol` candidates; UI picks one; `TraceRequest.PreferredFqn` round-trips the choice exactly.
- **Trace total-node cap (D5)** — `MaxTotalNodes=100` ceiling protects against god-class entry points; hit sets `truncated=true` without dropping already-discovered nodes.
- **Callers cache (A9)** — `TraceWalker.CallersCache` memoizes `FindCallersAsync` per FQN within a single run, cutting the dominant cost on deep BFSs.
- **Method snippet via Roslyn syntax (A7)** — `ContextRequestHandler.FulfillMethodAsync` now emits `DeclaringSyntaxReferences[0].GetSyntax().ToFullString()` instead of a line-counted approximation.
- **Streaming parser O(n) (A8)** — `FindingStreamParser` is a single-pass scanner over newly-appended chunks plus a tail window; no more whole-buffer regex scan per token.
- **Vulkan probe reuse (A5)** — `LlamaSharpService.ResolveBackend` now returns the probe-loaded weights so initialization doesn't read the 4.7 GB GGUF twice.
- **Workspace model split (B4)** — `Workspace.RootFolder` (always a dir) + `Workspace.EntryFile` (nullable) replace the file-vs-folder ambiguity of `ProjectPath`; all consumers updated.
- **Dead AnalysisOrchestrator removed (B3)** — `InvestigationOrchestrator` is the sole implementation.

**Other**
- Dark dev-tool aesthetic (JetBrains Mono + Inter Tight)
- Mermaid via `mermaid` npm package (dark theme, JetBrains Mono labels, htmlLabels)

### ❌ Deferred (next sessions)

- **Auth (C1)** — Windows Auth via IIS passthrough on the internal network. The single most important blocker before OpenShift deployment; the rate limiter buys back some of the OOM-risk but `WorkspaceController.Browse` still leaks the host filesystem to any unauthenticated caller.
- **Configurable CORS origins (B5)** — `Program.cs` still hardcodes `5173/5174`. Move to `Cors:AllowedOrigins` in `appsettings.json`.
- **Multi-language abstraction (B1)** — LSP client + tree-sitter for non-C# repos (TypeScript first). Roslyn becomes "the C# LSP backend," not a special case. Trace mode is C#-only today via Roslyn; LSP would unlock it for TS/Python/etc.
- **Live Oracle introspection (PL/SQL v2)** — `Oracle.ManagedDataAccess.Core` + connection string + queries against `ALL_TABLES` / `ALL_TAB_COLUMNS` / `USER_SOURCE` / `ALL_PROCEDURES` to fetch schemas and stored-proc bodies from a live DB when the repo doesn't have the DDL committed. Also unlocks cross-workspace augmentation (a C# workspace's `DbAccess` trace nodes pulling schema from a sibling Oracle connection). v1 (repo-only) is shipping.
- **Whole-repo dead-code detection** — tree-sitter to enumerate functions, LSP for references, LLM only for ambiguous cases (reflection, DI, dynamic dispatch).
- **Business documentation mode** — walk top-level entry points (controllers, handlers), trace flows, produce a feature catalog. Builds on the existing trace-mode pipeline.
- **File-backed skills (extension of F10)** — `SkillRouter` ships content-keyed addenda hard-coded today; the planned next step is a folder of `SKILL.md` files (csharp-bug-hunting, stored-proc-analysis, etc.) routed by the same predicates.
- **Roslyn-extracted initial context** — currently raw file text for the first iteration. Upgrade to method-level extraction when context budget gets tight.
- **Optional finding-validation pass (F4 "second opinion")** — config flag to run a second LLM pass that critiques iter-1 findings and drops the ones it can't defend. Doubles run time but kills residual FPs.
- **Trace v1.5 polish** — batched synopsis (single LLM call for several small nodes), per-direction branch limits. (Cycle detection + total-node cap + overload disambig + callers cache have all shipped — see above.)
- **OpenShift deployment** — Dockerfile, persistent volumes for model + SQLite, ConfigMap for `appsettings`. The single-project layout was designed for this; never done.
- **Headless CLI (F5)** + **VS Code extension (F6)** + **Watch mode (F7)** — all unblocked by the SQLite persistence + estimator + diff endpoints that just landed, but not implemented yet.
- **Prometheus `/metrics` (F11 part 2)** — `/healthz` + `/readyz` shipped; queue depth + duration histograms still TODO.
- **Notifications (F12) + shared prompt library (F13) + "Explain this commit" (F15)** — see [docs/REVIEW-ENHANCEMENTS.md](docs/REVIEW-ENHANCEMENTS.md) §F.

---

## Pickup Priorities (when resuming)

In recommended order:

1. **Auth (C1) + configurable CORS (B5).** With persistence + rate limiting + ignored-findings + Save-to-repo all shipping, the remaining gating concern before OpenShift is identity. Plan: Windows Auth via IIS passthrough on the internal network, plus a small middleware shim that scopes `WorkspaceController.Browse` (currently still leaks the host filesystem) to an allowlist of root prefixes. Move CORS origins to `Cors:AllowedOrigins` while in `Program.cs`.
2. **Test coverage for the hardening surface.** SQLite-backed stores, `ResultCache`, `IgnoredFindingsStore`, `PathSafety`, `SecretScrubber`, `FindingsAggregator`, `FindingsComparer`, `SkillRouter` — none of these have unit tests yet. Existing xunit project only covers PL/SQL parser/resolver/builder. Target: 70% line coverage on `Services/` before the OpenShift cutover (see [docs/REVIEW-ENHANCEMENTS.md](docs/REVIEW-ENHANCEMENTS.md) §E).
3. **Verify trace-mode in the UI on a richer C# project.** Local smoke-tests passed on `CodeIntel.sln` (Callees depth=1 → 8 nodes ~1m25s; Both depth=2 → 20 nodes ~3m). Want to confirm the experience on something bigger — controller/service entry points in a real LOB app. Now interesting: does the `MaxTotalNodes=100` cap fire? Does the overload-candidate picker get hit? Does the callers cache change wall-clock noticeably?
4. **LSP + tree-sitter abstraction (B1).** Replace direct Roslyn calls in `ContextRequestHandler` + `TraceWalker` with an LSP client interface; add tree-sitter for fast structural scans. Ships TypeScript support (and the trace-mode TS support) for ~free once done. The single biggest compounding lever — analysis, trace, dead-code, business-docs all benefit.
5. **PL/SQL repo-mode UI smoke-test + Oracle live (v2).** v1 (repo-only) is shipping with an ANTLR-backed parser — load a real PL/SQL repo, pick a stored proc, run each of the 4 SQL presets, verify the parser/resolver attaches the right object definitions and the Copilot Next Step briefs read cleanly. Then v2: add `Oracle.ManagedDataAccess.Core`, wire a `SqlOptions` (connection string) + `IOracleSchemaService` that fulfils the `OracleObject` context-request with live `ALL_TABLES` / `USER_SOURCE` data when the repo doesn't have the DDL committed. Pairs with the trace-mode DbAccess NodeKind (cross-workspace augmentation) once the LSP rewrite lands.
6. **Whole-repo dead-code detection.** Tree-sitter to enumerate functions, LSP for references, LLM only for ambiguous cases (reflection, DI, dynamic dispatch). Cheap once LSP is in place.
7. **Business documentation mode.** Walk top-level entry points → trace → catalog. Builds directly on the trace pipeline.
8. **File-backed skills.** `SkillRouter` already ships with content-keyed predicates; the next step is moving the hard-coded addenda into `Prompts/skills/*.md` and routing by the same predicates.
9. **Dockerfile + OpenShift** — multi-stage build (npm → dotnet publish), volume mounts for `/models` + `/data` (SQLite), ConfigMap for runtime tuning, `/readyz` wired to the readiness probe.

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
