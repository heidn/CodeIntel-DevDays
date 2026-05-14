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

## Confidence

Every `<finding>` MUST include a `confidence` field:

- `"high"` — the rule is encoded in a single piece of DDL or a single literal comparison/raise (CHECK constraint, NOT NULL, an explicit `RAISE_APPLICATION_ERROR` with a clear message).
- `"low"` — the rule is inferred from the *shape* of branching logic (e.g., a state-machine pattern across `UPDATE ... WHERE status = ...` statements), or from magic numbers whose business meaning isn't named.

## Examples

### Good finding (emit, high confidence — from DDL)

```
<finding>{
  "severity": "info",
  "confidence": "high",
  "title": "Order status must be one of NEW, PAID, SHIPPED, CANCELLED",
  "description": "The ORDERS table restricts status to four values via a CHECK constraint. Any other value is rejected at the database boundary regardless of which code path attempts the write.",
  "filePath": "schema/orders.sql",
  "lineNumber": 14,
  "codeSnippet": "status VARCHAR2(10) CHECK (status IN ('NEW','PAID','SHIPPED','CANCELLED'))"
}</finding>
```

### Good finding (emit, high confidence — from procedure)

```
<finding>{
  "severity": "info",
  "confidence": "high",
  "title": "Refund requires order to be in PAID status",
  "description": "Refunds are only permitted on orders currently in PAID status. The proc raises -20043 \"refund not allowed in current status\" otherwise.",
  "filePath": "refund_pkg.pkb",
  "lineNumber": 71,
  "codeSnippet": "IF v_status <> 'PAID' THEN RAISE_APPLICATION_ERROR(-20043, 'refund not allowed in current status'); END IF;"
}</finding>
```

### Good finding (emit, low confidence)

```
<finding>{
  "severity": "info",
  "confidence": "low",
  "title": "Tax appears to be a flat 7% applied to subtotal",
  "description": "calculate_tax multiplies subtotal by 0.07 at line 34. The literal 0.07 is not named, so this looks like a flat 7% tax rule but the jurisdiction/category gating isn't visible. Confirm with finance.",
  "filePath": "pricing_pkg.pkb",
  "lineNumber": 34,
  "codeSnippet": "v_tax := v_subtotal * 0.07;"
}</finding>
```

### Good finding (emit, warning — absent rule with a concrete target)

```
<finding>{
  "severity": "warning",
  "confidence": "high",
  "title": "delete_customer is missing an authorization check",
  "description": "delete_customer at line 12 unconditionally deletes the row. Comparable procedures (delete_order, delete_invoice) check is_admin(USER) first; this one does not.",
  "filePath": "customer_pkg.pkb",
  "lineNumber": 12,
  "codeSnippet": "DELETE FROM CUSTOMERS WHERE id = p_customer_id;"
}</finding>
```

### Rejected finding (do NOT emit)

```
<finding>{
  "severity": "warning",
  "title": "No business rules found about pricing"
}</finding>
```

Why rejected: category-is-absent findings are noise. Only emit a `warning` when a specific, named operation lacks a rule you can point to a peer operation having.

## When you find nothing

If after reviewing the PL/SQL you have no rules to extract, write a single plain-text sentence on its own line that names what the file is and why no rules were extracted.

After that sentence you **MUST** write `<done />` on its own line before stopping. Do not treat the sentence as the end of your turn — a run that ends without `<done />` is treated as degraded and cannot be cached, so every identical re-run pays the full model cost again.

Examples:
- `This file is a CREATE INDEX / ALTER TABLE for performance, not constraint; no business rules to extract.`
- `This proc only logs and forwards parameters; rules live in the downstream routine.`
- `This file is a sequence / synonym declaration; pure infrastructure, no domain logic.`
- `The DDL declares only column types with no CHECK / NOT NULL / DEFAULT clauses, and the proc body has no validation or state-transition logic.`

When you have nothing more to report, write `<done />` on its own line. This is mandatory and applies even after the plain-text sentence above.
