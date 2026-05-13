Review the Oracle PL/SQL code below and identify specific cleanup opportunities.

This preset produces a refactor briefing for a follow-up tool (Copilot) — your job is to
spot the targets, not to write the refactor. Be precise about *what* and *where*; leave
the *how* for the downstream tool.

The primary file(s) under analysis are above. PL/SQL OBJECT DEFINITIONS (when present)
are supporting material to disambiguate references.

## What to flag

- **Dead code** — unreachable branches, variables declared but never assigned, functions never called from the primary file (cross-reference against the OBJECT DEFINITIONS only — assume external callers if you can't see the full picture).
- **Magic numbers and strings** — hardcoded status codes, type ids, thresholds, sentinel values. Quote the literal and suggest naming.
- **Extractable subqueries / inline views** — a complex `SELECT` repeated more than once, or deeply nested, that should become a CTE or view.
- **Repeated `EXCEPTION` blocks** — same handler structure across multiple procs is a candidate for a shared utility package.
- **Inconsistent error handling** — some routines `RAISE`, others swallow, others log; flag the inconsistency, not the routines individually.
- **Long parameter lists** — 6+ params suggests a record type or refactor.
- **`SELECT ... INTO record` followed by individual field assignments** — usually a sign of an outdated pattern.
- **Cursor usage that could be a `FORALL` / bulk operation** — row-by-row processing of a known set.
- **Inconsistent naming** — quote two adjacent examples (e.g., `v_id` vs `pId`) rather than catalog all.

## Severity

- `suggestion` — a clear cleanup target with named lines.
- `info` — an observation about a broader pattern (e.g., "inconsistent error handling across these 3 procs") that isn't tied to a single line.

## Do NOT emit

- Bugs — that's the bug-finding preset.
- Performance suggestions that would change query plans — that's the efficiency preset.
- Business-rule observations — that's the business-rules preset.
- "Could be cleaner" without a concrete target.

## Output rules

- Each `description` names *what* to extract / rename / consolidate, and *why* the current state hurts (readability, duplication, maintainability).
- Each `codeSnippet` quotes the concrete example.
- One finding per cleanup target.
- When you have nothing more to report, write `<done />` on its own line.
