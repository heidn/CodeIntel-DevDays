# CodeIntel — Flaws, Risks, and Suggested Enhancements

A review of concrete issues in the current code, plus feature suggestions beyond the deferred list in [CLAUDE.md](CLAUDE.md). Severity rated **🔴 fix soon**, **🟠 fix before prod**, **🟡 worth addressing**, **🟢 polish**. Items that have shipped are marked **✅ Done — …** at the top of the section with a pointer to the implementing file. The rest remain open.

The goal is to make the existing architecture more robust before scaling. None of these block the dev-days demo.

---

## Status snapshot (as of the hardening pass)

| Severity | Open | Closed |
|---|---|---|
| 🔴 fix soon | C1 | A1 |
| 🟠 fix before prod | A2, C3 | A3, A4, C2, C4, D1, D2 |
| 🟡 worth addressing | B1, B2 superseded?, B5, D4, D6, E (most) | A5, A6, A7, A8, A9, B2 (ANTLR), B3, B4, D3, D5 |
| 🟢 polish | A10 | A11, A12 |
| Features (§F) | F3, F4, F5, F6, F7, F9, F12, F13, F15 | F1, F2, F8, F10, F11 (`/healthz`/`/readyz`), F14 |

If you only read one section: **C1 is now the single hardest blocker for OpenShift** — everything else from §A and §C has either landed or has a mitigation (rate-limit for C2, scrubber for C4, PathSafety for A1, LRU caps for A3/A4).

---

## A. Bugs / correctness

### A1. ~~🔴 Path-escape guard is exploitable~~ ✅ Done — [PathSafety.cs](src/CodeIntel.Server/Services/PathSafety.cs)

Replaced the prefix-match with `Path.GetFullPath` + separator-aware containment. Used by [ReportWriter.WriteAsync](src/CodeIntel.Server/Services/ReportWriter.cs#L58), [ReportWriter.WriteTraceAsync](src/CodeIntel.Server/Services/ReportWriter.cs#L108), and `RoslynWorkspaceService.ReadFileAsync`. The historical description follows for reference.
[ReportWriter.cs:55-61](src/CodeIntel.Server/Services/ReportWriter.cs#L55-L61), [ReportWriter.cs:102-108](src/CodeIntel.Server/Services/ReportWriter.cs#L102-L108), and [RoslynWorkspaceService.cs:259-262](src/CodeIntel.Server/Services/RoslynWorkspaceService.cs#L259-L262) use the prefix-check pattern:

```csharp
if (!resolvedOut.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
```

If `repoRoot = C:\projects\foo`, then `C:\projects\foobar\evil.md` passes the check because `C:\projects\foobar` *starts with* `C:\projects\foo`. The classic mitigation is to append `Path.DirectorySeparatorChar` to `resolvedRoot` before comparing, or use `Path.GetRelativePath` and reject results containing `..`.

**Fix:**
```csharp
var rootWithSep = resolvedRoot.EndsWith(Path.DirectorySeparatorChar)
    ? resolvedRoot : resolvedRoot + Path.DirectorySeparatorChar;
if (!resolvedOut.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
    && !resolvedOut.Equals(resolvedRoot, StringComparison.OrdinalIgnoreCase))
    return null;
```

### A2. 🟠 Workspace browse leaks the host filesystem (and drives) to any caller
[WorkspaceController.cs:58-119](src/CodeIntel.Server/Controllers/WorkspaceController.cs#L58-L119) has no auth and returns `DriveInfo.GetDrives()` plus every subdirectory at any path the caller supplies. On localhost that's fine; on the OpenShift internal URL, *every developer on the team* can browse the host pod's `/`, `/etc`, `/proc`, and any mounted volumes — including the model path. Combine with [CLAUDE.md](CLAUDE.md)'s "No auth for MVP" and this is the single most important thing to gate before deployment.

**Fix:** at minimum, constrain `Browse` to a configured allowlist of root prefixes (e.g. `/workspaces`, the user's home). Hide drives in production.

### A3. ~~🟠 In-memory result stores grow without bound~~ ✅ Done — [SqliteAnalysisResultStore](src/CodeIntel.Server/Services/AnalysisResultStore.cs), [SqliteTraceResultStore](src/CodeIntel.Server/Services/TraceResultStore.cs)

Both stores now persist into SQLite (`data/codeintel.db`, WAL). Each `Save()` issues a `PruneAsync` that trims to `Analysis:MaxPersistedResults` (default 200, LRU by `started_at`). Pairs with D2.

### A4. ~~🟠 Workspaces never evicted; MSBuildWorkspace stays in memory forever~~ ✅ Done — [RoslynWorkspaceService.TouchLru](src/CodeIntel.Server/Services/RoslynWorkspaceService.cs#L46)

`MaxLoadedWorkspaces` (default 3) caps the dictionary; eviction disposes the `MSBuildWorkspace` to release the compilation. No `DELETE /api/workspace/{id}` endpoint yet but the LRU keeps the working set bounded.

### A5. ~~🟡 Vulkan probe loads the GGUF twice on startup~~ ✅ Done — [LlamaSharpService.ResolveBackend](src/CodeIntel.Server/Services/LlamaSharpService.cs#L120)

`ResolveBackend` now returns `(parameters, backendName, probedWeights)` — the probe-loaded weights are handed back to `InitializeAsync` and reused as `_weights`, eliminating the 4.7 GB second read.

### A6. ~~🟡 `LlamaSharpService.Dispose` doesn't dispose `_executor`~~ ✅ Done — [LlamaSharpService.Dispose](src/CodeIntel.Server/Services/LlamaSharpService.cs#L189)

`Dispose` now flips `_isReady` first (so racing `StreamAsync` calls fail fast), clears `_executor` (which borrows from `_weights` and holds no native handles of its own), then disposes `_weights` and `_inferenceLock`. Idempotent via `_disposed`.

### A7. ~~🟡 `ExtractMethodSnippet` doesn't find method end~~ ✅ Done — [ContextRequestHandler.FulfillMethodAsync](src/CodeIntel.Server/Services/ContextRequestHandler.cs#L125)

The 80-line line-counted snippet was replaced by `declRef.GetSyntaxAsync(ct).ToFullString()`. Roslyn already knows the method end, so the snippet is now exactly the declaration — no padding from neighbouring methods, no mid-body truncation.

### A8. ~~🟡 `FindingStreamParser` re-scans the whole buffer on every chunk~~ ✅ Done — [FindingStreamParser](src/CodeIntel.Server/Services/FindingStreamParser.cs)

Rewritten as a single-pass scanner that only inspects the newly-appended chunk plus a small tail window for partial-tag detection. O(n) over the stream. `IncompleteFindingCount` is now an integer, not a re-scan.

### A9. ~~🟡 Trace `FindCallersAsync` re-walks the solution per node~~ ✅ Done — [TraceWalker.CallersCache](src/CodeIntel.Server/Services/TraceWalker.cs#L59)

Per-run memo keyed on FQN; cleared at the start of each `BuildGraphAsync` call. The same method visited from multiple branches now pays the symbol-finder cost exactly once.

### A10. 🟢 `EnsureFolderReadme` writes synchronously inside an async method
Still open. [ReportWriter.cs:232-251](src/CodeIntel.Server/Services/ReportWriter.cs#L232-L251) uses `File.WriteAllText`. Switch to `WriteAllTextAsync` for consistency.

### A11. ~~🟢 Trace cycle edges still rendered as forward arrows~~ ✅ Done — [TraceEdge.IsBackEdge](src/CodeIntel.Server/Models/TraceModels.cs#L70)

BFS now flags an edge as `isBackEdge` when its target was already in `visited`. `TraceWalker.BuildMermaid` renders flagged edges as `-.->` dashed arrows.

### A12. ~~🟢 Trace entry-point disambiguation is first-match-wins~~ ✅ Done — [TraceController.Candidates](src/CodeIntel.Server/Controllers/TraceController.cs#L41), [TraceWalker.ResolveCandidatesAsync](src/CodeIntel.Server/Services/TraceWalker.cs#L240)

`POST /api/trace/candidates` returns every matching `IMethodSymbol` (with file/line/signature) so the UI can present a picker. `TraceRequest.PreferredFqn` round-trips the chosen FQN back to `/run`, and `ResolveEntryPointAsync` honors the exact match.

---

## B. Architectural debt

### B1. 🟠 No abstraction over Roslyn — TS/Java are second-class
[ContextRequestHandler](src/CodeIntel.Server/Services/ContextRequestHandler.cs) and [TraceWalker](src/CodeIntel.Server/Services/TraceWalker.cs) call Roslyn directly. The promised LSP+tree-sitter rewrite ([CLAUDE.md](CLAUDE.md) pickup #2) is the single biggest compounding lever — every feature would automatically gain TS/Python/Go support, and trace mode (currently C#-only) would unlock for TS. This is already on the roadmap; flagging it here as the highest-leverage architectural change.

### B2. ~~🟡 PL/SQL parser is regex-only; no real grammar~~ ✅ Done — [PlSqlObjectParser](src/CodeIntel.Server/Services/PlSqlObjectParser.cs), [Grammar/PlSqlRefs.g4](src/CodeIntel.Server/Grammar/PlSqlRefs.g4)

Rewritten on top of an ANTLR grammar (codegen via the `Antlr4BuildTasks` NuGet at build time). The grammar is intentionally narrow — it parses just enough PL/SQL to extract object references — so the visitor stays small and codegen is fast. Quoted identifiers, multi-line statements, and comments/strings now behave correctly. Dynamic SQL inside string literals is still out of scope (still acknowledged limitation).

### B3. ~~🟡 Two orchestrators in source, one in DI~~ ✅ Done — file deleted

`AnalysisOrchestrator.cs` is gone. `InvestigationOrchestrator` is the sole implementation.

### B4. ~~🟡 Workspace `ProjectPath` does double duty~~ ✅ Done — [Workspace](src/CodeIntel.Server/Models/WorkspaceModels.cs)

`Workspace.RootFolder` (always a dir) + `Workspace.EntryFile` (nullable) split the file-vs-folder ambiguity. `ProjectPath` is preserved for round-tripping and display; new consumers use `RootFolder`. `ContentHasher.WorkspaceRoot` reads `RootFolder` first.

### B5. 🟡 CORS origins hard-coded to localhost ports
[Program.cs:54](src/CodeIntel.Server/Program.cs#L54) lists `5173` and `5174` only. Make this `Cors:AllowedOrigins` in `appsettings.json` so it doesn't break on prod.

### B6. 🟢 Frontend uses `crypto.randomUUID()` for analysisId
[TracePanel.tsx:48](src/CodeIntel.Server/ClientApp/src/components/TracePanel.tsx#L48) generates the trace ID client-side. This is fine but defeats server-side correlation — the server has to trust client IDs. Either let the server return the ID and have the client join the group after, or accept the client's ID as a hint and let the server collision-check.

---

## C. Security gaps

### C1. 🔴 No authentication, anywhere
Already called out in [CLAUDE.md](CLAUDE.md) but flagging here because of compounding factors:
- `Browse` exposes filesystem (item A2).
- `WorkspaceController.Get` returns any workspace by guessing the 12-char ID prefix.
- The SignalR group is just the analysis GUID — anyone who guesses the GUID can subscribe to another user's stream.

For OpenShift deployment, the bare minimum is IIS-passthrough Windows Auth or a reverse-proxy SSO layer. Otherwise the tool is a filesystem-browsing endpoint on the internal network.

### C2. ~~🟠 Rate-limiting absent on `/api/analysis/run`~~ ✅ Done — [Program.cs](src/CodeIntel.Server/Program.cs#L70)

`Microsoft.AspNetCore.RateLimiting` fixed-window per IP, `Analysis:RateLimitRunsPerMinute` (default 5/min). Applied to `POST /api/analysis/run` and `POST /api/trace/run` via `[EnableRateLimiting("analysis-run")]`. Rejected requests return a structured 429 body. Caveat: per-IP is coarse on shared NAT/proxy infra; revisit once auth lands.

### C3. 🟠 SHA-256 model integrity check is best-effort
Still open. [LlamaSharpService](src/CodeIntel.Server/Services/LlamaSharpService.cs) skips verification when `Llm:ModelSha256` is empty and only logs a warning. Make the hash mandatory unless `ASPNETCORE_ENVIRONMENT=Development`.

### C4. ~~🟡 Reports written into the repo include `RawLlmOutput`~~ ✅ Done — [SecretScrubber](src/CodeIntel.Server/Services/SecretScrubber.cs)

`SecretScrubber.Scrub` runs in [ReportWriter.WriteAsync](src/CodeIntel.Server/Services/ReportWriter.cs#L72) (and the trace path) right before `File.WriteAllTextAsync`. Patterns cover AWS access keys, AWS secret keys, generic `key=value` / `password=value`, GitHub PATs, Slack tokens, bearer headers, PEM private keys, and JWTs. Per-pattern hit counts flow back through `ReportWriteResult.Redactions` so the UI can warn.

---

## D. UX gaps

### D1. ~~🟠 No "ignore this finding" / "known FP" workflow~~ ✅ Done — [IgnoredFindingsStore](src/CodeIntel.Server/Services/IgnoredFindingsStore.cs), [IgnoredFindingsController](src/CodeIntel.Server/Controllers/IgnoredFindingsController.cs)

SHA-256 signature on `(severity, file, lowercased title)` (same shape as `FindingsComparer` so diff + ignore are aligned). `POST /api/ignored-findings`, `GET ?workspaceId=...`, `DELETE /{signature}`. Persisted per workspace root in SQLite. UI surfaces an ignore button on every finding card.

### D2. ~~🟠 Restart loses every analysis~~ ✅ Done — [CodeIntelDb](src/CodeIntel.Server/Data/CodeIntelDb.cs)

`Microsoft.Data.Sqlite`, WAL mode, schema created on first open. Tables: `analyses`, `traces`, `ignored_findings`, `result_cache`. Path is `Data:DatabasePath` (default `data/codeintel.db`, resolved relative to `ContentRootPath`).

### D3. ~~🟡 No per-finding "open in editor"~~ ✅ Done — [openInVsCode.ts](src/CodeIntel.Server/ClientApp/src/utils/openInVsCode.ts)

`window.location.href = vscode://file/{path}[:line[:column]]`. Wired into finding cards in `ResultsView` and trace node cards in `TraceResultsView`. First click per origin shows the browser's permission dialog.

### D4. 🟡 No live elapsed/idle indicator parity between analysis & trace
Still partially open. Trace results pane now has elapsed/synopsis chips; the idle-warn chip parity hasn't been verified end-to-end. Quick check: lower `Analysis:IdleTokenTimeoutSeconds` to 10 and stall a trace mid-synopsis — confirm the chip lights.

### D5. ~~🟡 Mermaid can blow out for large traces~~ ✅ Done — [TraceWalker.MaxTotalNodes](src/CodeIntel.Server/Services/TraceWalker.cs#L36)

Hard ceiling at 100 total nodes. Already-discovered nodes are kept; further expansion stops and `truncated=true` is set. "Show full graph" download escape hatch still TODO if it ever bites — so far the cap has been sufficient.

### D6. 🟢 Status text is opaque to non-Roslyn users
"Building context..." then "Investigating (pass 2/3)..." — fine for technical users but the message could surface: "fetching 3 referenced classes via Roslyn" so the user understands the model is making decisions.

---

## E. Test coverage gaps

The existing xunit tests cover only the PL/SQL surface. Untested:

1. **`FindingStreamParser`** — malformed JSON handling, `IsDone` detection, incomplete-tag counting, multi-block streams.
2. **`TraceWalker`** — cycle handling, fan-out cap, NodeKind classification edge cases (typed HttpClient wrapper, custom DbContext base), entry-point resolution.
3. **`ReportWriter`** — INDEX merge logic, path-escape guard (after fixing A1, write a test for the foobar / foo case), trace + analysis interleaved.
4. **`InvestigationOrchestrator`** — cancellation reason discrimination (`user` vs `idle` vs `timeout`), partial save on cancel.
5. **`ContextBuilder`** — token-budget exhaustion at file N (already has PL/SQL coverage; needs the non-SQL path).
6. **End-to-end SignalR** — `TestServer` + a mock `ILlmService` that emits a canned token stream, assert the event sequence.

A clean target: 70% line coverage on `Services/` before the OpenShift deploy.

---

## F. Suggested new features beyond the existing roadmap

[CLAUDE.md](CLAUDE.md) already lists the LSP/tree-sitter rewrite, live Oracle introspection, dead-code, business-docs mode, skills system, OpenShift deployment, auth, and persistence. The following are *additional* ideas worth considering:

### F1. ~~Findings diff between runs~~ ✅ Done — [FindingsDiff.cs](src/CodeIntel.Server/Services/FindingsDiff.cs), [AnalysisController.Diff](src/CodeIntel.Server/Controllers/AnalysisController.cs#L99)

`GET /api/analysis/{id}/diff/{previousId}` → `{added, resolved, persisted}`. Signature: `(severity, file, lowercased title)` — same shape as `IgnoredFindingsStore.SignatureFor` so the ignore-list and diff line up.

### F2. ~~File-hash result cache~~ ✅ Done — [ContentHasher](src/CodeIntel.Server/Services/ContentHasher.cs), [ResultCache](src/CodeIntel.Server/Services/ResultCache.cs)

Hash key is `{presetKey}|{modelName}|{sha256-of-files}`; lookup happens at the very top of `InvestigationOrchestrator.RunAsync`, before any prompt construction. Free-text mode never caches. TTL configurable via `Analysis:ResultCacheTtlHours` (default 168h). Cache hits replay findings instantly and emit the same `completed` event with `duration=0`.

### F3. Embedding-based "relevant files" selector
A small local embedding model (~80MB, e.g. `bge-small-en-v1.5`) can answer "given this question or finding, which 5 files in the repo are most relevant?" in <1s. This replaces the manual checkbox tree for free-text mode and dramatically improves the agentic loop's `search_code` fulfillment quality.

### F4. Two-model "second opinion" mode
Run the same prompt through Qwen-7B *and* a second model (Devstral, Phi-4) in parallel, then surface only findings emitted by both. Cuts FPs at the cost of 2× inference. Already plausible given the singleton service — would need a second LLM instance behind a `[FromKeyedServices]` registration.

### F5. Headless CLI
`codeintel run --preset find-bugs --workspace ./src --files X,Y --output report.md` — runs the same pipeline without the UI. Unlocks:
- CI integration ("nightly find-bugs on `main`, post diff to Slack").
- Pre-commit hook ("scan only staged files").
- Bulk-analyze: loop over 100 files, dump 100 reports.

### F6. VS Code extension
Even minimal — right-click a method → "CodeIntel trace" or "CodeIntel find-bugs on selection" → opens the saved report in a side panel and offers to drop a Copilot Chat reference. The team already lives in VS Code (Copilot subscription), so meet them there.

### F7. Watch mode
"Re-run `find-bugs` on save for the currently-open file" via SignalR push. Keep a 5-second debounce. Useful for hot-spot debugging — see new findings appear as you type.

### F8. ~~Findings aggregation~~ ✅ Done — [FindingsAggregator.cs](src/CodeIntel.Server/Services/FindingsAggregator.cs)

Post-loop collapse pass keyed on `(severity, file, lowercased title)`. The 7B model often re-states the same logical finding on different lines across iterations; the aggregator keeps the first occurrence and appends `_Also reported at line(s) 42, 87 (3 occurrences collapsed)._` to the description. Surfaced as an info log when it fires.

### F9. Audit log + per-user history
Still open. The SQLite schema is in place (`analyses.workspace_id`, `started_at`) but there's no `user_id` column until auth lands. Add a `users` table once C1 is in.

### F10. ~~Skills routed by file content, not just preset~~ ✅ Done — [SkillRouter](src/CodeIntel.Server/Services/SkillRouter.cs)

Five content-keyed predicates today: `concurrency`, `raw-sql`, `http-client`, `auth`, `plsql-cursor`. Each contributes a prompt addendum biasing the model toward the relevant issue class. Active skills are emitted as a `status` event so users see what specialization is being applied. Next step: move the hard-coded addenda into `Prompts/skills/*.md` files routed by the same predicates.

### F11. ~~Health + metrics endpoints~~ ✅ Partially done — [HealthController](src/CodeIntel.Server/Controllers/HealthController.cs)

`GET /healthz` (always 200) and `GET /readyz` (LLM + SQLite, returns 503 if either fails) live outside `/api` so probes don't get scraped with normal traffic. Prometheus `/metrics` still TODO.

### F12. Slack / Teams webhook on completion
`Analysis:Notifications:SlackWebhookUrl` in `appsettings.json` — on `completed` with `FindingCount > 0`, post a one-liner with the saved-report URL. Useful for team-wide visibility once the tool is internally deployed.

### F13. Shared prompt library
Today, custom prompts are typed into the free-text box and lost. A `prompts/team/` folder in the repo (or a small admin endpoint) that lets users save and share named custom prompts across the team would turn `find-bugs` into a starting point, not an endpoint.

### F14. ~~Cost / time estimator~~ ✅ Done — [AnalysisEstimator](src/CodeIntel.Server/Services/AnalysisEstimator.cs), [AnalysisController.Estimate](src/CodeIntel.Server/Controllers/AnalysisController.cs#L37)

`POST /api/analysis/estimate` returns `{ estimatedTokens, estimatedSeconds, sampleSize, explanation }`. Uses the median seconds-per-token across recent completed runs (median, not mean, because one timed-out 600s run would otherwise wreck the projection). Falls back to a coarse `~18ms/token` constant when there's <2 runs of history.

### F15. "Explain this commit" mode
Seed the LLM with `git show {sha}` as the focused content. Findings + summary scoped to the commit. Combines well with F5 (CI hook for PR diffs).

---

## G. Quick wins remaining

The hardening pass closed most of the original §G list. What's still left, ranked by cost-to-impact:

1. Make `Llm:ModelSha256` mandatory unless `ASPNETCORE_ENVIRONMENT=Development` (C3) — 10 minutes.
2. Configurable CORS origins (B5) — 10 minutes.
3. `WorkspaceController.Browse` root-allowlist (A2) — 30 minutes; a stopgap until C1 lands.
4. Switch `ReportWriter.EnsureFolderReadme` to async (A10) — 5 minutes.
5. `DELETE /api/workspace/{id}` endpoint to complement the workspace LRU (A4 follow-up) — 15 minutes.

---

## Summary — what's most important

If I had to pick 3 items before this tool leaves a single dev's laptop:
1. **C1 + A2** — auth in front of the whole API. Still the single hardest blocker; `WorkspaceController.Browse` still leaks the host filesystem to unauthenticated callers.
2. **C3** — mandatory model integrity check in production, since `LlamaSharpService` still degrades to a warning.
3. **Test coverage on the hardening surface (§E)** — `ResultCache`, `IgnoredFindingsStore`, `PathSafety`, `SecretScrubber`, `FindingsAggregator`, `FindingsComparer`, `SkillRouter` are all untested. Without coverage, the next refactor regresses silently.

If I had to pick 3 feature directions that compound:
1. **B1** — LSP/tree-sitter rewrite (every feature gets multi-language for free).
2. **F5 + F6** — headless CLI + VS Code extension. SQLite persistence, the estimator, and the diff endpoint all just landed and are now reusable from a CLI or extension. Closes the gap to where the team actually works.
3. **F3** — embedding-based "relevant files" selector. The agentic loop already supports `search_code` context requests; swap regex for embedding similarity and the loop quietly improves.
