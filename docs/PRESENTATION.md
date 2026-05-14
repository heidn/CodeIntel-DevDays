---
marp: true
theme: default
style: |
  /* ── Base — matches app theme ─────────────────────────────────────────── */
  section {
    background: #f1f5f9;
    color: #0f172a;
    font-family: 'Inter Tight', 'Inter', -apple-system, system-ui, sans-serif;
    font-size: 22px;
    padding: 48px 60px;
  }
  /* ── Typography ────────────────────────────────────────────────────────── */
  h1 {
    color: #4f46e5;
    font-size: 2.2em;
    font-weight: 700;
    letter-spacing: -0.02em;
    margin-bottom: 0.2em;
  }
  h2 {
    color: #4f46e5;
    font-size: 1.5em;
    font-weight: 700;
    border-bottom: 2px solid #e2e8f0;
    padding-bottom: 0.3em;
    margin-bottom: 0.6em;
  }
  h3 { color: #7c3aed; font-size: 1.1em; font-weight: 600; margin-bottom: 0.3em; }
  p { color: #334155; line-height: 1.6; }
  strong { color: #4f46e5; }
  em { color: #7c3aed; font-style: normal; font-weight: 600; }
  /* ── Lists ─────────────────────────────────────────────────────────────── */
  ul { color: #334155; padding-left: 1.4em; }
  ul li { margin-bottom: 0.35em; line-height: 1.5; }
  ul li::marker { color: #4f46e5; }
  /* ── Code ──────────────────────────────────────────────────────────────── */
  code {
    background: #e0e7ff;
    color: #3730a3;
    border-radius: 4px;
    padding: 2px 7px;
    font-family: 'JetBrains Mono', 'Fira Code', Consolas, monospace;
    font-size: 0.82em;
  }
  pre {
    background: #ffffff;
    border: 1px solid #e2e8f0;
    border-left: 4px solid #4f46e5;
    border-radius: 6px;
    padding: 1em 1.2em;
    font-family: 'JetBrains Mono', 'Fira Code', Consolas, monospace;
    font-size: 0.78em;
    line-height: 1.55;
  }
  pre code { background: none; padding: 0; color: #1e293b; }
  /* ── Blockquote / callout ──────────────────────────────────────────────── */
  blockquote {
    background: #ffffff;
    border-left: 4px solid #4f46e5;
    border-radius: 0 8px 8px 0;
    padding: 0.8em 1.2em;
    margin: 0.8em 0;
    color: #1e293b;
  }
  blockquote p { color: #1e293b; margin: 0; }
  /* ── Tables ────────────────────────────────────────────────────────────── */
  table { border-collapse: collapse; width: 100%; font-size: 0.82em; }
  th {
    background: #4f46e5;
    color: #ffffff;
    padding: 8px 12px;
    font-weight: 600;
    text-align: left;
  }
  td { padding: 7px 12px; border-bottom: 1px solid #e2e8f0; color: #334155; }
  tr:nth-child(even) td { background: #f8fafc; }
  /* ── Chip / badge helper ────────────────────────────────────────────────── */
  .chip {
    display: inline-block;
    background: #e0e7ff;
    color: #3730a3;
    border-radius: 999px;
    padding: 2px 10px;
    font-size: 0.75em;
    font-weight: 600;
    margin: 2px;
  }
  .chip-green { background: #dcfce7; color: #15803d; }
  .chip-purple { background: #f3e8ff; color: #7c3aed; }
  /* ── Title slide ───────────────────────────────────────────────────────── */
  section.title {
    background: linear-gradient(135deg, #4f46e5 0%, #7c3aed 100%);
    color: #ffffff;
    display: flex;
    flex-direction: column;
    justify-content: center;
  }
  section.title h1 { color: #ffffff; font-size: 2.8em; }
  section.title h2 { color: rgba(255,255,255,0.85); border: none; font-size: 1.1em; font-weight: 400; }
  section.title p { color: rgba(255,255,255,0.7); }
  /* ── Feature demo slide ─────────────────────────────────────────────────── */
  section.demo {
    background: #ffffff;
    border-top: 6px solid #4f46e5;
  }
  /* ── Divider / section header ─────────────────────────────────────────── */
  section.section-header {
    background: #4f46e5;
    color: #ffffff;
    display: flex;
    flex-direction: column;
    justify-content: center;
    align-items: center;
    text-align: center;
  }
  section.section-header h1 { color: #ffffff; font-size: 2.4em; }
  section.section-header p { color: rgba(255,255,255,0.8); font-size: 1em; }
  /* ── Pagination ─────────────────────────────────────────────────────────── */
  section::after { color: #94a3b8; font-size: 0.7em; }
paginate: true
---

<!-- _class: title -->

# CodeIntel

## Local AI Code Intelligence

*Load any codebase. Get structured findings in minutes. Hand off to Copilot.*

---

## The Problem

Every developer has been here:

- **Unfamiliar codebase** — where does this flow actually go?
- **Buried business logic** — what are the real rules in this 800-line stored proc?
- **Change confidence** — what breaks if I touch this method?
- **Code review** — is there something wrong here that I'm missing?

Existing options are slow (read it all yourself), interruptive (ask a colleague), or context-blind (paste fragments into Copilot without full project context).

---

## The Architecture

> The local model is a **briefing officer**.  
> GitHub Copilot (your team subscription) is the **analyst**.

```
Your Repo (.sln / .sql)
      │
      ▼
 Local LLM  ──► <finding> blocks + Mermaid diagram + complexity table
 (in-process,        │
  no cloud)          │   Markdown report committed into your repo
                     ▼
             #file: → Copilot Chat → Jira tickets / fix plans / PRs
```

The MD report is a **durable, human-readable, AI-consumable** artifact — not a chat transcript.

---

## Three Modes

| Mode | Engine | What you get |
|---|---|---|
| **Analysis** | LLM agentic loop | Findings: bugs, dead code, business rules |
| **Trace** | Roslyn BFS + LLM | Call graph with node synopses + Mermaid |
| **Metrics** | Roslyn / ANTLR static | Complexity table — no inference, instant |

Each mode ends with **Save to Repo** → a preset-aware Copilot prompt you paste into Copilot Chat.

---

<!-- _class: section-header -->

# How an LLM Actually Works

*The 30-second version every developer should know*

---

## LLM Inference — What Happens When You Hit "Run"

An LLM is a **next-token predictor**. It doesn't "understand" code — it predicts what character sequence most likely follows.

**Two phases per call:**

| Phase | What happens | Duration |
|---|---|---|
| **Prompt evaluation** | Model reads your entire context (all input tokens) at once — matrix multiplications across every transformer layer | 30–120s on CPU for ~5K tokens |
| **Token generation** | Model emits one token at a time — each token feeds back as input for the next | ~10–25 tok/s on CPU (7B Q4) |

> **Key insight:** prompt-eval is the slow part. Once tokens start flowing, they're fast. The first token can take 90+ seconds of total silence — that's not a hang, it's the model reading.

---

## Tokens — Not Characters, Not Words

LLMs don't see characters or words — they see **tokens** (subword chunks).

```
"NullReferenceException"  →  ["Null", "Reference", "Exception"]        (3 tokens)
"order.Discount.Code"     →  ["order", ".", "Dis", "count", ".", "Code"]  (6 tokens)
"if (x != null)"          →  ["if", " (", "x", " !=", " null", ")"]      (6 tokens)
```

**Why this matters for CodeIntel:**
- Context budget is measured in tokens (~5,000 default), not lines or characters
- A 300-line C# file ≈ 2,000–3,000 tokens — sometimes one file fills the whole budget
- Files exceeding the budget auto-chunk at **brace boundaries** (C#/TS) or `END;` (PL/SQL)

---

## How Tokens Reach the UI

```
 LLM Engine                Server                    Browser
 ─────────                 ──────                    ───────
 [predict token]  ──►  IAsyncEnumerable<string>
                       foreach token:
                         • Reset idle watchdog timer
                         • Append to FindingStreamParser
                         • SignalR → push "token" event  ──►  analysisStore.append()
                                                              React re-renders
                         • If <finding>...</finding> complete:
                           SignalR → push "finding" event ──► finding card appears
```

Every single token is pushed over **SignalR** (WebSocket) the instant it's generated. The UI renders a scan-beam animation while tokens accumulate. When the parser detects a complete `<finding>{...}</finding>` block, it emits a structured finding card alongside the raw stream.

---

<!-- _class: section-header -->

# Crafting Prompts for a Local LLM

*The hardest engineering problem in this project*

---

## The Prompt Engineering Challenge

A 7B parameter model is **capable but undisciplined**. Without careful prompt design:

- It **hedges** everything — *"this could potentially maybe cause issues"*
- It **hallucinates** bugs that don't exist — flags null checks as missing when `?.` is right there
- It **repeats** itself across iterations — same finding restated 3 different ways
- It **rambles** instead of emitting structured output

The prompt is not a suggestion — it's a **specification** the model must follow. Every sentence exists because a real failure happened without it.

---

## Anatomy of a Good Prompt — find-bugs.md

The `find-bugs` prompt has **7 sections**, each solving a specific 7B failure mode:

```markdown
1. WHAT TO EMIT    — "Emit a <finding> only when you can name BOTH:
                      the exact trigger AND the specific failure mode"

2. WHAT TO LOOK FOR — Concrete bug classes: null deref, off-by-one,
                       race conditions, resource leaks, sync-over-async

3. WHEN YOU FIND NOTHING — "Write a sentence explaining WHY, then <done/>"
                            (prevents the model from going silent)

4. WHAT IS NOT A FINDING — Anti-patterns: ?.  ??  try/catch  using
                            Safe APIs: Directory.CreateDirectory, ConcurrentDictionary
                            Hedge words: "could/might/may" → reject

5. SEVERITY RULES  — bug vs warning, with decision criteria
6. CONFIDENCE      — high (exact line + trigger) vs low (real shape, proof incomplete)
7. OUTPUT FORMAT   — JSON inside <finding> tags, with good + bad examples
```

---

## What Made the Biggest Difference

### The anti-pattern allowlist

The 7B model's #1 failure: flagging **already-guarded code** as a bug.

```markdown
## What is NOT a finding (do not emit)
Before emitting, scan the surrounding ~5 lines. Reject if:
- `?.` null-conditional — already handled
- `??` or `?? throw` — already handled
- `try`/`catch` around the suspect call — already handled
- `using`/`await using` — already disposes
```

Without this: **60%+ of findings were false positives** on guarded patterns.  
With this: false positives dropped to ~20%, and the remaining ones are low-confidence.

### The hedge-word ban

> *"If your description uses 'potential', 'could', 'might', 'may', 'possibly', or 'in some cases' — do not emit the finding."*

This single rule eliminated an entire class of noise.

---

## Structured Output — Why `<finding>` Tags?

The model returns **free text** with structured blocks embedded:

```xml
Looking at the code, I notice an issue with error handling...

<finding>{
  "severity": "bug",
  "confidence": "high",
  "title": "Null deref when discount is null",
  "description": "When order.Discount is null (line 12), line 47
                  calls discount.Code.ToUpper() → NullReferenceException.",
  "filePath": "OrderService.cs",
  "lineNumber": 47,
  "codeSnippet": "var code = order.Discount.Code.ToUpper();"
}</finding>

The rest of the code looks well-structured...
<done />
```

**Why XML-style tags, not raw JSON?** The model *will* prepend prose — tags survive that. A pure JSON response breaks the moment the model says "Looking at the code..."

---

## The Agentic Loop — Model Requests More Context

The model can ask for code it hasn't seen yet:

```xml
I need to see the constructor to verify the null path.
<request_context type="method">OrderService.OrderService</request_context>
```

**Server response flow:**
1. `FindingStreamParser` detects the `<request_context>` block
2. `ContextRequestHandler` resolves via **Roslyn** — finds the symbol, extracts full syntax
3. Context is appended to the next iteration's prompt
4. Model runs again with the additional code visible

Up to **3 iterations** — each round the model sees more of the codebase. This is what makes it "agentic" rather than single-shot.

---

## What an Expected Result Set Looks Like

A typical `find-bugs` run on a 200-line service file:

| Metric | Value |
|---|---|
| Prompt eval | 60–120s (cold start, CPU) |
| Token generation | 30–60s |
| Total wall time | 2–4 minutes |
| Findings emitted | 2–5 |
| High confidence | 1–2 (exact line + trigger named) |
| Low confidence | 1–3 (pattern real, proof incomplete) |
| False positives | ~20% (pre-aggregation) |
| After aggregation | 0–1 dupes collapsed |

**The 7B model is intentionally noisy.** Low-confidence findings are kept (dimmed in UI) because Copilot will verify them in the handoff step. A missed real bug costs more than a false positive that Copilot dismisses in seconds.

---

<!-- _class: section-header -->

# Analysis Mode

*Agentic finding loop across selected files*

---

<!-- _class: demo -->

## Analysis Mode — How It Works

**8 presets**, filtered by language (C# or PL/SQL):

<span class="chip">find-bugs</span><span class="chip">find-dead-code</span><span class="chip">find-business-rules</span><span class="chip">summarize</span>  
<span class="chip chip-purple">find-bugs-sql</span><span class="chip chip-purple">find-business-rules-sql</span><span class="chip chip-purple">cleanup-stored-proc</span><span class="chip chip-purple">efficiency-review</span>

**Agentic loop** (up to 3 iterations):
1. LLM reads context → emits `<finding>` blocks *and* `<request_context>` requests
2. Server fulfills via **Roslyn** — class bodies, method signatures, callers, callees
3. Findings carry **confidence** (`high` / `low`) — low ones surface dimmed, never dropped

---

<!-- _class: demo -->

## Analysis — Live Demo

> **LIVE DEMO** — load CodeIntel.sln → select a file → pick "find-bugs" → hit Run

**What to watch for:**
- Cold-start panel during prompt evaluation (60–120s silence — model is reading)
- Scan-beam animation as tokens stream in character by character
- `<finding>` blocks appear as structured cards the instant they're parsed
- Confidence chips: solid bar = high, dashed bar = low
- Click a finding → VS Code deep-link opens the exact line

---

<!-- _class: demo -->

## Analysis — Findings Output

Each finding has: **severity** · **file + line** · **title** · **explanation** · **confidence**

- High-confidence findings get a solid indigo left bar
- Low-confidence findings are **dimmed** with a dashed bar and a tooltip
- Ignore button stores a SHA-256 signature — suppressed on future runs

After the loop: **FindingsAggregator** collapses near-duplicates across iterations.  
If any duplicate was `high`, the group promotes to `high`.

---

<!-- _class: demo -->

## Analysis → Copilot Handoff

Hit **Save to repo** → file lands in `docs/codeintel/`:

```markdown
## Copilot Next Step
You are a senior C# engineer reviewing these findings.
For each HIGH confidence finding, produce a Jira ticket:
- Title: one sentence
- Severity: Critical / High / Medium
- Reproduction steps: ...
- Suggested fix: ...
```

Paste `#file:docs/codeintel/2026-05-13-find-bugs-a3f2.md` into Copilot Chat.  
Instant structured triage.

---

<!-- _class: section-header -->

# Trace Mode

*BFS call graph — callers, callees, or both*

---

<!-- _class: demo -->

## Trace Mode — How It Works

Type a method name (or click **"Trace from here"** in any file preview):

- **Direction**: Callers / Callees / Both
- **Depth**: 1–5 levels

**Server does:**
- Roslyn `SymbolFinder.FindCallersAsync` (memoized per run) for callers
- Semantic model walk of `InvocationExpressionSyntax` for callees
- Node classification: `Normal` · `DbAccess` (cylinder) · `HttpCall` (hexagon)
- LLM generates a 1–2 sentence synopsis per node

Mermaid diagram is **generated programmatically** — not LLM-emitted, always correct.

---

<!-- _class: demo -->

## Trace Mode — Live Demo

> **LIVE DEMO** — open a file → click a method → "Trace from here"

**What to watch for:**
- Mode auto-switches to Trace with the symbol's location pre-populated
- Mermaid diagram builds live — DB nodes are cylinders, HTTP nodes are hexagons
- Per-node LLM synopses appear as cards below the graph
- Dashed arrows = back-edges (cycles detected)
- Click a node card → VS Code opens at the method definition

---

<!-- _class: demo -->

## Findings Overlay on Trace

Run an Analysis. Switch to Trace. **The bug findings auto-decorate the call graph.**

- Bug rings appear on Mermaid nodes where findings landed
- Each node card gets a finding chip (`BUG`, `WARN`, `DEAD`) with severity color

```mermaid
flowchart LR
  classDef db fill:#dcfce7,stroke:#16a34a,color:#15803d
  classDef bug stroke:#dc2626,stroke-width:4px

  A([WriteTraceAsync]):::bug --> B[(SaveToDb)]:::db
  A --> C[BuildMermaid]
  A --> D[ReportGenerator]:::bug
```

The trace becomes a **bug heatmap** — instantly see which paths in the call graph have known issues.

---

<!-- _class: section-header -->

# More Cool UX

*Pin to analysis · click-to-trace · code annotation view*

---

<!-- _class: demo -->

## Pin to Analysis + Code Annotation

**Pin to Analysis:** Select a line range in any file → **"Pin to analysis"** → snippet becomes a chip.  
Ask a focused question with exact context attached.

**Code Annotation View:** After a find-bugs / find-dead-code run:
- **Output tab** — streamed model rationale + finding cards (default)
- **Code tab** — findings rendered **inline** next to the line they flagged

Each finding sits next to the source line, color-coded by severity, expandable.

---

<!-- _class: section-header -->

# Metrics Tab

*Static complexity — no inference, always instant*

---

<!-- _class: demo -->

## Metrics — Static Analysis, No LLM

| Metric | C# (Roslyn) | PL/SQL (ANTLR) |
|---|---|---|
| Cyclomatic complexity | ✅ | ✅ |
| Nesting depth | ✅ | — |
| Method length | ✅ | — |
| Parameter count | ✅ | — |
| Empty-catch blocks | ✅ | — |
| Sync-over-async | ✅ | — |
| Cursor declarations | — | ✅ |
| Swallowed WHEN OTHERS | — | ✅ |

Roslyn AST walk for C#, ANTLR token stream for PL/SQL. **No inference.** Cached by content hash — instant on reopen. Sortable table with flag chips, click a row to open the file at that method.

---

<!-- _class: section-header -->

# What Shipped to Make It a Team Tool

*Not just "demo on my laptop"*

---

## Hardening Pass

<div style="display:grid;grid-template-columns:1fr 1fr;gap:1.5em">

<div>

**Reliability**
- Per-analysis CTS triad: user cancel · idle watchdog (90s) · hard ceiling (600s)
- Partial findings survive cancel
- `/healthz` + `/readyz` probes for OpenShift

**Performance**
- Result cache (SHA-256 content key, 7-day TTL)
- Workspace LRU cap (3 loaded solutions)
- O(n) single-pass finding parser

</div>
<div>

**Safety**
- Rate limiting — 5 runs/min per IP, 429 body
- Secret scrubbing (AWS, GitHub PAT, JWT, PEM) before MD write
- PathSafety — containment check, can't write outside workspace

**UX**
- Ignored findings — per-workspace FP suppression
- Findings diff — added / resolved / persisted
- VS Code deep-link from every finding card

</div>
</div>

---

## What I Learned — LLM Prompt Engineering

**The prompt is the product.** 80% of the engineering effort was prompt iteration, not application code. Every sentence in the prompt template exists because a real model failure happened without it.

**Negative examples matter more than positive ones.** Telling the model what NOT to emit (hedge words, already-guarded code, safe APIs) reduced false positives from 60% to ~20%.

**Structured output needs escape hatches.** The model must know what to do when it finds nothing — otherwise it invents findings or goes silent. An explicit "write a sentence explaining why, then `<done/>`" path prevents both failure modes.

**Confidence as a first-class field, not a filter.** Don't drop uncertain findings — surface them dimmed. A 7B model hedges on things Copilot can confirm in seconds. Missing a real bug costs more than showing a false positive.

---

## What I Learned — Architecture

**"Don't make the local model good — make Copilot's verification round fast."**  
A 7B model is noisy. Structured output + confidence fields + aggregation beats prompt engineering alone.

**ANTLR beats regex for real grammars.**  
The regex PL/SQL parser mishandled comments, strings, multi-line statements. Narrow grammar, correct output.

**Machine-specific config drift is a real outage.**  
Lost half a day to committing CUDA settings to main. Lesson: `appsettings.Development.json` (gitignored) + `git update-index --skip-worktree`.

**SQLite is underrated for dev tools.**  
WAL mode, zero ops, OpenShift PVC-mountable. Right call over Postgres.

---

## What's Next

**Before OpenShift:**
- **Auth** — Windows Auth via IIS passthrough; `WorkspaceController.Browse` still leaks the host filesystem today
- **Configurable CORS** — hardcoded `5173/5174` → `appsettings.json`

**Feature expansion:**
- **Oracle live** — `ALL_TABLES` / `USER_SOURCE` instead of repo-only DDL grep
- **TypeScript LSP** — LSP client shipped; verification on real React/Next.js repos ongoing
- **Business documentation mode** — trace all entry points → feature catalog
- **Dockerfile + OpenShift** — multi-stage build, volume mounts, `/readyz` probe

---

<!-- _class: title -->

# Thank You

**Try it:** `dotnet run` — model loads on startup (~30s), status dot goes green when ready

*Local LLM · Roslyn · ANTLR · SignalR · React 19 · SQLite*

---

<!--
════════════════════════════════════════════════════════════════════
  SPEAKER NOTES  (not rendered as slides)
════════════════════════════════════════════════════════════════════

PACING — 12–15 minutes (expanded for LLM deep-dive)

Slide 1 — Title (0:00–0:30)
  "I built a tool during dev days that lets you point a local LLM
   at any codebase — C# solutions, PL/SQL stored procs — and get
   structured findings, call graphs, and complexity metrics in
   minutes, without sending your code anywhere."

Slide 2 — Problem (0:30–1:15)
  "The trigger: I kept getting handed unfamiliar code to review.
   No fast way to get oriented. Read everything slowly, interrupt
   a colleague, or paste fragments into Copilot without full context."

Slide 3 — Architecture (1:15–2:00)
  "The key insight was separating the roles. A 7B model on my
   laptop is good at reading 500 lines and saying 'three things
   look suspicious.' It's bad at being authoritative. Copilot —
   with our team's existing subscription — is great at taking a
   structured briefing and producing tickets, PRs, fix plans.
   The MD report is the bridge. Durable, committable, AI-consumable."

Slide 4 — Three Modes (2:00–2:15)
  Quick overview. Three modes: analysis, trace, metrics.

── HOW AN LLM WORKS ────────────────────────────────────────────

Slide 5 — Section header (2:15–2:20)

Slide 6 — LLM Inference (2:20–3:15)
  "Two phases. First: prompt eval — the model reads your entire
   input at once. This is the slow part, 60-120 seconds of silence
   on CPU. It's not hanging. Second: token generation — one token
   at a time, each feeding back as input for the next. About 10-25
   tokens per second on a laptop CPU with a 7B model."

Slide 7 — Tokens (3:15–3:45)
  "LLMs don't see characters or words — they see tokens, which are
   subword chunks. NullReferenceException is 3 tokens, not 1 word.
   This matters because our context budget is 5000 tokens. A 300-line
   C# file might be 2500 tokens. Files that don't fit get auto-chunked
   at brace boundaries."

Slide 8 — How Tokens Reach the UI (3:45–4:15)
  "Every single token is pushed over SignalR the instant it's generated.
   The UI shows a scan-beam animation. When the parser detects a complete
   <finding> block in the stream, it fires a structured finding card.
   You see both the raw model output and the parsed findings simultaneously."

── PROMPT ENGINEERING ──────────────────────────────────────────

Slide 9 — Section header (4:15–4:20)

Slide 10 — The Challenge (4:20–4:50)
  "The hardest engineering problem wasn't the architecture — it was
   getting a 7B model to produce useful output. Without careful prompts
   it hedges everything, hallucinates bugs that don't exist, and repeats
   itself. The prompt is a specification, not a suggestion."

Slide 11 — Anatomy of find-bugs.md (4:50–5:30)
  "Seven sections, each solving a specific failure mode. What to emit,
   what to look for, what to do when you find nothing, what is NOT a
   finding — that anti-pattern section is the most important one —
   severity rules, confidence guidance, and output format with examples."

Slide 12 — What Made the Biggest Difference (5:30–6:15)
  "Two things. First: the anti-pattern allowlist. The model's #1 failure
   was flagging code that's already guarded — null-conditional, try/catch,
   using blocks. Telling it to scan 5 lines around the suspect line and
   reject if any guard is present dropped false positives from 60% to 20%.
   Second: the hedge-word ban. One sentence — 'if your description uses
   could, might, may, or possibly, don't emit it' — eliminated an entire
   class of noise."

Slide 13 — Structured Output (6:15–6:45)
  "The model returns free text with XML-tagged JSON blocks embedded.
   Why not pure JSON? Because the model WILL prepend prose. Tags survive
   that. A pure JSON response breaks the moment it says 'Looking at...'
   The parser does a single-pass O(n) scan for opening and closing tags."

Slide 14 — Agentic Loop (6:45–7:15)
  "The model can request code it hasn't seen. It emits a request_context
   tag, the server resolves via Roslyn, appends to the next iteration.
   Up to 3 rounds. This is what makes it agentic — the model decides
   what else it needs to see."

Slide 15 — Expected Results (7:15–7:45)
  "Typical run: 2-4 minutes, 2-5 findings, 1-2 high confidence, about
   20% false positives post-aggregation. The model is intentionally
   noisy — low-confidence findings are kept because Copilot verifies."

── ANALYSIS DEMO ───────────────────────────────────────────────

Slide 16 — Section header (7:45–7:50)

Slide 17 — Analysis How It Works (7:50–8:15)
  Quick — 8 presets, agentic loop, confidence field. Already covered
  the mechanics in the LLM section.

Slide 18 — Live Demo (8:15–9:30)  [LIVE DEMO]
  Load CodeIntel.sln → select a file → find-bugs → Run
  Point out: cold-start silence, scan beam, findings appearing,
  confidence chips, click to open VS Code.

Slide 19 — Findings Output (9:30–9:45)
  Quick — aggregation, ignore button, SHA-256 signature.

Slide 20 — Copilot Handoff (9:45–10:00)
  "Save to repo, paste #file: into Copilot Chat, instant triage."

── TRACE SECTION ───────────────────────────────────────────────

Slide 21 — Section header (10:00–10:05)

Slide 22 — Trace How It Works (10:05–10:25)
  Quick — Roslyn BFS, node classification, programmatic Mermaid.

Slide 23 — Trace Live Demo (10:25–11:00)  [LIVE DEMO]
  Open file, click method, "Trace from here", watch Mermaid build.

Slide 24 — Findings Overlay (11:00–11:15)
  "Run analysis then switch to trace — bug rings appear on the graph."

── UX + METRICS ────────────────────────────────────────────────

Slide 25 — Section header (11:15–11:20)

Slide 26 — Pin + Code Annotation (11:20–11:35)
  Quick — pin snippets, inline annotations. Two features, one slide.

Slide 27 — Section header (11:35–11:40)

Slide 28 — Metrics (11:40–12:00)
  "No inference. Roslyn + ANTLR. Cached. Click to open."

── HARDENING + WRAP ────────────────────────────────────────────

Slide 29 — Section header (12:00–12:05)

Slide 30 — Hardening (12:05–12:30)
  Quick scan of the grid. "Rate limiting, secret scrubbing, result cache."

Slide 31 — What I Learned — Prompts (12:30–13:00)
  "The prompt is the product. Negative examples > positive. Confidence
   as a field, not a filter. Escape hatches for empty results."

Slide 32 — What I Learned — Architecture (13:00–13:30)
  "Briefing officer / analyst split. ANTLR > regex. Config drift = outage.
   SQLite is perfect for dev tools."

Slide 33 — What's Next (13:30–13:50)
  Auth gate + feature expansion. Quick.

Slide 34 — Thank You (13:50–14:00)

════════════════════════════════════════════════════════════════════
-->
