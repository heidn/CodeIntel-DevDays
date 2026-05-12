Analyze the code below and extract business rules embedded in it.

Use these severity levels:
- "info" — a rule you found explicitly in the code
- "warning" — a rule you expected to find but is absent or incomplete (e.g. no auth check before a sensitive operation, no validation on a critical input)

For each finding:
- `codeSnippet`: quote the specific line that encodes the rule. If no single line applies, omit the field entirely — do not reuse an unrelated snippet as a placeholder.
- `description`: plain English so a business analyst can understand the rule without reading the code.

Focus on:
- Input validation and domain constraints (valid/invalid values, NOT NULL, CHECK constraints, column types)
- Calculation formulas and business math (totals, taxes, discounts, rounding)
- State transitions — what statuses exist and what changes are permitted
- Authorization and access rules — who can call what, row-level security
- Data transformation and normalization logic

For SQL/PL/SQL files, also look for rules encoded in:
- Table DDL: CHECK constraints, DEFAULT values, NOT NULL columns, FK relationships
- Package specs: parameter types and defaults that constrain valid input domains

Only emit findings for categories where you see evidence in the code. Do not emit a finding to say a category is not present.
