# CodeIntel — Project Memory

> Internal dev tool: web app that lets developers analyze C# code and (eventually) SQL Server objects using a local LLM, with handoff to **GitHub Copilot** (team's subscription) via MD reports written into the loaded repo.

## Status

**Phase 1 + Phase 2 verified end-to-end. Save-to-repo + cancellation + watchdogs shipped.**

The agentic loop runs cleanly through to `<done/>` and produces structured findings on a real .sln. On-demand "Save to repo" writes the markdown report plus a preset-aware "Copilot Next Step" prompt into `{repoRoot}/docs/codeintel/` (configurable), with INDEX.md + `.codeintel-index.json` sidecar + one-time folder README. Cancellation is wired through a per-analysis CTS registry with idle-token (90s) and overall (600s) watchdogs; partial findings survive cancel.

Tested A/B finding: the local 7B model produces noisy hedge-y findings — that's exactly what the Copilot Next Step handoff is for. The tightened `find-bugs.md` prompt and parser drop-counters reduce the worst FPs but the architecture deliberately leans on Copilot to verify.

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
└── src/CodeIntel.Server/
    ├── CodeIntel.Server.csproj
    ├── Program.cs                       ← DI, SignalR, SPA hosting, eager LLM init
    ├── appsettings.json
    ├── Controllers/
    │   ├── WorkspaceController.cs       ← POST /api/workspace/load, GET file, GET definition
    │   ├── AnalysisController.cs        ← presets, status, run, recent, cancel
    │   └── ReportsController.cs         ← MD generation, download, POST save
    ├── Hubs/
    │   └── AnalysisHub.cs               ← single "AnalysisEvent" channel
    ├── Services/
    │   ├── RoslynWorkspaceService.cs    ← MSBuildLocator + MSBuildWorkspace
    │   ├── LlamaSharpService.cs         ← singleton, semaphore-serialized inference
    │   ├── ContextBuilder.cs            ← raw-text bundling with token budget
    │   ├── PromptTemplateService.cs     ← loads embedded MD prompts, ChatML format
    │   ├── FindingStreamParser.cs       ← parses <finding> + <request_context>, tracks malformed/incomplete drops
    │   ├── AnalysisOrchestrator.cs      ← one-shot pipeline (not active in DI)
    │   ├── InvestigationOrchestrator.cs ← agentic loop + CT/watchdogs (ACTIVE)
    │   ├── AnalysisCancellationRegistry.cs ← per-analysis CTS lookup for cancel endpoint
    │   ├── ContextRequestHandler.cs     ← fulfills LLM context requests via Roslyn
    │   ├── ReportGenerator.cs           ← MD output + preset-aware Copilot Next Step
    │   ├── ReportWriter.cs              ← writes MD into target repo + INDEX + JSON sidecar
    │   └── AnalysisResultStore.cs       ← in-memory cache
    ├── Models/
    │   ├── AnalysisModels.cs            ← Request, Result, Finding, enums
    │   ├── AnalysisEvents.cs            ← SignalR event factory
    │   ├── WorkspaceModels.cs           ← Workspace, ProjectNode, FileNode, CodeContext
    │   └── Options.cs                   ← LlmOptions, AnalysisOptions
    ├── Prompts/                         ← embedded MD resources
    │   ├── find-dead-code.md
    │   ├── find-bugs.md
    │   ├── find-business-rules.md
    │   └── summarize.md
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

- Solution loading via Roslyn workspace (`MSBuildWorkspace`); TS/Java/SQL via file scan
- File tree UI with project + file selection; folder picker dialog; file preview tabs
- 4 preset prompts: find dead code / bugs / business rules / summarize
- Free-text mode + pinned snippet support
- SignalR token streaming with live UI updates (started, token, finding, status, iteration, contextRequested/Fulfilled, completed, cancelled, error)
- Structured finding extraction during stream (`<finding>` blocks) with malformed/incomplete drop counters
- **Agentic investigation loop** — `InvestigationOrchestrator` (default 3 iterations); LLM emits `<request_context>` to pull additional file/class/method/callers/callees/search via Roslyn
- Inline code annotations (`CodeAnnotationView`)
- **MD report generation** with preset-aware "Copilot Next Step" prompt section
- **Save to repo flow** — `POST /api/reports/{id}/save` writes the markdown into `{repoRoot}/{Analysis:ReportOutputPath}` (default `docs/codeintel/`), maintains `INDEX.md` and `.codeintel-index.json` sidecar, drops a one-time folder README, guards against writes outside the workspace root
- UI surfaces: Save to repo button + path-override input + post-save banner with copy-path / copy-`#file:` reference buttons
- Download endpoint kept as fallback (`/api/reports/{id}/download`)
- **Cancellation infrastructure** — per-analysis `CancellationTokenSource` registry, `POST /api/analysis/{id}/cancel`, distinct `cancelled` SignalR event, partial findings preserved on cancel
- **Watchdogs** — idle-token timeout (90s default, reset per token) + overall hard ceiling (600s default), both configurable
- UI: Cancel button while running, live elapsed seconds, idle warn chip (≥30s without tokens), distinct cancelled-state styling (neutral for user, amber for timeout/idle)
- Tightened `find-bugs.md` prompt with anti-hedging + safe-API allowlists (suppresses most of the common Qwen 7B FP classes)
- Dark dev-tool aesthetic (JetBrains Mono + Inter Tight)
- In-memory result history (`AnalysisResultStore`)

### ❌ Deferred (next sessions)

- **Call-trail trace mode** — start at a function/entry point, walk the Roslyn call graph N levels, per-node LLM synopsis, emit Mermaid flow diagram + prose. The killer feature for bug investigation and onboarding.
- **Multi-language abstraction** — LSP client + tree-sitter for non-C# repos (TypeScript first). Roslyn becomes "the C# LSP backend," not a special case.
- **Database introspection** — dump table schemas + stored proc bodies into context. Plan is to use existing app DB connection; persistent tables for session history go in our own DB.
- **Whole-repo dead-code detection** — tree-sitter to enumerate functions, LSP for references, LLM only for ambiguous cases (reflection, DI, dynamic dispatch).
- **Business documentation mode** — walk top-level entry points (controllers, handlers), trace flows, produce a feature catalog with flow diagrams.
- **Skills system** — folder of `SKILL.md` files (csharp-bug-hunting, stored-proc-analysis, etc.) routed into prompts by mode + keyword.
- **Roslyn-extracted initial context** — currently raw file text for the first iteration. Upgrade to method-level extraction when context budget gets tight.
- **Optional finding-validation pass** — config flag to run a second LLM pass that critiques iter-1 findings and drops the ones it can't defend. Doubles run time but kills residual FPs.
- **OpenShift deployment** — Dockerfile, persistent volume for model, ConfigMap for `appsettings`.
- **Auth** — Windows Auth via IIS passthrough on the internal network.
- **Persistence** — DB tables for analysis history, saved reports, prompt template versions.

---

## Pickup Priorities (when resuming)

In recommended order:

1. **Build call-trail trace mode.** Pick an entry point (controller action or stack-trace frame), Roslyn-walk the call graph N levels, per-node LLM synopsis, emit Mermaid + prose synopsis. This is the unique-value feature — no chat LLM can reliably do this without the structural plumbing.
2. **LSP + tree-sitter abstraction.** Replace direct Roslyn calls in `ContextRequestHandler` with an LSP client interface; add tree-sitter for fast structural scans. Ships TypeScript support for ~free once done.
3. **SQL Server schema introspection.** Two flavors: live `INFORMATION_SCHEMA` query for the dev DB + parsing of source migrations. Cross-reference schema chunks into context when a finding involves SQL.
4. **Skills system.** Specialized prompts (`bug-async.md`, `bug-sql-injection.md`, etc.) that get routed in based on file content. Bigger quality boost on a 7B than any general prompt.
5. **Dockerfile + OpenShift** — multi-stage build (npm → dotnet publish), volume mount for `/models`, ConfigMap for runtime tuning.

---

## Commands Cheat Sheet

```powershell
# Dev run
dotnet run --project src\CodeIntel.Server

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
