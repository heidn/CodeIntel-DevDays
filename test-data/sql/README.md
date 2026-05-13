# test-data/sql — PL/SQL UI Smoke-Test Fixtures

Load this folder via the workspace picker. Each fixture below was planted with deliberate
signals for one of the 4 SQL presets. Use this README as a cheat sheet to evaluate whether
the local model picked them up.

> The fixtures are intentionally written *without* `-- BUG:` style markers so the model
> can't cheat by reading the comments. The "what's planted" list lives only here.

---

## Files

### Tables / DDL

| File | What's in it |
|---|---|
| `tables.sql` | DDL for `customers`, `orders`, `order_items`, `products` (existing fixture, unchanged) |
| `employees_tables.sql` | DDL for `departments`, `employees`, `salary_grades`, `audit_log` — rich CHECK / FK / DEFAULT / UNIQUE constraints |

### Stored procedure packages

| File | Demonstrates | Best preset |
|---|---|---|
| `orders_api.pkg` / `.pkb` | Clean(-ish) baseline | Any (`summarize` is a good no-bugs check) |
| `payroll_pkg.pkb` | Deliberate PL/SQL bugs | **find-bugs-sql** |
| `order_reports_pkg.pkb` | Deliberate performance issues | **efficiency-review** |
| `hr_legacy_pkg.pkb` | Deliberate cleanup targets | **cleanup-stored-proc** |
| `employees_tables.sql` | Deliberate business rules | **find-business-rules-sql** |

---

## What each preset SHOULD find

### `find-bugs-sql` on `payroll_pkg.pkb`

| Procedure | Planted bug |
|---|---|
| `apply_dept_raise` | Explicit cursor `OPEN c_emps` with no `CLOSE` on any path — cursor leak |
| `apply_dept_raise` | `EXCEPTION WHEN OTHERS THEN NULL` — swallowed exception |
| `deactivate_terminated` | `UPDATE employees SET status = 'TERMINATED'` with **no `WHERE` clause** — updates every employee |
| `lookup_employee` | `SELECT ... INTO p_name FROM employees WHERE emp_id = p_emp_id` with no `NO_DATA_FOUND` / `TOO_MANY_ROWS` handler |
| `count_blanks` | `WHERE termination_date = NULL` — must be `IS NULL`; the `= NULL` comparison is always false |
| `run_dynamic_update` | `EXECUTE IMMEDIATE 'UPDATE … status = ''' \|\| p_status \|\| '''';` — string-concatenated dynamic SQL (no binds); also: the resulting UPDATE has no `WHERE`, so it overwrites every row |

**Expected:** at least 4–5 distinct findings. The 7B model often misses the cursor leak when
the proc is short; don't be surprised if that one slips through.

### `efficiency-review` on `order_reports_pkg.pkb`

| Procedure | Planted signal |
|---|---|
| `backfill_customer_notes` | Row-by-row `UPDATE` inside a cursor loop — should be a single `UPDATE … (SELECT …)` or `MERGE` |
| `search_orders_by_status` | String-concatenated dynamic SQL (no binds) — prevents cursor sharing |
| `find_customer_orders` | `UPPER(c.last_name) = UPPER(p_lastname)` — function on indexed column blocks index use |
| `find_customer_orders` | `SELECT *` in a join |
| `id_exists` | `WHERE order_id = p_id_text` — `order_id` is `NUMBER`, predicate forces implicit conversion |
| `id_exists` | `SELECT COUNT(*)` used to test existence — should be `WHERE EXISTS` or `ROWNUM = 1` |
| `top_three_recent` | `WHERE ROWNUM <= 3 ORDER BY order_date DESC` — `ROWNUM` filters before sort; returns wrong rows |

**Expected:** at least 4–6 findings. The 7B model is shakier on efficiency than on bugs;
verify Copilot Next Step reads cleanly even if some findings are weak.

### `cleanup-stored-proc` on `hr_legacy_pkg.pkb`

| Procedure | Planted signal |
|---|---|
| `process_hire` | Magic numbers — `p_status_code = 1 / 2 / 3` with no named constants |
| `process_hire` | Inconsistent naming — `p_first_name` / `pSalary` / `pHireDate` / `p_email` mixed in one signature |
| `process_hire` | Long parameter list (9 parameters) — candidate for a record type |
| `process_hire` | Dead code — `v_dummy := 0; v_dummy := 999;` with `v_dummy` never read |
| `process_hire` | Three near-identical `INSERT INTO employees` branches differing only by status literal — extractable |
| `process_hire` | Two `EXCEPTION` branches doing the same audit-insert + RAISE — extractable utility |

**Expected:** 3–5 findings. The naming-inconsistency one is the easiest signal for a 7B
model to pick up.

### `find-business-rules-sql` on `employees_tables.sql`

Should extract (at minimum):
- Department codes (`cost_center`) are unique (UK)
- Departments must have an active flag of `'Y'` or `'N'` (CHECK)
- Every employee must belong to a department (FK + NOT NULL)
- Email is required and unique per employee (NOT NULL + UK)
- Employee status must be one of `ACTIVE` / `LEAVE` / `TERMINATED` / `RETIRED` (CHECK)
- Salary must be positive (CHECK)
- Termination date, if present, must be on or after hire date (CHECK)
- Hire date defaults to the current date when not supplied (DEFAULT SYSDATE)
- Salary-grade names are unique (UK) and `max_salary > min_salary` (CHECK)
- Audit log auto-stamps timestamp + user via DEFAULT values
- Audit `action_type` must be one of `INSERT` / `UPDATE` / `DELETE` / `LOGIN` / `EXEC` (CHECK)

**Expected:** 8+ rules. This preset is the friendliest to a 7B model — the signals are
right there in the DDL syntax.

---

## How to run the smoke test

1. `dotnet run --project src\CodeIntel.Server` and wait for the green dot.
2. Load `c:\Users\heidn\Repos\Devdays\CodeIntel\test-data\sql\` via the folder picker.
3. The tree should show all 8 files; the preset pane should show only the 4 SQL presets.
4. Pick one file from the table above, pick the matching preset, click Run.
5. Watch the server console for:
   - `Parsed PL/SQL refs in <file>.pkb: T tables, R routines, P packages`
   - `PL/SQL deps: N appended` — confirms the resolver pulled in `employees_tables.sql` / `tables.sql`
6. After completion, click **Save to repo** — verify the report MD lists the right "Copilot
   Next Step" template for that preset.
7. Skim the report's Findings section against the table above. Count hits vs misses.

### Bonus: cross-file resolution test

When you run `find-bugs-sql` on `payroll_pkg.pkb`, the parser should extract `employees` as
a referenced table, and the resolver should attach `employees_tables.sql` to the context
(it'll match via the CREATE-TABLE DDL grep, since the filename is `employees_tables.sql`
not `employees.sql`). Look for the `--- PL/SQL OBJECT DEFINITIONS ---` header in the
saved report's context section to confirm.
