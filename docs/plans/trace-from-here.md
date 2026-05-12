# CodeIntel — "Trace from here" from FilePreviewPanel

## Context

Today the trace flow starts by typing a method name (e.g. `OrderService.Submit`) in `TracePanel.tsx`. That works but is friction-heavy — the user already has the file open in the preview pane and can see exactly what they want to trace. The screenshot shows `BuildPrompt` selected on line 46 of `PromptTemplateService.cs`, with a "Pin to analysis" button visible. The natural next step is a sibling **"Trace from here"** button that:

1. Resolves the clicked symbol (location → declaration via the existing `getDefinition` flow)
2. Switches the analysis pane to **Trace** mode automatically
3. Pre-populates the entry point with the resolved location (file + line + character)
4. Leaves direction / depth at their defaults so the user just picks and hits Run

This closes the v2 follow-up on the trace v1 roadmap: the trace API already accepts a `TraceEntryPoint` with `{filePath, line, character}`, so the entire feature is frontend-only.

## Reuse map (from exploration)

| What I need | What exists | Where |
|---|---|---|
| Resolve clicked position → declared symbol | `getDefinition(workspaceId, file, line, character)` returns `DefinitionLocation { filePath, line, character, symbolName }` | [api/workspace.ts:34-48](src/CodeIntel.Server/ClientApp/src/api/workspace.ts), [WorkspaceService.FindDefinitionAsync](src/CodeIntel.Server/Services/RoslynWorkspaceService.cs#L289) — no backend changes |
| Trace entry-point with file+line+char | `TraceEntryPoint.{methodName, filePath, line, character}` + `TraceWalker.ResolveEntryPointAsync` location branch | [TraceModels.cs](src/CodeIntel.Server/Models/TraceModels.cs), [TraceWalker.cs](src/CodeIntel.Server/Services/TraceWalker.cs) — already wired |
| Word-at-offset extraction | `wordAtOffset(text, offset)` returning `{ word, charStart }` | [FilePreviewPanel.tsx:22-35](src/CodeIntel.Server/ClientApp/src/components/FilePreviewPanel.tsx#L22) |
| Sibling button + selection chip pattern | "Pin to analysis" button shown when `hasSelection`, pinning a `PinnedSnippet` to `workspaceStore.pinSnippet` | [FilePreviewPanel.tsx:121-186](src/CodeIntel.Server/ClientApp/src/components/FilePreviewPanel.tsx#L121) + [PromptSelector.tsx:184-191](src/CodeIntel.Server/ClientApp/src/components/PromptSelector.tsx#L184) |
| Pane mode toggle | Local `useState<'analysis'\|'trace'>` in `AnalysisPanel.tsx:25` | Needs to lift into a store to be triggered from outside |
| Trace input store | `useTraceStore.entryPointName` (string), `setEntryPointName` | [traceStore.ts](src/CodeIntel.Server/ClientApp/src/stores/traceStore.ts) — needs a new location-based field |
| Trace submit | `TracePanel.handleRun` builds `TraceRequest` from typed name | [TracePanel.tsx](src/CodeIntel.Server/ClientApp/src/components/TracePanel.tsx) — needs a location-based branch |

## Approach

### 1. Lift `paneMode` into `workspaceStore`

`AnalysisPanel.tsx` owns `paneMode` as local `useState` today. Move it into [stores/workspaceStore.ts](src/CodeIntel.Server/ClientApp/src/stores/workspaceStore.ts) (which already carries UI-ish state like `pinnedSnippet`, `previewedFile`, `selectedFiles`):

```ts
paneMode: 'analysis' | 'trace';
setPaneMode: (mode: 'analysis' | 'trace') => void;
```

`AnalysisPanel.tsx` reads/writes from the store instead of local state. The mode toggle keeps working identically.

### 2. Add a location-based entry-point to `traceStore`

[stores/traceStore.ts](src/CodeIntel.Server/ClientApp/src/stores/traceStore.ts) extension:

```ts
// New
entryPointLocation: TraceEntryPointLocation | null;
setEntryPointLocation: (loc: TraceEntryPointLocation | null) => void;

// New type (export from this file or types/index.ts)
interface TraceEntryPointLocation {
  filePath: string;
  line: number;
  character: number;
  // For display only; not sent to backend (the backend re-resolves via Roslyn).
  symbolLabel: string;       // e.g. "BuildPrompt"
  fileShortName: string;     // e.g. "PromptTemplateService.cs"
}
```

`startRun` and `reset` clear `entryPointLocation` alongside the other run-state fields.

### 3. `FilePreviewPanel`: capture clicked symbol + add the button

Two small edits to [components/FilePreviewPanel.tsx](src/CodeIntel.Server/ClientApp/src/components/FilePreviewPanel.tsx):

**a. Capture the click offset alongside line selection.** Currently `handleLineClick` stores only line numbers in `selStart`/`selEnd`. Extend with:

```ts
const [clickSymbol, setClickSymbol] = useState<
  { line: number; character: number; word: string } | null
>(null);

// in handleLineClick (regular-click branch, after selectLine):
const lineText = lines[lineNo - 1] ?? '';
const offset = caretCharOffset(e);
if (offset !== null) {
  const w = wordAtOffset(lineText, offset);
  if (w) setClickSymbol({ line: lineNo, character: w.charStart, word: w.word });
  else setClickSymbol(null);
}
// Clear on shift+click (range selection) since the "current word" is ambiguous.
```

`clickSymbol` is cleared in `clearSelection()` and on shift+click range extension.

**b. Render a sibling "Trace from here" button** in the existing selection chip block (lines ~137–186), placed right after the "Pin to analysis" button:

```tsx
<Button
  size="small"
  variant="outlined"
  startIcon={<AccountTreeIcon sx={{ fontSize: 14 }} />}
  onClick={handleTraceFromHere}
  disabled={!clickSymbol || tracing}
>
  Trace from `{clickSymbol?.word ?? '…'}`
</Button>
```

The handler:

```ts
async function handleTraceFromHere() {
  if (!clickSymbol || !workspaceId) return;
  setTracing(true);
  try {
    // Resolve to the declaration so a click on a usage still works.
    const def = await getDefinition(workspaceId, absolutePath, clickSymbol.line, clickSymbol.character);
    const loc = def
      ? { filePath: def.filePath, line: def.line, character: def.character, symbolLabel: def.symbolName, fileShortName: baseName(def.filePath) }
      : { filePath: absolutePath, line: clickSymbol.line, character: clickSymbol.character, symbolLabel: clickSymbol.word, fileShortName: baseName(absolutePath) };
    useTraceStore.getState().setEntryPointLocation(loc);
    useWorkspaceStore.getState().setPaneMode('trace');
    clearSelection();
  } catch {
    // Fallback: use the clicked position even without resolution.
    useTraceStore.getState().setEntryPointLocation({
      filePath: absolutePath,
      line: clickSymbol.line,
      character: clickSymbol.character,
      symbolLabel: clickSymbol.word,
      fileShortName: baseName(absolutePath),
    });
    useWorkspaceStore.getState().setPaneMode('trace');
    clearSelection();
  } finally {
    setTracing(false);
  }
}
```

### 4. `TracePanel`: render the location chip + use it on submit

In [components/TracePanel.tsx](src/CodeIntel.Server/ClientApp/src/components/TracePanel.tsx):

- Read `entryPointLocation` from the trace store alongside `entryPointName`.
- When `entryPointLocation` is set, replace the `TextField` with a read-only chip:

```tsx
{entryPointLocation ? (
  <Stack ...>
    <Chip
      icon={<AccountTreeIcon sx={{ fontSize: 14 }} />}
      label={`${entryPointLocation.fileShortName}:${entryPointLocation.line}  ·  ${entryPointLocation.symbolLabel}`}
      onDelete={() => setEntryPointLocation(null)}
      sx={{ fontFamily: '"JetBrains Mono", monospace' }}
    />
    <Typography variant="caption" color="text.secondary">
      Trace will run against the declaration at this location. Clear the chip to type a name instead.
    </Typography>
  </Stack>
) : (
  <TextField {/* existing typed-name input */} />
)}
```

- `handleRun` picks the entry-point shape from whichever is set:

```ts
const entryPoint: TraceEntryPoint = entryPointLocation
  ? { methodName: null, filePath: entryPointLocation.filePath, line: entryPointLocation.line, character: entryPointLocation.character }
  : { methodName: entryPointName.trim(), filePath: null, line: null, character: null };
```

The `canRun` predicate is updated to allow either a non-empty typed name OR a set location.

### 5. (Optional polish) Show "Trace from here" only when a single line is selected

If `selStart !== selEnd` (range selection for pinning), hide the trace button — symbol-from-click only makes sense for a single line.

## Critical Files

| Change | File |
|---|---|
| Add `paneMode` + `setPaneMode` | [src/CodeIntel.Server/ClientApp/src/stores/workspaceStore.ts](src/CodeIntel.Server/ClientApp/src/stores/workspaceStore.ts) |
| Add `entryPointLocation` + `setEntryPointLocation` (clear on `startRun`/`reset`) | [src/CodeIntel.Server/ClientApp/src/stores/traceStore.ts](src/CodeIntel.Server/ClientApp/src/stores/traceStore.ts) |
| Read pane mode from store instead of `useState` | [src/CodeIntel.Server/ClientApp/src/components/AnalysisPanel.tsx](src/CodeIntel.Server/ClientApp/src/components/AnalysisPanel.tsx) |
| Capture `clickSymbol`, add "Trace from here" button + handler | [src/CodeIntel.Server/ClientApp/src/components/FilePreviewPanel.tsx](src/CodeIntel.Server/ClientApp/src/components/FilePreviewPanel.tsx) |
| Render location chip when `entryPointLocation` set; submit accordingly | [src/CodeIntel.Server/ClientApp/src/components/TracePanel.tsx](src/CodeIntel.Server/ClientApp/src/components/TracePanel.tsx) |

No backend changes — `TraceWalker.ResolveEntryPointAsync`'s location branch already handles `{filePath, line, character}` entries, and `getDefinition` already returns the declaration's `symbolName`.

## Verification

1. Build: `dotnet build src\CodeIntel.Server` clean; `npx tsc --noEmit` in `ClientApp` clean.
2. Start server, load `CodeIntel.sln`.
3. Open `PromptTemplateService.cs` in the file preview pane.
4. Click on the word `BuildPrompt` on line 46.
5. Confirm the line is selected (existing behavior) **and** a new "Trace from `BuildPrompt`" button appears next to "Pin to analysis."
6. Click "Trace from `BuildPrompt`". Expect:
   - Pane switches to **Trace** mode.
   - TracePanel shows a chip: `PromptTemplateService.cs:46 · BuildPrompt` (with a delete X).
   - Run button is enabled; direction defaults to **Callers**; depth defaults to **2**.
7. Run the trace. Expect a graph of who calls `BuildPrompt` (`InvestigationOrchestrator`, `AnalysisOrchestrator`, possibly others), each with a synopsis. Mermaid renders inline.
8. Clear the chip (X) and confirm the typed-name `TextField` returns and an empty state disables Run.
9. Click a usage of `BuildPrompt` in a *different* file (e.g. `InvestigationOrchestrator.RunAsync` calls `_promptService.BuildPrompt(...)`). Confirm "Trace from here" still resolves to the declaration in `PromptTemplateService.cs` (via the `getDefinition` step), not the usage location.
10. Switch back to **Analysis** mode → confirm `PromptSelector` / `ResultsView` is unaffected and the existing "Pin to analysis" still pins from a multi-line selection.

## Out of scope

- Right-click context menu for "Trace from here" (current plan uses an inline button — simpler and discoverable).
- Per-direction defaults persisted per location (everyone gets the same default `Callers` for now).
- Multi-symbol selection (one symbol per click).
- Resolving fully-qualified names without round-tripping through `getDefinition` (the call adds ~10ms; not worth optimizing).
