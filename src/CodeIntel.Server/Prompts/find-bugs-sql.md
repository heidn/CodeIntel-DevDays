Find specific, real bugs in the Oracle PL/SQL code below.

Emit a <finding> only when you can name BOTH of:
1. The exact input, branch, or DB state that triggers the failure.
2. The specific failure mode (which exception, wrong row count, leaked cursor, corrupt data, etc.).

If you cannot name both, emit nothing. A short, accurate report beats a long, noisy one.

The primary file(s) under analysis are above. PL/SQL OBJECT DEFINITIONS (when present) are
supporting material — tables, views, packages, and routines the primary file references.
Use them to verify column names, types, and FK relationships when relevant.

## What to look for

- `WHEN OTHERS THEN NULL` (or `WHEN OTHERS` without RAISE/RAISE_APPLICATION_ERROR/logging) — swallowed exceptions.
- `UPDATE` or `DELETE` with no `WHERE` clause (or a `WHERE` clause that's trivially true).
- Implicit type conversion in a `WHERE` clause that prevents index use (`WHERE numeric_col = '123'`, `WHERE TO_CHAR(date_col) = ...`).
- NULL comparison via `=` or `<>` instead of `IS NULL` / `IS NOT NULL`.
- Explicit cursors opened in a loop without `CLOSE` on every exit path — cursor leak.
- `COMMIT` or `ROLLBACK` inside a trigger — raises `ORA-04092`.
- Hard-coded ROWNUM filter combined with `ORDER BY` without a subquery wrapper — returns wrong rows.
- `SELECT ... INTO` without a `NO_DATA_FOUND` / `TOO_MANY_ROWS` handler when zero/many rows is possible.
- Bind-variable substitution via string concatenation in `EXECUTE IMMEDIATE` — SQL injection.
- Misuse of `DECODE` / `CASE` defaults that silently coerce NULL to a non-NULL value when callers expect NULL.
- Sequence `.NEXTVAL` referenced inside a row trigger that fires per-row when the design assumed once-per-statement.

## What is NOT a finding (do not emit)

Before emitting any finding, scan the surrounding lines. Reject the finding if any of these apply:

**Already-guarded patterns:**
- A handler block follows the suspect statement: `EXCEPTION WHEN <named_exc> THEN ...` matches the failure mode.
- `WHEN OTHERS THEN` re-raises with `RAISE` or `RAISE_APPLICATION_ERROR` or calls a logging package — the exception IS being handled.
- A preceding `IF ... IS NOT NULL THEN` guard.
- A `FOR ... IN cursor LOOP` (implicit cursor) — Oracle closes these automatically. Do not flag as a cursor leak.

**Safe-by-design patterns:**
- `MERGE INTO` with a `WHEN MATCHED` / `WHEN NOT MATCHED` clause is normal.
- `COMMIT` inside an autonomous-transaction routine (`PRAGMA AUTONOMOUS_TRANSACTION`) is intentional, not a bug.
- `EXECUTE IMMEDIATE` with `USING bind1, bind2` is parameterized — not injection.
- `SELECT * INTO record_var FROM t WHERE pk = :id` paired with a `NO_DATA_FOUND` handler is correct.

**Speculation.** If your description uses any of these words, do not emit the finding: "potential", "could", "might", "may", "possibly", "in some cases".

**Style/preference.** Naming, formatting, "this could be cleaner" — not bugs.

## Severity

- `bug` — the failure occurs in the code as written, on a path you can describe.
- `warning` — the failure path is real but depends on caller misuse or DB state not shown. Still requires a named, concrete path.

## Confidence

Every `<finding>` MUST include a `confidence` field:

- `"high"` — you can quote the failing line and the exact DB state / input that triggers the failure. The trigger is in the file you can see.
- `"low"` — the pattern is real but the trigger lives in a caller or a DB row distribution you don't have full visibility into (e.g., `SELECT INTO` could match many rows depending on data — fine if the design assumes one row).

## Output rules

- Each `description` states the failure path in one sentence: *"When X happens at line N, Y raises Z."*
- Each `codeSnippet` quotes the exact failing line — not the surrounding scaffolding.
- One finding per defect. Do not split.
- When you have nothing more to report, write `<done />` on its own line.

## Examples

### Good finding (emit, high confidence)

```
<finding>{
  "severity": "bug",
  "confidence": "high",
  "title": "Swallowed exception in process_order — failures look like successes",
  "description": "The EXCEPTION block at line 88 catches WHEN OTHERS THEN NULL with no logging or re-raise. When the INSERT at line 71 fails (e.g., FK violation against ORDER_HEADER), the proc returns successfully and the caller commits a partial state.",
  "filePath": "process_order.sql",
  "lineNumber": 88,
  "codeSnippet": "EXCEPTION WHEN OTHERS THEN NULL;"
}</finding>
```

### Good finding (emit, high confidence)

```
<finding>{
  "severity": "bug",
  "confidence": "high",
  "title": "SQL injection in dynamic SQL — concatenated user parameter",
  "description": "Line 42 builds the WHERE clause by concatenating p_filter into the EXECUTE IMMEDIATE string with no bind variable. A caller supplying p_filter => '1=1 OR 1=1' bypasses the intended row restriction.",
  "filePath": "search_orders.sql",
  "lineNumber": 42,
  "codeSnippet": "EXECUTE IMMEDIATE 'SELECT * FROM ORDERS WHERE ' || p_filter;"
}</finding>
```

### Good finding (emit, low confidence)

```
<finding>{
  "severity": "warning",
  "confidence": "low",
  "title": "SELECT INTO has no TOO_MANY_ROWS handler",
  "description": "Line 55 does SELECT id INTO v_id FROM CUSTOMERS WHERE last_name = p_last_name. If two customers share a last name the proc raises TOO_MANY_ROWS unhandled. Depends on whether last_name is unique in this deployment.",
  "filePath": "lookup_customer.sql",
  "lineNumber": 55,
  "codeSnippet": "SELECT id INTO v_id FROM CUSTOMERS WHERE last_name = p_last_name;"
}</finding>
```

### Rejected finding (do NOT emit)

```
<finding>{
  "severity": "warning",
  "title": "COMMIT inside autonomous transaction may cause issues"
}</finding>
```

Why rejected: `COMMIT` inside a routine declared `PRAGMA AUTONOMOUS_TRANSACTION` is the documented, intentional pattern. Inventing a failure for safe-by-design syntax is a hallucination.

### Rejected finding (do NOT emit)

```
<finding>{
  "severity": "bug",
  "title": "Implicit cursor in FOR loop might leak"
}</finding>
```

Why rejected: implicit cursors (`FOR rec IN (SELECT ...) LOOP`) are auto-closed by Oracle. Only explicit `OPEN ... FETCH` patterns can leak.

## When you find nothing

If after reviewing the PL/SQL you have no bugs to flag, write a single plain-text sentence on its own line that names what the file is and why no bugs were found.

After that sentence you **MUST** write `<done />` on its own line before stopping. Do not treat the sentence as the end of your turn — a run that ends without `<done />` is treated as degraded and cannot be cached, so every identical re-run pays the full model cost again.

Examples:
- `This file is a CREATE-OR-REPLACE VIEW with no procedural logic; no failure paths to flag.`
- `This is a small DDL script with only column definitions; no executable PL/SQL.`
- `This proc only logs and forwards parameters to another routine; bugs would live downstream.`
- `Every cursor is closed on every exit path and every exception handler re-raises or logs; the visible patterns are safe-by-design.`

When you have nothing more to report, write `<done />` on its own line. This is mandatory and applies even after the plain-text sentence above.
