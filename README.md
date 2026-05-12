# CodeIntel

Internal code intelligence tool. Web app that analyzes C# code with a local LLM.

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

1. Load a `.sln` file from the left panel
2. Check the files you want to analyze
3. Pick a preset (find dead code / bugs / business rules / summarize) or write a free-text question
4. Click Run — tokens stream live, findings appear as cards
5. Export the MD report for handoff to Claude Opus (Jira tickets, fix plans, etc.)

## Documentation

See [`CLAUDE.md`](./CLAUDE.md) for architecture, design decisions, conventions, known risks, and pickup priorities. That file is the source of truth — it loads automatically when you run `claude` in this directory.

## Stack

.NET 10 • React 19 • MUI v9 • LLamaSharp • Roslyn • SignalR • Vite
