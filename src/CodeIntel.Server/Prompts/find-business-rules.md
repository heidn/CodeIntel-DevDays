Analyze the code below and extract business rules embedded in it.

Use these severity levels:
- `"info"` — a rule you found explicitly in the code.
- `"warning"` — a rule you expected to find but is absent or incomplete (e.g. no auth check before a sensitive operation, no validation on a critical input).

For each finding:
- `codeSnippet`: quote the specific line that encodes the rule. If no single line applies, omit the field entirely — do not reuse an unrelated snippet as a placeholder.
- `description`: plain English so a business analyst can understand the rule without reading the code.

Focus on:
- Input validation and domain constraints (valid/invalid values, NOT NULL, CHECK constraints, column types).
- Calculation formulas and business math (totals, taxes, discounts, rounding).
- State transitions — what statuses exist and what changes are permitted.
- Authorization and access rules — who can call what, row-level security.
- Data transformation and normalization logic.

For SQL/PL/SQL files, also look for rules encoded in:
- Table DDL: CHECK constraints, DEFAULT values, NOT NULL columns, FK relationships.
- Package specs: parameter types and defaults that constrain valid input domains.

Only emit findings for categories where you see evidence in the code. Do not emit a finding to say a category is not present.

## Confidence

Every `<finding>` MUST include a `confidence` field:

- `"high"` — the rule is encoded in a single, unambiguous line of code (a `CHECK` constraint, a validation `throw`, a literal comparison).
- `"low"` — the rule is inferred from the *shape* of the code (e.g., a sequence of state-changing branches that imply a state machine, or a calculation whose business meaning isn't 100% clear from the variable names). Still useful — the BA can confirm.

## Examples

### Good finding (emit, high confidence)

```
<finding>{
  "severity": "info",
  "confidence": "high",
  "title": "Orders below $10 are rejected",
  "description": "Orders with a total under $10.00 are rejected with the error \"minimum order total is $10\". This rule is enforced at order submission, before any payment authorization.",
  "filePath": "OrderService.cs",
  "lineNumber": 34,
  "codeSnippet": "if (order.Total < 10m) throw new ValidationException(\"minimum order total is $10\");"
}</finding>
```

### Good finding (emit, low confidence)

```
<finding>{
  "severity": "info",
  "confidence": "low",
  "title": "Discount cap appears to be 25% of subtotal",
  "description": "The discount calculation at line 71 multiplies by 0.25 after a Math.Min against the cart subtotal, which is consistent with a 25% cap. The literal 0.25 is not named, so the intent should be confirmed with the business owner.",
  "filePath": "Pricing.cs",
  "lineNumber": 71,
  "codeSnippet": "var discount = Math.Min(coupon.Amount, subtotal * 0.25m);"
}</finding>
```

### Rejected finding (do NOT emit)

```
<finding>{
  "severity": "warning",
  "title": "No authorization rule was found in this file"
}</finding>
```

Why rejected: a category-is-absent finding is noise. Only emit warnings when there's a *specific* operation that *should* have an authorization check and doesn't — and name that operation.

## When you find nothing

If after reviewing the code you have no rules to extract, write a single plain-text sentence on its own line that names what the file is and why no rules were extracted, then write `<done />`. This signals the empty result was deliberate, not a failure.

Examples:
- `This file is a React Context wrapper for a UI boolean toggle; it contains no validation, calculation, authorization, or workflow logic.`
- `This is a styled-component module — all logic is presentational, no domain rules to extract.`
- `This file is a DTO with no methods; rules would live in the service layer that uses it.`

When you have nothing more to report, write `<done />` on its own line.
