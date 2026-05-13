# CodeIntel

Internal code intelligence tool. Web app that analyzes C#, TypeScript, Java, and Oracle PL/SQL with a local LLM, with handoff to GitHub Copilot via committed MD reports.

## Quick Start

```powershell
# 1. Install .NET 10 SDK + Node.js 20+
# 2. Download model to ./models/
#    https://huggingface.co/bartowski/Qwen2.5-Coder-7B-Instruct-GGUF
#    Grab: qwen2.5-coder-7b-instruct-q4_k_m.gguf (~4.7 GB)
# 3. Run
dotnet restore
cd src\CodeIntel.Server\ClientApp && npm install && cd ..
dotnet run

# Open http://localhost:5000
```

Probes: `GET /healthz` (liveness, always 200) and `GET /readyz` (LLM + SQLite ready) live outside `/api` for OpenShift readiness gating.

## How It Works

1. Load a workspace from the left panel — `.sln` for C#, or any folder of `.sql/.pkg/.pkb` files for Oracle PL/SQL
2. Check the files you want to analyze. Before running, the UI shows an estimate ("~12,400 tokens, est. 2m 30s") based on the median of recent runs.
3. Pick a preset (language-aware — C# workspaces see find-bugs / find-dead-code / find-business-rules / summarize; PL/SQL workspaces see find-bugs-sql / find-business-rules-sql / cleanup-stored-proc / efficiency-review), or write a free-text question
4. Click **Run Analysis** — tokens stream live, findings appear as cards. PL/SQL seeds auto-attach referenced table/proc/package definitions to the context. A content-keyed skill router biases the prompt when concurrency/SQL/HTTP/auth/PL-cursor patterns are present.
5. Or click **Trace** (top toggle, C# only today) to render a call-graph from any entry-point method. Overloads get a candidate picker; cycles render as dashed back-edges in the Mermaid graph.
6. Click **Save to repo** → writes a markdown report into `docs/codeintel/` of the loaded repo, complete with a preset-aware "Copilot Next Step" prompt. Reference it in Copilot Chat via `#file:` syntax. A secret scrubber redacts AWS keys / GitHub PATs / JWTs / PEM blocks / bearer tokens before the file hits disk.

Reruns of the same preset against unchanged file content are served instantly from a SQLite-backed result cache (`{presetKey, modelName, sha256(files)}` key, 7-day default TTL). Findings you mark as false positives are persisted per workspace and silently filtered from later runs. Two analyses can be diffed via `GET /api/analysis/{id}/diff/{previousId}` to surface added/resolved/persisted findings.

## Tests

```powershell
dotnet test tests\CodeIntel.Server.Tests
```

PL/SQL fixtures for manual UI smoke-testing live in [`test-data/sql/`](./test-data/sql/) — see the [README in that folder](./test-data/sql/README.md) for a preset-by-preset cheat sheet.

## Documentation

- [`CLAUDE.md`](./CLAUDE.md) — architecture, design decisions, conventions, known risks, pickup priorities. The source of truth; loads automatically when you run `claude` in this directory.
- [`docs/REVIEW-FEATURES.md`](./docs/REVIEW-FEATURES.md) — reviewer-oriented walkthrough of every shipping feature with how-to-test steps.
- [`docs/REVIEW-ENHANCEMENTS.md`](./docs/REVIEW-ENHANCEMENTS.md) — flaws / risks / suggested enhancements backlog. Items closed during the hardening pass are marked ✅ with file pointers; the open ones (auth, CORS allowlist, more test coverage) drive the next sessions.

## Stack

.NET 10 • React 19 • MUI v9 • LLamaSharp • Roslyn • ANTLR (PL/SQL grammar) • SignalR • SQLite (`Microsoft.Data.Sqlite`, WAL) • Vite • xUnit
