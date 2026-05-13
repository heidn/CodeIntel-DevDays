# CodeIntel — Feature Inventory, Usage, and Test Plan

A reviewer-oriented walkthrough of every shipping feature: what it does, how to drive it, and how to verify it works. Sorted by user-visible surface (workspace → analysis → trace → save → ops). Updated after the Phase 3 hardening pass — see [REVIEW-ENHANCEMENTS.md](REVIEW-ENHANCEMENTS.md) for the bug/feature backlog that drove the new sections.

---

## What's new since the last review

Persisted history + ignored findings + result cache (SQLite), per-IP rate limiting on `/run`, LRU caps on workspaces and result rows, ANTLR-backed PL/SQL parser, secret scrubbing before save, cost/time estimator, findings diff, findings aggregator, content-keyed skill router, VS Code deep-link from finding/node cards, trace cycle detection + overload disambiguation + total-node cap, `/healthz` + `/readyz` probes. New sections below: §3.5 (estimate), §3.6 (diff), §3.7 (ignored findings), §6.5 (trace overload candidates), §10.5 (cache), §13 (health probes), §14 (operational tunables).

---

## 0. Prerequisites & quickstart

| Need | Where |
|---|---|
| .NET 10 SDK | https://dotnet.microsoft.com/download/dotnet/10.0 |
| Node.js 20+ | https://nodejs.org |
| GGUF model file (~4.7 GB) | drop into [models/](models/), default `qwen2.5-coder-7b-instruct-q4_k_m.gguf` — path in [appsettings.json:10](src/CodeIntel.Server/appsettings.json#L10) |

```powershell
dotnet restore
cd src\CodeIntel.Server\ClientApp; npm install; cd ..
dotnet run
# → http://localhost:5000
```

**Smoke-test the install:**
1. Top-right status dot turns **amber → green** within ~30–60s (LLM load on CPU).
2. `GET http://localhost:5000/api/analysis/status` returns `{"llmReady":true, ...}`.

---

## 1. Workspace loading

### What it does
Loads a project for analysis. Language is auto-detected from the path or contents:
- `.sln` / `.csproj` → C#, loaded via **Roslyn `MSBuildWorkspace`** (full semantic model).
- `tsconfig.json` / `package.json` → TypeScript (file-scan only — no semantic model).
- `pom.xml` / `build.gradle*` → Java (file-scan).
- `.sql` / `.pkg` / `.pkb` or a folder dominated by them → **PL/SQL repo mode** (file-scan).

Detection logic: [RoslynWorkspaceService.cs:51-95](src/CodeIntel.Server/Services/RoslynWorkspaceService.cs#L51-L95).

### How to use
1. Click **Load project** in the left sidebar.
2. Pick a `.sln`/`.csproj` (C#) or a folder (TS/Java/PL/SQL).
3. The file tree populates; pick files via checkboxes.

### How to test
- **C# happy path:** load `CodeIntel.sln` itself — projects + files appear; `GET /api/workspace/{id}` returns the tree.
- **PL/SQL happy path:** load [test-data/sql/](test-data/sql/) — language detected as `sql`, presets in the right column switch to the four SQL presets ([PromptTemplateService.cs:31-49](src/CodeIntel.Server/Services/PromptTemplateService.cs#L31-L49)).
- **Folder browser:** `GET /api/workspace/browse?path=C:\` — returns subdirs, drives, and any project files; verify hidden / `node_modules` / `bin` / `obj` are filtered ([WorkspaceController.cs:77-85](src/CodeIntel.Server/Controllers/WorkspaceController.cs#L77-L85)).
- **Bad path:** `POST /api/workspace/load {"path":"C:\\does-not-exist"}` → 404 with JSON error.
- **Definition jump:** open any `.cs` file, ctrl-click an identifier (or call `GET /api/workspace/{id}/definition?file=...&line=...&character=...`) — returns `{ filePath, line, character, symbolName }` from Roslyn ([RoslynWorkspaceService.cs:309-353](src/CodeIntel.Server/Services/RoslynWorkspaceService.cs#L309-L353)). For TS/Java/SQL it falls back to a regex declaration scan ([RoslynWorkspaceService.cs:429-468](src/CodeIntel.Server/Services/RoslynWorkspaceService.cs#L429-L468)).

---

## 2. Analysis mode — preset prompts

### What it does
Runs one of 8 prompt presets against the selected files. The 4 C#/TS/Java presets and 4 PL/SQL presets are language-filtered in the UI.

| Preset key | Title | Languages |
|---|---|---|
| `find-dead-code` | Find Dead Code | C# / TS / Java |
| `find-bugs` | Find Bugs | C# / TS / Java |
| `find-business-rules` | Find Business Rules | C# / TS / Java |
| `summarize` | Summarize | all |
| `find-bugs-sql` | Find PL/SQL Bugs | SQL |
| `find-business-rules-sql` | Extract PL/SQL Rules | SQL |
| `cleanup-stored-proc` | Stored Proc Cleanup | SQL |
| `efficiency-review` | Efficiency Review | SQL |

Sources: [PromptTemplateService.cs:31-49](src/CodeIntel.Server/Services/PromptTemplateService.cs#L31-L49), prompt bodies in [src/CodeIntel.Server/Prompts/](src/CodeIntel.Server/Prompts/).

### How to use
1. Load a workspace.
2. Tick one or more files in the tree.
3. (Optional) open a file preview, select a region, click **Pin to analysis** to attach a `PinnedSnippet`.
4. Click a preset card → **Run**.
5. Watch the streaming pane: tokens flow live, structured findings appear as cards.

### How to test
- **API call:** `POST /api/analysis/run` with `{ mode:"preset", presetKey:"find-bugs", selectedFilePaths:["..."], workspaceId:"..." }`. Returns `{ analysisId }`. Subscribe to SignalR group = the analysisId on `/hubs/analysis`.
- **SignalR events to verify** (single channel `AnalysisEvent`, type-discriminated):
  - `status`, `started`, `iterationStarted`, `token`, `finding`, `contextRequested`, `contextFulfilled`, `completed`.
- **Findings parsing:** the LLM should emit `<finding>{...}</finding>` blocks; parser at [FindingStreamParser.cs:19-21](src/CodeIntel.Server/Services/FindingStreamParser.cs#L19-L21).
- **Final result:** `GET /api/analysis/{id}` returns the full `AnalysisResult` ([AnalysisModels.cs:59-73](src/CodeIntel.Server/Models/AnalysisModels.cs#L59-L73)).
- **Recent list:** `GET /api/analysis/recent?count=20`.
- **Inline annotations:** click a finding card with a `lineNumber` → the `CodeAnnotationView` should scroll to and highlight that line.

---

## 3. Analysis mode — free-text question

### What it does
Same pipeline as preset, but the task block is a user-supplied question instead of a canned template. Findings remain structured (the model is still instructed to emit `<finding>` blocks when warranted). **Free-text mode never hits the result cache** (no preset key).

### How to use
- Toggle to **Free text**, type a question, run.

### How to test
- `POST /api/analysis/run` with `{ mode:"freeText", freeTextPrompt:"...", selectedFilePaths:[...], workspaceId:"..." }`.
- Validation: empty prompt → `400 { error:"FreeTextPrompt is required..." }`.

---

## 3.5. Run estimator (F14)

### What it does
Before kicking off a run, the UI calls `POST /api/analysis/estimate` to show a `"~12,400 tokens, est. 2m 30s — based on median of your last 5 runs"` chip. Reduces the surprise factor on heavy runs.

### How to use
Open the prompt selector with files selected; the estimate chip appears below the preset cards.

### How to test
- `POST /api/analysis/estimate {"workspaceId":"...","selectedFilePaths":["..."]}` → `{ estimatedTokens, estimatedSeconds, sampleSize, explanation }`.
- **No history:** delete `data/codeintel.db`, run estimate → `explanation: "no history yet — using a coarse default rate"`.
- **With history:** run ≥2 analyses, re-estimate → `explanation: "based on median of your last N runs"`. Source: [AnalysisEstimator.cs](src/CodeIntel.Server/Services/AnalysisEstimator.cs).

---

## 3.6. Findings diff (F1)

### What it does
Compare two analyses on the same workspace+preset and bucket their findings into **added**, **resolved**, **persisted**. Signature is `(severity, filePath, lowercased title)` — line numbers can drift between runs and aren't part of the key.

### How to test
- `GET /api/analysis/{afterId}/diff/{beforeId}` → `{ added:[...], resolved:[...], persisted:[...], counts:{...} }`.
- Run `find-bugs` on a file, fix one of the findings in the source, re-run → that finding lands in `resolved`. Add a new bug → it lands in `added`. Source: [FindingsDiff.cs](src/CodeIntel.Server/Services/FindingsDiff.cs).

---

## 3.7. Ignored findings (D1)

### What it does
Persist a per-workspace ignore-list keyed on the same `(severity, file, title)` signature as the diff. Future runs in the same workspace silently drop findings whose signature is on the list.

### How to test
- **UI:** click the "ignore" button on a finding card; verify the card disappears and a small "+1 ignored" chip appears on the results header.
- **API:** `POST /api/ignored-findings {"workspaceId":"...","finding":{...},"note":"FP — null check is upstream"}` → `{ signature: "AB12CD34EF56..." }`.
- `GET /api/ignored-findings?workspaceId=...` → list of ignored items.
- `DELETE /api/ignored-findings/{signature}?workspaceId=...` → un-ignore.
- Source: [IgnoredFindingsStore.cs](src/CodeIntel.Server/Services/IgnoredFindingsStore.cs), [IgnoredFindingsController.cs](src/CodeIntel.Server/Controllers/IgnoredFindingsController.cs).

---

## 4. Agentic investigation loop

### What it does
Up to `MaxAgenticIterations` (default 3) passes of the LLM. Mid-stream, the model can emit `<request_context type="...">target</request_context>` to pull more code. [ContextRequestHandler](src/CodeIntel.Server/Services/ContextRequestHandler.cs) fulfills each:

| Type | Fulfillment |
|---|---|
| `file` | Read by path; fallback to filename lookup across workspace |
| `class` | Roslyn `SymbolFinder.FindDeclarationsAsync` (filter=Type) → emit file. Fallback: regex `\bclass\s+Name\b` |
| `method` | Same, filter=Member → snippet of up to 80 lines from the declaration |
| `callers_of` | Roslyn `SymbolFinder.FindCallersAsync` |
| `callees_of` | Text-search fallback only |
| `search_code` | Regex case-insensitive search across all files |
| `oracle_object` | PL/SQL repo resolver — filename match, then CREATE OR REPLACE DDL grep |

### How to test
- Run `find-bugs` on a non-trivial file; expect 2–3 iterations and at least one `contextRequested`/`contextFulfilled` event pair.
- Verify `iterationStarted` event for each pass.
- **Method snippet quality (A7 fix):** request a `method` context for a small method (≤10 lines) → confirm the returned snippet is exactly the method body, not 80 lines bleeding into the next declaration. Roslyn-backed via `DeclaringSyntaxReferences[0].GetSyntaxAsync()` in [ContextRequestHandler.FulfillMethodAsync](src/CodeIntel.Server/Services/ContextRequestHandler.cs#L125).
- The continuation prompt is built by `PromptTemplateService.BuildContinuationPrompt` — inspect via server logs (Serilog `LogDebug "Continuation prompt length"`).

---

## 4.5. Skill router (F10)

### What it does
Before the agentic loop kicks off, `SkillRouter.RouteSkills(context)` runs five content-keyed predicates and contributes a prompt addendum biasing the model toward the relevant issue class. The active skill names are emitted as an `AnalysisEvent` with `type: "status"` so the UI can show `"Skills active: concurrency, http-client"`.

| Skill | Fires on |
|---|---|
| `concurrency` | `async/await`, `Task.`, `Parallel.`, `Thread.`, `lock(` |
| `raw-sql` | string-concatenated SQL or `FromSqlRaw`/`ExecuteSqlRaw` |
| `http-client` | `HttpClient`, `IHttpClientFactory`, `RestClient`, `new HttpRequestMessage` |
| `auth` | `Authentication`, `Authorize`, `Principal`, `Claim`, `Identity`, `Password`, `Jwt`, etc. |
| `plsql-cursor` | SQL workspace + `CURSOR`, `OPEN ... FETCH`, `EXCEPTION WHEN`, `RAISE_APPLICATION_ERROR` |

### How to test
- Run `find-bugs` against an async-heavy file → status pane shows `"Skills active: concurrency"`.
- Run against a file with both `HttpClient` and EF → `"Skills active: http-client, raw-sql"` (if it also has FromSqlRaw) or `"http-client"` alone.
- Source: [SkillRouter.cs](src/CodeIntel.Server/Services/SkillRouter.cs).

---

## 5. PL/SQL repo mode (v1)

### What it does
- Load a folder of `.sql`/`.pkg`/`.pkb` files; language detected as `Sql`.
- When any selected file is PL/SQL, [ContextBuilder](src/CodeIntel.Server/Services/ContextBuilder.cs) auto-resolves referenced tables/views/procedures/packages and attaches their definitions under a `--- PL/SQL OBJECT DEFINITIONS ---` header.
- The 4 SQL presets ship with anti-hedging templates and produce preset-aware Copilot briefs in the saved report.
- During agentic iterations the LLM can emit `<request_context type="oracle_object">NAME</request_context>` to fetch any object on demand.
- **Parser now backed by ANTLR (B2 fix):** [Grammar/PlSqlRefs.g4](src/CodeIntel.Server/Grammar/PlSqlRefs.g4) is the source of truth. Quoted identifiers, schema-qualified names, multi-line statements, and comment/string handling are now grammar-level concerns rather than regex hacks. Codegen runs at build time via the `Antlr4BuildTasks` NuGet.

### How to use
1. Load a folder of PL/SQL files.
2. Tick a stored proc.
3. Pick one of the 4 SQL presets, run.
4. Save the report — the Copilot Next Step section is preset-specific (Jira-ready bugs / Confluence rules / sequenced refactor / EXPLAIN-PLAN review).

### How to test
- Unit coverage exists for parser/resolver/builder: [tests/CodeIntel.Server.Tests/](tests/CodeIntel.Server.Tests/). Run `dotnet test tests\CodeIntel.Server.Tests`.
- **End-to-end smoke** (still pending per [CLAUDE.md](CLAUDE.md) pickup #5): load a real PL/SQL repo, run all four SQL presets, confirm referenced objects show up in the prompt and the saved Copilot brief reads cleanly.
- **Resolver fallback:** rename a package file so the filename no longer matches the package name — verify the CREATE OR REPLACE DDL grep still finds it ([PlSqlRepoResolver](src/CodeIntel.Server/Services/PlSqlRepoResolver.cs)).
- **Parser corner cases:** `-- FROM customer` inside a comment must not register `customer` as a table — covered in [PlSqlObjectParserTests.cs](tests/CodeIntel.Server.Tests/PlSqlObjectParserTests.cs). Quoted identifier `"My Table"` round-trips cleanly through the grammar.

---

## 6. Trace mode — call-trail visualization

### What it does
Roslyn BFS in call-graph space, with per-node 1–2-sentence LLM synopses and a programmatic Mermaid render. C# only (uses Roslyn).

- **Inputs:** method name (`Class.Method` or `Namespace.Class.Method`) OR a `{filePath, line, character}` location. Overloads disambiguated via §6.5.
- **Direction:** `Callers` / `Callees` / `Both`.
- **Depth:** 1–5 (validated server-side).
- **Per-node fan-out cap:** 8 — excess sets `truncated=true`.
- **Total-node ceiling (D5 fix):** 100 nodes max per trace. Already-discovered nodes are kept; further expansion stops with `truncated=true`. Protects against god-class entry points.
- **Callers cache (A9 fix):** `CallersCache` memoizes `SymbolFinder.FindCallersAsync` per FQN within a single trace run.
- **Node classification:** every node tagged `Normal | DbAccess | HttpCall` via inheritance walk + method-name heuristics. Mermaid renders DB as a cylinder, HTTP as a hexagon, both color-coded.
- **Cycle handling (A11 fix):** `visited` HashSet prevents re-expansion; back-edges set `IsBackEdge=true` on `TraceEdge` and render as dashed `-.->` Mermaid arrows.

### How to use
1. Toggle the **Analysis | Trace** pill at the top of [AnalysisPanel.tsx](src/CodeIntel.Server/ClientApp/src/components/AnalysisPanel.tsx).
2. Type a method name OR use **Trace from here** in a file preview to seed the entry point as a location chip.
3. Pick direction + depth, click Run.
4. Watch the Mermaid render fill in; each node card gets a synopsis as the LLM emits one.
5. Click a node to open the source file at the right line.

### How to test
- **API:** `POST /api/trace/run` with `{ workspaceId, entryPoint:{methodName:"Foo.Bar"}, direction:"callees", depth:2, preferredFqn:null }` → `{ traceId }`. Validation rules in [TraceController.cs](src/CodeIntel.Server/Controllers/TraceController.cs).
- **SignalR events:** `traceGraphReady` → N × `traceNodeSynopsis` → `completed`.
- **Smoke baseline:** `TraceWalker.cs::BuildGraphAsync` as entry, Callees, depth=1 → ~8 nodes, ~1m25s on the dev CPU. Depth=2 Both → ~20 nodes, ~3m. Documented in [CLAUDE.md](CLAUDE.md) status.
- **Trace from here:** open any `.cs` file, click an identifier, click the **Trace from here** button — the trace panel should switch into focus with the entry-point chip pre-populated.
- **NodeKind classification:** trace a controller method that calls `DbContext.SaveChangesAsync` AND `HttpClient.GetAsync` → expect both a cylinder and a hexagon in the Mermaid.
- **Cycle rendering (A11):** trace a method involved in a recursive or mutual-recursion call → back-edges render dashed `-.->` in the Mermaid output rather than solid.
- **Total-node cap (D5):** trace a god-class with depth=5 Both → `truncated:true` in the response; UI shows the truncation banner.
- **Save trace:** `POST /api/trace/{id}/save` → writes a trace report into `docs/codeintel/` with a direction-aware Copilot brief.
- **Cancel:** while running, hit Cancel — verify `cancelled` event arrives with `reason="user"` and the partial graph + any completed synopses are saved.
- **Rate limit:** fire 6+ traces from the same IP within 60s → 6th returns `429 { error: "Too many runs in the last minute..." }`.

---

## 6.5. Trace overload disambiguation (A12)

### What it does
When the entry-point name matches multiple `IMethodSymbol` candidates (overloads, multi-target frameworks), the UI calls `POST /api/trace/candidates` first and presents a picker. The chosen FQN is round-tripped back to `/run` via `TraceRequest.PreferredFqn` so the server can pick exactly that overload — no more first-match-wins ambiguity.

### How to test
- **Single match:** type a uniquely-named method → picker doesn't render; trace runs directly.
- **Multiple matches:** type `Equals` or any common name → `POST /api/trace/candidates` returns >1 `EntryPointCandidate` records (`fqn`, `displayName`, `filePath`, `line`, `signature`); UI shows the picker.
- **API:** `POST /api/trace/candidates {"workspaceId":"...","entryPoint":{"methodName":"Foo.Bar"}}` → `[{fqn, displayName, filePath, line, signature}, ...]`.
- Source: [TraceWalker.ResolveCandidatesAsync](src/CodeIntel.Server/Services/TraceWalker.cs).

---

## 7. Save report to repo (analysis + trace)

### What it does
Writes the report markdown into `{workspaceRoot}/{Analysis:ReportOutputPath}` (default `docs/codeintel/`) along with:
- `INDEX.md` — human-readable, regenerated newest-first
- `.codeintel-index.json` — canonical sidecar with `Kind: "analysis" | "trace"` discriminator
- `README.md` — one-time, explaining the folder
- Each report ends with a **preset-aware "Copilot Next Step"** section the user references via `#file:<filename>` in Copilot Chat.

**Pre-write hardening:**
- `PathSafety.IsInside` (A1 fix) — `Path.GetFullPath` + separator-aware containment. `repo` and `repofoo` no longer collide.
- `SecretScrubber.Scrub` (C4 fix) — regex pass replaces matched AWS keys, GitHub PATs, JWTs, PEM blocks, bearer tokens, generic `key=value` patterns, and Slack tokens with `[REDACTED:<reason>]`. Per-pattern hit counts are returned on the response and logged.

### How to use
- After an analysis or trace completes, click **Save to repo**. Optionally override the path in the input next to the button.
- The post-save banner has **Copy path** and **Copy `#file:` reference** buttons, plus a redaction count if any secrets were scrubbed.

### How to test
- `POST /api/reports/{analysisId}/save` (or `/api/trace/{traceId}/save`), body `{"outputPath":"docs/codeintel"}` or `null`. Response now includes `redactionCount` and `redactions: { "aws-key": 1, "github-pat": 0, ... }`.
- **Path-escape guard (A1):** try `outputPath: "../../../../escape"` → server logs a warning and returns 500; nothing is written outside the workspace root. Also try `outputPath: "../{workspaceParentDir}foo"` (the `foo`/`foobar` confusion that the old prefix-check missed) — must also be rejected.
- **Secret scrub (C4):** analyze a file containing `aws_secret_access_key = "ABCDEFGHIJ1234567890ABCDEFGHIJ1234567890"` → saved MD shows `[REDACTED:aws-secret]` and the save response has `redactionCount > 0`.
- **INDEX integrity:** save two analyses + one trace, open `INDEX.md` — verify columns are: Date / Type / Subject / Result / Report. Trace rows show node count; analysis rows show finding counts.
- **Workspace required:** unload the workspace (server restart) and try to save → 400 "Workspace is no longer loaded."

### Download fallback
`GET /api/reports/{analysisId}/download` still works for users who don't want to commit.

---

## 8. Cancellation + watchdogs

### What it does
Three cancellation paths feed a linked CTS ([InvestigationOrchestrator.cs:67-75](src/CodeIntel.Server/Services/InvestigationOrchestrator.cs#L67-L75), same pattern in [TraceOrchestrator.cs:63-69](src/CodeIntel.Server/Services/TraceOrchestrator.cs#L63-L69)):

| Source | Trigger | Defaults |
|---|---|---|
| User cancel | `POST /api/analysis/{id}/cancel` | — |
| Idle watchdog | No tokens for N seconds (reset per token) | 90s |
| Overall watchdog | Total elapsed exceeds N seconds | 600s |

All three configurable in [appsettings.json:19-27](src/CodeIntel.Server/appsettings.json#L19-L27). The orchestrator's catch block inspects which CTS fired to emit a specific `cancelled` event with `reason ∈ {user, idle, timeout, unknown}` and **saves any partial findings/synopses** so Save-to-repo still works after a cancel.

### How to test
- **User cancel:** run analysis, hit Cancel, expect `cancelled` event with `reason:"user"`.
- **Idle watchdog:** lower `IdleTokenTimeoutSeconds` to `5` in `appsettings.json`, run a heavy analysis, kill the model mid-flight (or just wait if your model often stalls) — expect `reason:"idle"`.
- **Overall watchdog:** lower `OverallTimeoutSeconds` to `30`, run a multi-iteration analysis — expect `reason:"timeout"`.
- **Partial save:** cancel mid-run, confirm `GET /api/analysis/{id}` returns the analysis with the findings collected so far, and Save-to-repo succeeds.

---

## 9. SignalR event channel

### What it does
Single named event `"AnalysisEvent"` carries every server-to-client update, discriminated by `type`. Hub: [AnalysisHub.cs](src/CodeIntel.Server/Hubs/AnalysisHub.cs). Events factory: [AnalysisEvents.cs](src/CodeIntel.Server/Models/AnalysisEvents.cs).

| `type` | When |
|---|---|
| `status` | Phase change ("Building context...", etc.) |
| `started` | After context built |
| `iterationStarted` | Each agentic pass |
| `token` | Live token stream |
| `finding` | `<finding>` block parsed |
| `contextRequested` / `contextFulfilled` | Per agentic request |
| `traceGraphReady` | Roslyn BFS done, before synopses |
| `traceNodeSynopsis` | One per node |
| `completed` / `cancelled` / `error` | Terminal |

### How to test
Subscribe with [@microsoft/signalr](https://www.npmjs.com/package/@microsoft/signalr) to `/hubs/analysis`, call `joinAnalysis(id)`, listen for `AnalysisEvent`. Vite dev proxy already forwards WebSocket (`ws: true`) — verify the proxy logs `[vite] proxying ... /hubs/analysis`.

---

## 10. LLM service (LLamaSharp)

### What it does
Singleton hosting a single GGUF model. Inference is serialized by `SemaphoreSlim(1,1)` because LLamaSharp contexts are not thread-safe. Auto/Vulkan backend probe falls back to CPU on failure. SHA-256 model integrity check at load. Source: [LlamaSharpService.cs](src/CodeIntel.Server/Services/LlamaSharpService.cs).

**Hardening:**
- **A5 fix:** Vulkan probe weights are reused on the second load — no more 4.7 GB double-read on startup.
- **A6 fix:** `Dispose` flips `_isReady` first (so racing `StreamAsync` callers fail fast), clears `_executor`, then disposes weights + lock. Idempotent via `_disposed`.

### How to test
- **Hash mismatch:** corrupt the model file (or change `Llm:ModelSha256`) → startup logs "Model file hash mismatch" and `IsReady` stays false.
- **Missing model:** delete the file → log warns "Model file not found... LLM features will be unavailable" and the status dot stays amber.
- **Backend pick:** set `Llm:Backend = "cpu"` → forced CPU. Set to `"vulkan"` on a non-Vulkan box → explicit throw on startup.
- **No double-load:** start with Vulkan available, check startup logs — only one `Loading model from {Path}` line should appear before `Backend: vulkan`.
- **Serialized inference:** kick off two analyses in parallel; observe in logs that the second one blocks on `_inferenceLock.WaitAsync` until the first finishes its current stream.

---

## 10.5. Result cache (F2)

### What it does
Short-circuits a repeat run when `{presetKey, modelName, sha256-of-files}` has been seen within the last `Analysis:ResultCacheTtlHours` (default 168h = 7 days). The cached result is replayed instantly: a `status: "Cache hit — reusing result from HH:MM (N findings)"` event, then a re-emission of all findings, then `completed` with `duration=0`. Free-text mode never caches.

### How to test
- **Cache miss → fill:** run `find-bugs` on a file; let it complete; note the `analysisId`.
- **Cache hit:** immediately re-run the same preset on the same file → the run completes in <100ms and the status pane reads "Cache hit — reusing result from HH:MM".
- **File edit invalidates:** modify the file, re-run → no cache hit, full run executes.
- **Preset change invalidates:** swap to `find-dead-code` on the same file → no cache hit.
- **TTL:** set `Analysis:ResultCacheTtlHours: 0` in `appsettings.json` (or rewind clock) → every lookup logs `"Cache entry for {Key} is past TTL"` and the run executes normally.
- Source: [ResultCache.cs](src/CodeIntel.Server/Services/ResultCache.cs), [ContentHasher.cs](src/CodeIntel.Server/Services/ContentHasher.cs).

---

## 11. Cheat sheet of API surface

```
# Probes (outside /api so they don't get scraped with normal traffic)
GET  /healthz                                       # liveness, always 200
GET  /readyz                                        # readiness, 200 iff LLM + SQLite ready

# Workspace
GET  /api/workspace/browse?path=...                 # folder + drive listing for picker
POST /api/workspace/load                            # { path } → workspace
GET  /api/workspace/{id}                            # workspace tree
GET  /api/workspace/{id}/file?path=...              # file content (PathSafety checked)
GET  /api/workspace/{id}/definition?file=&line=&character=  # go-to-definition

# Analysis
GET  /api/analysis/presets                          # 8 preset cards
GET  /api/analysis/status                           # { llmReady, modelName, backendName }
POST /api/analysis/estimate                         # { workspaceId, selectedFilePaths } → estimate (F14)
POST /api/analysis/run                              # → { analysisId } — rate-limited per IP (C2)
GET  /api/analysis/{id}                             # result
GET  /api/analysis/{id}/diff/{previousId}           # findings diff (F1)
GET  /api/analysis/recent?count=20                  # history
POST /api/analysis/{id}/cancel                      # user cancel (also accepts trace ids)

# Ignored findings (D1)
GET    /api/ignored-findings?workspaceId=...        # list per workspace
POST   /api/ignored-findings                        # { workspaceId, finding, note }
DELETE /api/ignored-findings/{signature}?workspaceId=...

# Reports
GET  /api/reports/{id}                              # markdown body
GET  /api/reports/{id}/download                     # MD attachment
POST /api/reports/{id}/save                         # writes to repo (PathSafety + SecretScrubber)

# Trace
POST /api/trace/candidates                          # entry-point overload picker (A12)
POST /api/trace/run                                 # → { traceId } — rate-limited per IP (C2)
GET  /api/trace/{id}                                # result
GET  /api/trace/recent?count=20
POST /api/trace/{id}/save                           # writes to repo

# SignalR
WS   /hubs/analysis                                 # single event channel
```

---

## 12. Test suite that exists today

```powershell
dotnet test tests\CodeIntel.Server.Tests
```

Covers (xunit):
- [PlSqlObjectParserTests.cs](tests/CodeIntel.Server.Tests/PlSqlObjectParserTests.cs) — DML keyword extraction, comment/string stripping, schema-prefix normalization, stop-words.
- [PlSqlRepoResolverTests.cs](tests/CodeIntel.Server.Tests/PlSqlRepoResolverTests.cs) — filename match + CREATE OR REPLACE DDL fallback.
- [ContextBuilderPlSqlTests.cs](tests/CodeIntel.Server.Tests/ContextBuilderPlSqlTests.cs) — token-budget and dependency-attachment logic.

**Hardening surface untested** (the §E gap from REVIEW-ENHANCEMENTS.md is now wider — every new service deserves coverage):
- `PathSafety` — the foobar/foo trap, escape sequences, UNC paths.
- `SecretScrubber` — each pattern, hit-count accuracy, multiline PEM blocks.
- `ResultCache` — TTL eviction, free-text-skips-cache, hash invalidation on file edit.
- `IgnoredFindingsStore` — signature stability across runs, per-workspace isolation, list/un-ignore round-trip.
- `FindingsAggregator` — single-finding passthrough, multi-occurrence collapse with line-number hint.
- `FindingsComparer` — added/resolved/persisted bucketing.
- `SkillRouter` — each predicate fires; PL/SQL skill gates on `Language.Sql`.
- `AnalysisEstimator` — median calculation, fallback when sample <2.
- `SqliteAnalysisResultStore` / `SqliteTraceResultStore` — round-trip serialize/deserialize, LRU prune kicks in at `MaxPersistedResults`.
- `CodeIntelDb` — schema-on-first-open is idempotent.

**Pre-hardening surface still untested**:
- `TraceWalker` graph construction + NodeKind inference + cycle handling
- `InvestigationOrchestrator` agentic loop and cancellation paths
- `FindingStreamParser` malformed/incomplete drop counters
- `ReportWriter` index round-trip
- `RoslynWorkspaceService` MSBuild loading and definition lookups
- Any frontend component

Manual smoke-test checklist before declaring a release:
1. Load `CodeIntel.sln`, run `find-bugs` on [Services/ContextBuilder.cs](src/CodeIntel.Server/Services/ContextBuilder.cs), get findings, save to repo, verify INDEX updated.
2. Re-run the same analysis → expect cache hit (status: "Cache hit ... reusing result").
3. Run trace `ReportWriter.WriteTraceAsync` callees depth=1, expect ~8 nodes with synopses.
4. Cancel an in-flight analysis, verify partial save.
5. Ignore one finding from step 1, re-run after editing the file → verify the ignored finding is suppressed in the new result.
6. Fire 6 analyses in quick succession from one terminal → expect a 429 on the 6th.
7. Load a PL/SQL folder, run each of the 4 SQL presets, verify the Copilot Next Step is preset-specific.
8. Hit `/healthz` (200 always) and `/readyz` (200 if LLM loaded + SQLite responding).

---

## 13. Health / readiness probes (F11 partial)

### What it does
- `GET /healthz` → `200 { status: "ok" }` always (process is alive).
- `GET /readyz` → `200 { status:"ready", llm:{...}, db:{...} }` iff LLM loaded AND SQLite responds; `503 { status:"not-ready", ... }` otherwise.

Both live outside `/api` so OpenShift probes don't show up in normal request logs.

### How to test
- Hit `/healthz` immediately on startup → 200 (even before the model finishes loading).
- Hit `/readyz` during the 30–60s LLM warmup → 503 (llm.ready=false); after model loads → 200.
- Stop the SQLite file mid-flight (e.g. lock it externally) → `/readyz` → 503.
- Source: [HealthController.cs](src/CodeIntel.Server/Controllers/HealthController.cs).

---

## 14. Operational tunables

All in [appsettings.json](src/CodeIntel.Server/appsettings.json) under `Analysis:` (and `Data:` / `Llm:` for the others).

| Setting | Default | Purpose |
|---|---|---|
| `Analysis:MaxAgenticIterations` | 3 | Cap on agentic-loop passes |
| `Analysis:IdleTokenTimeoutSeconds` | 90 | Idle watchdog |
| `Analysis:OverallTimeoutSeconds` | 600 | Hard ceiling per run |
| `Analysis:MaxPersistedResults` | 200 | LRU cap on the analyses table |
| `Analysis:MaxLoadedWorkspaces` | 3 | LRU cap on loaded MSBuildWorkspaces |
| `Analysis:EnableResultCache` | true | F2 cache toggle |
| `Analysis:ResultCacheTtlHours` | 168 | F2 cache TTL (7 days) |
| `Analysis:RateLimitRunsPerMinute` | 5 | Per-IP fixed-window limit on /run endpoints |
| `Analysis:ReportOutputPath` | docs/codeintel | Where Save-to-repo writes |
| `Analysis:MaxContextTokens` | 5000 | Token budget per initial context build |
| `Data:DatabasePath` | data/codeintel.db | SQLite path (relative to ContentRoot) |
| `Llm:Backend` | auto | cpu / vulkan / auto |
| `Llm:ModelSha256` | (hardcoded) | Integrity check; empty = warn-only (C3 open) |
