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

## Confidence

Every `<finding>` MUST include a `confidence` field:

- `"high"` — the pattern is a well-known plan-killer (implicit type conversion, function on indexed column, cartesian join, COUNT(\*) for existence), and the predicate / shape is visible in the file.
- `"low"` — the pattern's impact depends on cardinality, index existence, or data distribution the analyzed code doesn't reveal (e.g., a row-by-row loop is fine for a handful of rows; SELECT * is fine if downstream uses every column). Emit so Copilot can confirm against EXPLAIN PLAN.

## Output rules

- Each `description` names the pattern, the exact predicate or shape, and the cost (e.g., "blocks index use", "n+1 round trips", "extra sort").
- Each `codeSnippet` quotes the concrete query / predicate.
- One finding per pattern; do not split.
- When you have nothing more to report, write `<done />` on its own line.

## Examples

### Good finding (emit, high confidence)

```
<finding>{
  "severity": "warning",
  "confidence": "high",
  "title": "Implicit type conversion blocks index use on ORDERS.id",
  "description": "Line 33 filters WHERE id = '12345' — id is NUMBER per the DDL, so Oracle wraps the column in TO_CHAR for the comparison, disabling the PK index. Pass a numeric literal or a bound NUMBER variable.",
  "filePath": "lookup_order.sql",
  "lineNumber": 33,
  "codeSnippet": "SELECT * FROM ORDERS WHERE id = '12345';"
}</finding>
```

### Good finding (emit, high confidence)

```
<finding>{
  "severity": "suggestion",
  "confidence": "high",
  "title": "COUNT(*) used to test existence",
  "description": "Line 58 uses SELECT COUNT(*) INTO v_count FROM ORDERS WHERE customer_id = p_cid; v_count > 0 to test for existence. This scans matching rows; rewrite as WHERE EXISTS (...) which short-circuits on the first hit.",
  "filePath": "customer_pkg.pkb",
  "lineNumber": 58,
  "codeSnippet": "SELECT COUNT(*) INTO v_count FROM ORDERS WHERE customer_id = p_cid;"
}</finding>
```

### Good finding (emit, low confidence)

```
<finding>{
  "severity": "suggestion",
  "confidence": "low",
  "title": "Row-by-row UPDATE inside cursor loop",
  "description": "Lines 90–98 fetch rows from c_orders and UPDATE ORDER_HEADER per row. If the cursor result set is more than a few hundred rows, replace with FORALL + BULK COLLECT or a single UPDATE ... WHERE id IN (...). Impact depends on typical batch size — Copilot to confirm.",
  "filePath": "batch_update.sql",
  "lineNumber": 90,
  "codeSnippet": "FOR rec IN c_orders LOOP UPDATE ORDER_HEADER SET status = 'X' WHERE id = rec.id; END LOOP;"
}</finding>
```

### Rejected finding (do NOT emit)

```
<finding>{
  "severity": "suggestion",
  "title": "Consider adding an index"
}</finding>
```

Why rejected: doesn't name the column, doesn't name the predicate that misses an index, doesn't say which query suffers. The downstream tool can't act on this.

### Rejected finding (do NOT emit)

```
<finding>{
  "severity": "warning",
  "title": "This query could be slow on large data"
}</finding>
```

Why rejected: true of almost every query. Not a signal.
