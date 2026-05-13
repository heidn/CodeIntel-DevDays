Analyze the code below for dead code — methods, classes, properties, or variables that
are defined but never called or referenced within the provided files.

For each dead code element, emit a <finding> with severity `"deadcode"`. Focus on:
- Private/internal functions with no callers in the provided files.
- Variables or fields assigned but never read.
- Unreachable code paths (after unconditional return/throw).
- Types with no usages.

Only report what you can confirm from the provided code. Do not speculate.

## Confidence

Every `<finding>` MUST include a `confidence` field:

- `"high"` — the symbol is `private` (visibility limited to the file) and has zero references in the code you can see. There is no plausible external caller.
- `"low"` — the symbol is `internal`, `public`, or `protected`, OR it has an attribute suggesting reflection / DI / serialization use (e.g., `[JsonProperty]`, `[Route]`, `[Test]`, controller actions, interface implementations). Emit so a reviewer checks; do not delete on autopilot.

## What is NOT a finding

- A `public` method on a `public` class with no in-repo callers — it's likely part of an API surface. Skip unless you can otherwise confirm it's unused.
- An override of a base method or an interface implementation — required by the contract even if unused locally.
- A method named `Main`, `Configure`, `ConfigureServices`, `Startup`, or matching a known framework hook — convention-called.
- A field marked `[Obsolete]` — already flagged for removal by the author.

## Examples

### Good finding (emit, high confidence)

```
<finding>{
  "severity": "deadcode",
  "confidence": "high",
  "title": "Private helper FormatLegacyKey is never called",
  "description": "OrderRepository.FormatLegacyKey is declared private at line 88 and is not invoked anywhere in OrderRepository.cs or any other file in this scope. Safe to delete.",
  "filePath": "OrderRepository.cs",
  "lineNumber": 88,
  "codeSnippet": "private static string FormatLegacyKey(int orderId) => $\"ORDER-{orderId:D8}\";"
}</finding>
```

### Good finding (emit, low confidence)

```
<finding>{
  "severity": "deadcode",
  "confidence": "low",
  "title": "Public method ExportLegacy has no in-repo callers",
  "description": "OrdersController.ExportLegacy on line 142 has no callers in the analyzed code, but it is decorated with [HttpGet(\"export-legacy\")]. It may still be reachable as an HTTP endpoint. Verify against route table before removing.",
  "filePath": "OrdersController.cs",
  "lineNumber": 142,
  "codeSnippet": "[HttpGet(\"export-legacy\")] public IActionResult ExportLegacy()"
}</finding>
```

### Rejected finding (do NOT emit)

```
<finding>{
  "severity": "deadcode",
  "title": "Configure method appears unused"
}</finding>
```

Why rejected: `Configure` is called by the ASP.NET Core host via reflection. Convention-called methods are not dead code.

When you have nothing more to report, write `<done />` on its own line.
