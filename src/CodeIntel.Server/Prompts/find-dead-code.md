Analyze the code below for dead code — methods, classes, properties, or variables that
are defined but never called or referenced within the provided files.

For each dead code element, emit a <finding> with severity "deadcode". Focus on:
- Private/internal functions with no callers in the provided files
- Variables or fields assigned but never read
- Unreachable code paths (after unconditional return/throw)
- Types with no usages

Only report what you can confirm from the provided code. Do not speculate.
