# CodeIntel — Project Memory

> Internal dev tool: web app that lets developers analyze C# code and (eventually) SQL Server objects using a local LLM, with optional handoff to Claude Opus via MD reports.

## Status

**Phase 1 + Phase 2 implemented — not yet verified on a real run.**

Phase 1 (one-shot pipeline) and Phase 2 (agentic investigation loop) are both coded. The agentic orchestrator (`InvestigationOrchestrator`) is the active DI registration. Neither phase has been tested end-to-end yet — verify clean build + first run is priority #1.

Origin: dev days project, intended to grow into a team tool deployed on internal OpenShift.

---

## Architecture (one-paragraph)

ASP.NET Core (.NET 10) hosts both the API and the React 19 SPA. The .NET server runs a GGUF model in-process via LLamaSharp. A request to `/api/analysis/run` returns immediately with an `analysisId`; the `InvestigationOrchestrator` runs an agentic loop (up to 5 iterations): it builds context, assembles a Qwen-formatted ChatML prompt from an embedded MD template, streams tokens through `ILlmService.StreamAsync`, and the `FindingStreamParser` reads both `<finding>{...}</finding>` and `<request_context>...</request_context>` blocks out of the stream. When the LLM requests more context, `ContextRequestHandler` fulfills it via Roslyn (file, class, method, callers, callees, search); findings and raw tokens are pushed to the client over SignalR. Results cache in-memory by GUID; `/api/reports/{id}/download` produces an MD report designed to be pasted into Claude Opus for fix plans, Jira tickets, etc. A non-agentic `AnalysisOrchestrator` also exists as a fallback but is not wired in DI.

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
    │   ├── WorkspaceController.cs       ← POST /api/workspace/load, GET file
    │   ├── AnalysisController.cs        ← presets, status, run, recent
    │   └── ReportsController.cs         ← MD generation + download
    ├── Hubs/
    │   └── AnalysisHub.cs               ← single "AnalysisEvent" channel
    ├── Services/
    │   ├── RoslynWorkspaceService.cs    ← MSBuildLocator + MSBuildWorkspace
    │   ├── LlamaSharpService.cs         ← singleton, semaphore-serialized inference
    │   ├── ContextBuilder.cs            ← raw-text bundling with token budget
    │   ├── PromptTemplateService.cs     ← loads embedded MD prompts, ChatML format
    │   ├── FindingStreamParser.cs       ← parses <finding> + <request_context> from stream
    │   ├── AnalysisOrchestrator.cs      ← one-shot pipeline (not active in DI)
    │   ├── InvestigationOrchestrator.cs ← agentic loop, up to 5 iterations (ACTIVE)
    │   ├── ContextRequestHandler.cs     ← fulfills LLM context requests via Roslyn
    │   ├── ReportGenerator.cs           ← MD output for Opus handoff
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

### ✅ Coded (not yet end-to-end tested)

- Solution loading via Roslyn workspace
- File tree UI with project + file selection; folder picker dialog
- 4 preset prompts: find dead code / bugs / business rules / summarize
- Free-text mode
- SignalR token streaming with live UI updates
- Structured finding extraction during stream (`<finding>` blocks)
- **Agentic investigation loop** — `InvestigationOrchestrator` (up to 5 iterations); LLM emits `<request_context>` to pull additional file/class/method/callers/callees/search results via Roslyn
- Inline code annotations (`CodeAnnotationView`) + file preview tabs (`FilePreviewPanel`)
- MD report generation with Opus handoff section + download
- Dark dev-tool aesthetic (JetBrains Mono + Inter Tight)
- In-memory result history

### ❌ Deferred (next sessions)

- **Database introspection** — dump table schemas + stored proc bodies into context. Plan is to use existing app DB connection; persistent tables for session history go in our own DB.
- **Skills system** — folder of `SKILL.md` files (csharp-bug-hunting, stored-proc-analysis, etc.) injected into prompts.
- **Roslyn-extracted initial context** — currently raw file text for the first iteration. Upgrade to method-level extraction when context budget gets tight.
- **OpenShift deployment** — Dockerfile, persistent volume for model, ConfigMap for `appsettings`.
- **Auth** — Windows Auth via IIS passthrough on the internal network.
- **Persistence** — DB tables for analysis history, saved reports, prompt template versions.

---

## Pickup Priorities (when resuming)

In recommended order:

1. **Verify clean build + first run.** `dotnet run --project src\CodeIntel.Server`. Load a real `.sln`, select files, run "find bugs" preset, watch tokens stream, confirm findings appear as cards, export MD report. Fix anything broken before proceeding.
2. **Smoke-test the agentic loop.** Pick a preset that's likely to trigger a `<request_context>` (e.g. "find bugs" on a few files). Watch SignalR events for `iterationStarted` / `contextRequested` / `contextFulfilled` events. Confirm the loop terminates and findings still appear. `ContextRequestHandler` Roslyn resolution is the most likely gap.
3. **DB context.** Extend `ContextBuilder` to accept selected DB objects (table names, proc names), pull from a `DbIntrospectionService`, include in prompt context.
4. **Skills.** Create `Skills/` folder structure, `SkillRouter` for mode + keyword routing, inject relevant skill content into system prompt.
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

1. **Local LLM** — does the heavy file-reading and pattern matching
2. **MD reports** — structured handoff document, the bridge layer
3. **Claude Opus** — consumes the MD report to produce Jira tickets, fix plans, PR descriptions, risk assessments

The MD bridge is the deliberate architectural choice that distinguishes this from just running a local LLM yourself: it produces a durable artifact that's human-readable, AI-consumable, and saveable as documentation.

Eventual deployment: internal URL on OpenShift, every dev on the team uses it from their browser, no installs.
