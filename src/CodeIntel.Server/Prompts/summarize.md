Analyze the code below and produce a plain-English summary of what it does.

Write the summary as readable prose — do NOT wrap the summary in <finding> tags.

Structure your response as:
1. A one-paragraph overview of the overall purpose.
2. Key responsibilities of each major class, module, or service.
3. Notable patterns, dependencies, or design decisions.

Only emit a <finding> block if you spot something genuinely worth flagging separately
(e.g. a critical bug or an important design concern) — not for the summary itself.

If you do emit a finding, it MUST include a `confidence` field (`"high"` or `"low"`).

Keep the summary concise — a developer unfamiliar with this code should understand it
in 2–3 minutes.

## Examples

### Good summary opening (emit prose like this)

> This file implements `OrderService`, a server-side coordinator that validates submitted orders, applies pricing rules, and persists them via `IOrderRepository`. It does not handle payment authorization directly — that is delegated to `PaymentGateway` invoked from `Submit()`. The class is registered as a scoped service in `Program.cs` and is the only caller of `IOrderRepository.Insert`.

Why it works: concrete names, names the boundaries (what it does, what it delegates), and a fact about wiring that orients a new reader fast.

### Weak summary (do NOT write this)

> This code is a service that does various things related to orders. It might handle some validation and possibly talks to a database. There are several methods that could be involved in processing.

Why it fails: vague verbs ("does various things"), hedging ("might", "possibly"), no concrete names. Useless to a new developer.

### Finding only when genuinely worth flagging

```
<finding>{
  "severity": "bug",
  "confidence": "high",
  "title": "OrderService.Submit swallows PaymentException",
  "description": "When PaymentGateway.Charge throws PaymentException, the catch on line 92 logs and returns Ok(), so callers cannot distinguish a successful charge from a silently-failed one.",
  "filePath": "OrderService.cs",
  "lineNumber": 92,
  "codeSnippet": "catch (PaymentException ex) { _logger.LogError(ex, \"charge failed\"); return Ok(); }"
}</finding>
```

Write `<done />` on its own line when finished.
