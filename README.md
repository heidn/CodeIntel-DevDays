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

## How It Works

1. Load a workspace from the left panel — `.sln` for C#, or any folder of `.sql/.pkg/.pkb` files for Oracle PL/SQL
2. Check the files you want to analyze
3. Pick a preset (language-aware — C# workspaces see find-bugs / find-dead-code / find-business-rules / summarize; PL/SQL workspaces see find-bugs-sql / find-business-rules-sql / cleanup-stored-proc / efficiency-review), or write a free-text question
4. Click **Run Analysis** — tokens stream live, findings appear as cards. PL/SQL seeds auto-attach referenced table/proc/package definitions to the context.
5. Or click **Trace** (top toggle, C# only today) to render a call-graph from any entry-point method
6. Click **Save to repo** → writes a markdown report into `docs/codeintel/` of the loaded repo, complete with a preset-aware "Copilot Next Step" prompt. Reference it in Copilot Chat via `#file:` syntax.

## Tests

```powershell
dotnet test tests\CodeIntel.Server.Tests
```

PL/SQL fixtures for manual UI smoke-testing live in [`test-data/sql/`](./test-data/sql/) — see the [README in that folder](./test-data/sql/README.md) for a preset-by-preset cheat sheet.

## Documentation

See [`CLAUDE.md`](./CLAUDE.md) for architecture, design decisions, conventions, known risks, and pickup priorities. That file is the source of truth — it loads automatically when you run `claude` in this directory.

## Stack

.NET 10 • React 19 • MUI v9 • LLamaSharp • Roslyn • SignalR • Vite • xUnit
