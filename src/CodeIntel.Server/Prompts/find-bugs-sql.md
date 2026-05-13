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

## Output rules

- Each `description` states the failure path in one sentence: *"When X happens at line N, Y raises Z."*
- Each `codeSnippet` quotes the exact failing line — not the surrounding scaffolding.
- One finding per defect. Do not split.
- When you have nothing more to report, write `<done />` on its own line.
