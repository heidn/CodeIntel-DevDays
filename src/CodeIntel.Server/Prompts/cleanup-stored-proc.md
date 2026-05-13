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

## Confidence

Every `<finding>` MUST include a `confidence` field:

- `"high"` — the target is concrete and visible: a specific literal to name, a specific block to extract, a specific repeated pattern across procs you can list.
- `"low"` — the target depends on intent or wider-codebase context you can't fully see (e.g., a private helper that may be called from a different package, a parameter list that's long but every parameter is genuinely required).

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

## Examples

### Good finding (emit, high confidence)

```
<finding>{
  "severity": "suggestion",
  "confidence": "high",
  "title": "Magic status code '03' should be a named constant",
  "description": "Status code '03' (refund-pending) appears as a literal at lines 47, 88, and 121. Extract a named constant — c_status_refund_pending CONSTANT VARCHAR2(2) := '03' — so the meaning is visible at every call site.",
  "filePath": "refund_pkg.pkb",
  "lineNumber": 47,
  "codeSnippet": "UPDATE ORDERS SET status = '03' WHERE id = p_order_id;"
}</finding>
```

### Good finding (emit, high confidence)

```
<finding>{
  "severity": "info",
  "confidence": "high",
  "title": "Inconsistent error handling across refund_pkg",
  "description": "process_refund (line 14) re-raises with RAISE_APPLICATION_ERROR. cancel_refund (line 88) logs and swallows. void_refund (line 144) commits and returns NULL on error. Consolidate on one pattern — Copilot to propose which.",
  "filePath": "refund_pkg.pkb",
  "lineNumber": 14
}</finding>
```

### Good finding (emit, low confidence)

```
<finding>{
  "severity": "suggestion",
  "confidence": "low",
  "title": "validate_customer may be uncalled within this file",
  "description": "validate_customer at line 201 has no callers in the analyzed file. It is exposed in the package spec, so external callers are possible. Confirm against the wider repo before deletion.",
  "filePath": "customer_pkg.pkb",
  "lineNumber": 201,
  "codeSnippet": "PROCEDURE validate_customer (p_id IN NUMBER) IS"
}</finding>
```

### Rejected finding (do NOT emit)

```
<finding>{
  "severity": "suggestion",
  "title": "This package could use better naming overall"
}</finding>
```

Why rejected: no concrete target. The downstream tool can't act on "better naming overall." Either quote two specific names that disagree, or stay silent.
