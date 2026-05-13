Review the Oracle PL/SQL code below for likely performance issues.

This is a SIGNAL preset, not a verdict preset. The local model cannot run EXPLAIN PLAN,
read AWR reports, or check actual index existence. Your job: flag concrete patterns that
*usually* hurt performance, name them precisely, and let the downstream tool (Copilot)
confirm against the real DB.

The primary file(s) under analysis are above. PL/SQL OBJECT DEFINITIONS (when present)
include indexes / constraints when they appear in the DDL — use them to refine your
findings (e.g., a `WHERE` on a column that the DDL shows is part of a `PRIMARY KEY` is
likely fine; same predicate on a non-indexed column is suspect).

## What to look for

- **Row-by-row processing** — explicit cursor + `FETCH` + per-row `INSERT`/`UPDATE` over a set that could use `INSERT ... SELECT`, `MERGE`, or `BULK COLLECT` + `FORALL`.
- **Implicit type conversion in `WHERE`** — `WHERE numeric_col = '123'` or `WHERE TO_CHAR(date_col) = ...` blocks index use. Quote the predicate.
- **Functions on indexed columns in `WHERE`** — `WHERE UPPER(name) = ...` blocks the index unless a function-based index exists.
- **`SELECT *` in a join** — fetches more columns than needed; only flag when the projection is clearly excessive.
- **Cartesian-shaped joins** — `FROM a, b` without a join condition, or a join condition only on a constant.
- **Missing bind variables** — values concatenated into `EXECUTE IMMEDIATE` strings prevent cursor sharing. (When `USING` is present, do NOT flag.)
- **`COUNT(*)` to test existence** — should be `WHERE EXISTS (...)` or `WHERE ROWNUM = 1`.
- **`ORDER BY` followed by `ROWNUM` filter on the outer query** — `ROWNUM` filters before sort; needs subquery.
- **`UNION` where `UNION ALL` would suffice** — `UNION` adds a dedupe sort.
- **Lookups inside loops** — `SELECT INTO` repeated per iteration over a join key that could be hoisted.

## What is NOT a finding

Reject patterns that *might* be slow but lack a specific reason:

- A `SELECT` on a table without commenting on cardinality — no signal.
- "This query is complex" — not a finding.
- "Consider adding an index" without naming the column and the predicate that misses it.
- A finding whose only justification is "could be slow on large data" — that's true of most queries; not signal.

## Severity

- `suggestion` — a concrete pattern likely to be slow, with named line and predicate / shape.
- `warning` — the pattern almost always hurts (e.g., row-by-row over thousands of rows; cartesian join). Still requires named evidence.

## Output rules

- Each `description` names the pattern, the exact predicate or shape, and the cost (e.g., "blocks index use", "n+1 round trips", "extra sort").
- Each `codeSnippet` quotes the concrete query / predicate.
- One finding per pattern; do not split.
- When you have nothing more to report, write `<done />` on its own line.
