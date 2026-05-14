# Feature Inventory

Quick-reference list of everything CodeIntel can do. Useful as a presentation cheat-sheet
or when answering "wait, can it do X?" questions live.

## 🔥 The wow factors

| # | Feature | Where | Why it lands |
|---|---|---|---|
| 1 | **Findings overlay on trace** — bug rings auto-decorate Mermaid nodes; finding chips on node cards | `TraceResultsView` after running an Analysis on the same workspace | Genuinely novel — turns the call graph into a bug heatmap |
| 2 | **"Trace from `symbol`"** — click any word in any file → button → jumps to Trace mode pre-populated | `FilePreviewPanel` selection toolbar | One-click from "what is this method" to its full call graph |
| 3 | **Pin to analysis** — select line range → "Pin to analysis" → snippet becomes a chip on a free-text question | `FilePreviewPanel` → `PromptSelector` | Ask focused questions about exact code ranges |
| 4 | **Ctrl+click go-to-definition** in any preview file (cross-file via Roslyn) | `FilePreviewPanel` | LSP-quality navigation without an LSP |
| 5 | **Output / Code toggle on results** — flip streamed text to inline-annotated source | `ResultsView` header tab toggle | Findings appear right next to the offending code |
| 6 | **Live LLM scan beam** + blinking cursor while streaming | Top of `ResultsView` while running | Visible heartbeat that the model is alive |

## ⭐ Solid polish

| Feature | Where |
|---|---|
| Save → **copy "#file: reference"** for direct Copilot Chat paste | Saved banner |
| **Folder/project checkbox** with indeterminate state — pick whole projects | `FileTree` |
| **Metrics summary cards** — High complexity / Long methods / Empty catches / Sync-over-async / Cursor totals (PL/SQL) / Swallowed WHEN OTHERS (PL/SQL) | `MetricsPanel` |
| **Sortable + filterable metrics table** — color-coded CC (red ≥10, yellow ≥6) | `MetricsPanel` |
| **Cold-start explainer panel** during first-token wait — "Reading ~N tokens... typically 30-60s" | `ResultsView` |
| **Mermaid fullscreen + SVG/PNG export** | `MermaidDiagram` |
| **Stale-selection warning banner** — fires if user changes selection after a run | `ResultsView` |
| **Idle warning chip** — "no output for 30s" when streaming stalls | `ResultsView`, `TraceResultsView` |
| **Overload candidate picker dialog** — modal when method name has multiple matches | `TracePanel` |
| **Degraded-run alert** — incomplete/malformed findings flagged, run not cached | `ResultsView` |
| **VS Code deep-link** on every finding/node/method-row — opens at exact line | `openInVsCode` util |
| **Free-text mode** with pinned snippet chip | `PromptSelector` |
| **Run estimator chip** — token + seconds projection, dashed border when sample size is low | `PromptSelector` |
| **Cancellation** — distinct cancel button + cancelling state + cancel-reason chip (user/idle/timeout) | All three views |
| **Word wrap toggle** + **reveal in tree** + **add/remove from analysis** in breadcrumb | File preview breadcrumb |
| **Auto-scroll file tree to active preview** | `FileTree` |
| **Toast on copy** | Save banner |
| **Findings confidence dimming** — low-confidence cards get reduced opacity + dashed left bar + tooltip | Finding cards |
| **Confidence count split** — "N high · M low" header | Findings sidebar |

## 📋 Core flow (already in the demo)

- Load `.sln`, `tsconfig.json`, or any project folder
- File tree with checkboxes, folder picker dialog
- 8 presets filtered by language (4 C# + 4 PL/SQL)
- Live token streaming via SignalR
- Findings cards in sidebar with severity icons + line numbers
- Save-to-repo with auto-generated INDEX.md + JSON sidecar
- Trace mode: callers / callees / both, depth 1–5
- Mermaid diagram with NodeKind shapes (cylinder=DB, hexagon=HTTP)
- Per-node LLM synopses
- Metrics tab with cyclomatic complexity, nesting, length, params, etc.

## 🛡 Hardening (mention if asked, not in demo)

- Rate limiting (5/min per IP) on `/run` endpoints
- Result cache keyed on SHA-256 of file contents (7-day TTL)
- Workspace LRU cap (3 loaded solutions)
- Idle watchdog (90s) + hard ceiling (600s) + linked CTS triad
- Secret scrubbing (AWS, GitHub PAT, JWT, PEM) before MD write
- PathSafety containment check
- Per-workspace ignored-findings store (SHA-256 signature → SQLite)
- Findings diff (added/resolved/persisted between two runs)
- Findings aggregator — collapses near-duplicates across iterations
- Skill router — content-keyed prompt addenda (concurrency / raw-sql / http-client / auth / plsql-cursor)
- `/healthz` + `/readyz` for OpenShift probes
- ANTLR grammar for PL/SQL (replaces regex)
- TypeScript LSP backend via `typescript-language-server`
