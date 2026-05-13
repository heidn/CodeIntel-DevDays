Analyze the Oracle PL/SQL code below and extract business rules embedded in it.

The primary file(s) under analysis are above. PL/SQL OBJECT DEFINITIONS (when present) are
supporting material. Treat constraints and structure in those definitions as ALSO being
rules to report, not just background.

Use these severity levels:
- `info` — a rule you found explicitly in the code or DDL.
- `warning` — a rule you expected but is absent or incomplete (e.g., no auth check before a sensitive operation, no validation on a critical input column).

For each finding:
- `codeSnippet` — quote the specific line that encodes the rule. If no single line applies (e.g., the rule spans multiple constraints), omit the field rather than reuse an unrelated snippet.
- `description` — plain English a business analyst can read without knowing PL/SQL.

## Look for rules in

**Table DDL (in PL/SQL OBJECT DEFINITIONS):**
- `CHECK` constraints — value-range or value-set rules.
- `NOT NULL` columns — required-field rules.
- `DEFAULT` clauses — defaulting rules when input is omitted.
- `FOREIGN KEY` / `REFERENCES` — referential rules between domains.
- `UNIQUE` / `PRIMARY KEY` — identity / no-duplicates rules.

**Procedure / function bodies:**
- Input validation — `RAISE_APPLICATION_ERROR` when a parameter fails a test.
- Calculation formulas — tax, discount, fee, rate math; include the formula in the description.
- State transitions — `UPDATE ... SET status = '...' WHERE status = '...'` patterns name the allowed transitions.
- Authorization gates — checks against `USER`, role tables, or a security package before sensitive ops.
- Data transformation / normalization — trimming, uppercasing, padding, code mapping.

**Triggers:**
- BEFORE INSERT / BEFORE UPDATE rules that enforce invariants the schema can't express.
- Audit-row stamping is implementation detail, not a business rule — skip unless the rule itself is interesting.

**Packages specs:**
- Parameter `DEFAULT` values and types — these constrain the legal input domain.

## Do NOT emit

- Implementation hygiene (good naming, commit/rollback placement) — that's the cleanup preset.
- Performance observations — that's the efficiency preset.
- A category-is-absent finding — only emit rules you can point at.

When you have nothing more to report, write `<done />` on its own line.
