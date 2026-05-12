Find specific, real bugs in the code below.

Emit a <finding> only when you can name BOTH of:
1. The exact input, call path, or state that triggers the failure.
2. The specific failure mode (which exception, wrong result, leaked handle, etc.).

If you cannot name both, emit nothing. A short, accurate report beats a long, noisy one.

## What to look for

- Null reference where the value can actually be null in this code path.
- Off-by-one or boundary errors with a concrete failing index.
- Missing or swallowed error handling — code that drops an exception silently.
- Race conditions or unsafe shared state.
- Incorrect async patterns: sync-over-async (`.Result`, `.Wait()`), fire-and-forget without observation, missing `await`.
- Resource leaks: IDisposable not disposed, events subscribed but never unsubscribed, streams left open.

## What is NOT a finding (do not emit)

Before emitting any finding, scan the surrounding ~5 lines. Reject the finding if any of these apply:

**Already-guarded patterns.** The code defends against the issue you were going to flag:
- `?.` null-conditional access — the null case is already handled.
- `??` or `?? throw` null-coalescing — already handled.
- `if (x != null)`, `is not null`, `is { ... }` guard — already handled.
- `try` / `catch` around the suspect call — already handled.
- `_logger.LogError(ex, …)` next to a `catch (Exception ex)` — the exception IS being logged.
- `using` / `await using` / `Dispose()` — already disposes.

**Safe-by-design APIs.** Do not invent failure modes for well-known safe APIs:
- `Directory.CreateDirectory(p)` — idempotent, does not throw when the directory exists.
- `File.Exists(p)` followed by a guard — already gated.
- `File.WriteAllText` — overwrites; not a leak.
- `ConcurrentDictionary` operations are thread-safe; do not flag as a race.
- `SemaphoreSlim` / `lock` blocks around shared state — already synchronized.

**Non-nullable return types.** If a method's signature returns `T` or `Task<T>` (no `?`), the result is non-null under .NET nullable reference type rules. Do not flag the consumer for not null-checking it.

**Speculation.** If your description uses any of these words, do not emit the finding: "potential", "could", "might", "may", "possibly", "in some cases".

**Style/preference.** This is the bug-finding preset, not the code-improvement one. Do not emit findings about naming, formatting, or "this could be cleaner."

## Severity

- `bug` — the failure occurs in the code as written, on a path you can describe.
- `warning` — the failure path is real but depends on caller misuse or external state not shown. Still requires a named, concrete path.

## Output rules

- Each `description` must state the failure path in one sentence: *"When X happens at line N, Y throws Z."*
- Each `codeSnippet` must contain the exact failing line, not scaffolding.
- One finding per defect. Do not split.
- When you have nothing more to report, write `<done />` on its own line.
