/**
 * CodeIntel Dev Days Demo — Cool Features Edition
 *
 * Run the app first:  dotnet run --project ..\src\CodeIntel.Server
 * Then in this folder: npm run demo           (normal speed)
 *                      npm run demo:slow      (presentation speed)
 *                      npm run demo:record    (saves a video to playwright-videos/)
 *
 * Edit DEMO_CONFIG below to match your machine before running.
 *
 * What this demo shows (in order):
 *   1. Welcome panel + file tree (folder checkbox picks whole project)
 *   2. File preview — click a line, see selection
 *   3. PIN TO ANALYSIS — line range becomes a chip on a free-text question
 *   4. TRACE FROM HERE — click a symbol, jump straight to a trace
 *   5. Live streaming with scan-beam animation + findings sidebar
 *   6. Save to repo + copy "#file:" reference for Copilot
 *   7. FINDINGS OVERLAY ON TRACE — bug rings auto-decorate the call graph
 *   8. Metrics tab — summary cards + sortable table
 */

import { test, type Page } from '@playwright/test';

// ── CONFIG ────────────────────────────────────────────────────────────────────

const DEMO_CONFIG = {
  solutionPath: 'C:\\GitRepos\\CodeIntel-DevDays\\CodeIntel-DevDays\\CodeIntel.sln',

  /** File to open in the preview pane for the "click a line" / "Trace from here" demos */
  previewFile: 'InvestigationOrchestrator.cs',

  /** Which line in `previewFile` to click — pick something with a method name on it */
  previewClickLine: 1,

  /** Files to add to selection for the Find Bugs run */
  analysisFiles: [
    'LlamaSharpService.cs',  // small-ish, one file keeps the run short
  ],

  analysisPreset: 'Find Bugs',

  /** Method name to use as a fallback if "Trace from here" doesn't work via click */
  traceFallbackEntryPoint: 'ReportWriter.WriteTraceAsync',

  /** Pause durations (ms) — increase for live presentation */
  pauses: {
    short:           1_500,   // brief pauses between actions
    afterLoad:       3_000,   // let audience see file tree
    afterClick:      2_500,   // let audience see the click effect
    beforeRun:       2_000,   // before clicking Run
    afterFirstToken: 6_000,   // let stream visibly run
    afterFindings:  10_000,   // let audience read findings
    afterTrace:      8_000,   // let audience see trace + overlay
    afterMetrics:    8_000,   // let audience read metrics table
  },
};

// ── HELPERS ───────────────────────────────────────────────────────────────────

async function waitForLlmReady(page: Page) {
  console.log('  Waiting for LLM...');
  await page.waitForFunction(
    () => !document.body.innerText.includes('loading model...'),
    { timeout: 120_000 },
  );
  console.log('  ✓ LLM ready');
}

async function selectFileByName(page: Page, fileName: string) {
  // Find the treeitem that contains this file name, then click its checkbox
  const treeitem = page.getByRole('treeitem', { name: new RegExp(fileName.replace('.', '\\.')) });
  await treeitem.waitFor({ state: 'visible', timeout: 15_000 });
  const checkbox = treeitem.locator('input[type="checkbox"]').first();
  await checkbox.click();
  console.log(`  ✓ Selected: ${fileName}`);
}

async function expandFolderInTree(page: Page, folderName: string) {
  // Find the treeitem for this folder and click its content area (not checkbox) to expand.
  // MUI TreeItem handles expand/collapse on content click; the Checkbox has stopPropagation.
  const treeitem = page.getByRole('treeitem', { name: folderName });
  await treeitem.waitFor({ state: 'visible', timeout: 10_000 });
  const expanded = await treeitem.getAttribute('aria-expanded');
  if (expanded !== 'true') {
    // Click the folder name text — this triggers MUI TreeItem expand without toggling the checkbox
    await treeitem.getByText(folderName, { exact: true }).click();
    await page.waitForTimeout(500);
  }
  console.log(`  ✓ Expanded folder: ${folderName}`);
}

async function openFileInPreview(page: Page, fileName: string) {
  // Click the file NAME (not checkbox) to open it in the preview pane
  const item = page.locator(`text="${fileName}"`).first();
  await item.waitFor({ state: 'visible', timeout: 15_000 });
  await item.click();
  console.log(`  ✓ Opened in preview: ${fileName}`);
}

async function switchMode(page: Page, mode: 'Analysis' | 'Trace' | 'Metrics') {
  // MUI ToggleButton with icon + text — match by role and exact accessible name
  const btn = page.getByRole('button', { name: mode, exact: true });
  await btn.click();
  console.log(`→ Switched to ${mode}`);
}

async function pause(page: Page, ms: number, reason: string) {
  console.log(`⏸  ${reason} (${ms / 1000}s)`);
  await page.waitForTimeout(ms);
}

async function clickPreviewLine(page: Page, lineNo: number, modifiers: ('Shift' | 'Control')[] = []) {
  // Lines in FilePreviewPanel render with data-line attributes
  const line = page.locator(`[data-line="${lineNo}"]`).first();
  await line.waitFor({ state: 'visible', timeout: 10_000 });
  await line.click({ modifiers });
  console.log(`  ✓ Clicked line ${lineNo}${modifiers.length ? ` with ${modifiers.join('+')}` : ''}`);
}

// ── DEMO ──────────────────────────────────────────────────────────────────────

test('CodeIntel presentation demo', async ({ page }) => {
  console.log('\n╔═══════════════════════════════════════════╗');
  console.log('║   CodeIntel  —  Cool Features Demo        ║');
  console.log('╚═══════════════════════════════════════════╝\n');

  await page.goto('/');
  await page.waitForLoadState('networkidle');

  // ── 1. LLM ready ─────────────────────────────────────────────────────────
  console.log('\n[ STEP 1 ] LLM warmup');
  await waitForLlmReady(page);

  // ── 2. Welcome panel ─────────────────────────────────────────────────────
  console.log('\n[ STEP 2 ] Welcome panel — onboarding flow');
  await pause(page, DEMO_CONFIG.pauses.short, 'Show welcome card');

  // ── 3. Load solution ─────────────────────────────────────────────────────
  console.log('\n[ STEP 3 ] Loading solution');
  const pathInput = page.getByPlaceholder('.sln, tsconfig.json, or project dir');
  await pathInput.click();
  await pathInput.fill(DEMO_CONFIG.solutionPath);
  await page.getByRole('button', { name: /Load Project/ }).click();
  await page.waitForSelector('text=/\\d+ projects? •/', { timeout: 60_000 });
  await pause(page, DEMO_CONFIG.pauses.afterLoad, 'Solution loaded — show file tree');

  // ── 4. Open a file in preview ────────────────────────────────────────────
  console.log('\n[ STEP 4 ] File preview — click a file in the tree');
  await expandFolderInTree(page, 'Services');
  await openFileInPreview(page, DEMO_CONFIG.previewFile);
  await pause(page, DEMO_CONFIG.pauses.afterClick, 'Preview pane opens with code');

  // ── 5. Click a line — show selection highlight ───────────────────────────
  console.log('\n[ STEP 5 ] Click a line → selection highlight + action toolbar');
  await clickPreviewLine(page, DEMO_CONFIG.previewClickLine);
  await pause(page, DEMO_CONFIG.pauses.afterClick, 'Selection toolbar appears: Pin / Trace from / Close');

  // ── 6. "Trace from here" — the magical UX ────────────────────────────────
  console.log('\n[ STEP 6 ] "Trace from here" — click symbol → jump to Trace mode');
  const traceFromBtn = page.getByRole('button', { name: /Trace from/ });
  if (await traceFromBtn.isVisible({ timeout: 3_000 }).catch(() => false)) {
    await traceFromBtn.click();
    console.log('  ✓ Clicked "Trace from here"');
    await pause(page, DEMO_CONFIG.pauses.short, 'Mode auto-switches to Trace with location chip');
  } else {
    console.log('  ⚠ Symbol click didn\'t resolve a word — falling back');
    await switchMode(page, 'Trace');
    const entryInput = page.getByPlaceholder(/OrderService|Namespace/i).first();
    await entryInput.fill(DEMO_CONFIG.traceFallbackEntryPoint);
  }

  // Configure trace: Callees, depth 1 (keeps it short)
  const calleesBtn = page.locator('button').filter({ hasText: /^Callees$/ }).first();
  if (await calleesBtn.isVisible().catch(() => false)) {
    await calleesBtn.click();
  }
  await pause(page, DEMO_CONFIG.pauses.beforeRun, 'About to run trace');

  // ── 7. Run trace ─────────────────────────────────────────────────────────
  console.log('\n[ STEP 7 ] Running trace — Mermaid diagram builds live');
  const runTrace = page.getByRole('button', { name: /^Run trace$/ });
  await runTrace.click();

  // Wait for graph: Mermaid SVG OR node-count text
  await Promise.race([
    page.waitForSelector('svg[id^="mermaid"], .mermaid svg', { timeout: 5 * 60_000 }),
    page.waitForFunction(
      () => /\d+ \/ \d+ synopses/.test(document.body.innerText) ||
            /\d+\/\d+ synopses/.test(document.body.innerText),
      { timeout: 5 * 60_000 },
    ),
    page.waitForTimeout(4 * 60_000),
  ]).catch(() => {});

  await pause(page, DEMO_CONFIG.pauses.afterTrace, 'Trace done — show Mermaid + node cards');

  // ── 8. Switch back to Analysis ───────────────────────────────────────────
  console.log('\n[ STEP 8 ] Back to Analysis mode');
  await switchMode(page, 'Analysis');
  await pause(page, DEMO_CONFIG.pauses.short, '');

  // ── 9. Select files for analysis ─────────────────────────────────────────
  console.log('\n[ STEP 9 ] Selecting files for the Find Bugs run');
  for (const fileName of DEMO_CONFIG.analysisFiles) {
    await selectFileByName(page, fileName);
    await page.waitForTimeout(600);
  }

  // ── 10. Pick preset ──────────────────────────────────────────────────────
  console.log('\n[ STEP 10 ] Picking preset');
  const presetCard = page.locator(`text="${DEMO_CONFIG.analysisPreset}"`).first();
  await presetCard.click();
  await pause(page, DEMO_CONFIG.pauses.beforeRun, 'Preset selected');

  // ── 11. Run analysis — scan beam + streaming + findings ──────────────────
  console.log('\n[ STEP 11 ] Run Analysis — scan beam + streaming tokens + findings sidebar');
  await page.getByRole('button', { name: /Run Analysis/ }).click();

  // First-token wait — show the cold-start panel
  await pause(page, DEMO_CONFIG.pauses.afterFirstToken, 'Cold-start panel + scan beam visible');

  // Wait for completion — chip says "Xs · N findings" when done
  await Promise.race([
    page.waitForFunction(
      () => /\d+\.?\d*s · \d+ findings?/.test(document.body.innerText),
      { timeout: 5 * 60_000 },
    ),
    page.waitForTimeout(4 * 60_000),
  ]).catch(() => {});

  await pause(page, DEMO_CONFIG.pauses.afterFindings, 'Findings cards visible — confidence chips, line numbers');

  // ── 12. Save to repo + copy #file: reference ─────────────────────────────
  console.log('\n[ STEP 12 ] Save to repo → copy "#file:" reference for Copilot');
  const saveBtn = page.getByRole('button', { name: /Save to repo/ });
  if (await saveBtn.isVisible().catch(() => false)) {
    await saveBtn.click();
    await pause(page, DEMO_CONFIG.pauses.short, 'Save panel slides in');

    // Click Save (default path)
    const saveConfirm = page.getByRole('button', { name: /^Save$/ });
    await saveConfirm.click();

    // Wait for the green "Saved to..." banner
    await page.waitForSelector('text=/Saved to/', { timeout: 30_000 }).catch(() => {});
    await pause(page, DEMO_CONFIG.pauses.short, 'Saved banner appears');

    // Click the "#file: reference" copy button
    const copyRefBtn = page.getByRole('button', { name: /#file:/ });
    if (await copyRefBtn.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await copyRefBtn.click();
      console.log('  ✓ Copied "#file:..." to clipboard');
      await pause(page, DEMO_CONFIG.pauses.short, 'Toast: "Copilot reference copied"');
    }
  }

  // ── 13. Switch back to Trace — FINDINGS OVERLAY auto-applies ─────────────
  console.log('\n[ STEP 13 ] ★ Switch to Trace → findings overlay auto-applies');
  await switchMode(page, 'Trace');
  await pause(page, DEMO_CONFIG.pauses.short, 'Trace view returns');

  // The overlay chip + bug rings on Mermaid nodes should now be visible
  // (TraceResultsView fetches the most-recent analysis on completion)
  await page.waitForSelector('text=/overlay:/', { timeout: 10_000 }).catch(() => {
    console.log('  (no overlay chip yet — overlay only triggers if findings match nodes by file)');
  });

  await pause(page, DEMO_CONFIG.pauses.afterTrace, 'Bug chips on node cards + rings on Mermaid graph');

  // ── 14. Metrics tab ──────────────────────────────────────────────────────
  console.log('\n[ STEP 14 ] Metrics — summary cards + sortable table');
  await switchMode(page, 'Metrics');

  // Wait for the table to populate (auto-loads on tab open)
  await page.waitForSelector('table tbody tr', { timeout: 60_000 }).catch(() => {});
  await pause(page, DEMO_CONFIG.pauses.short, 'Summary cards visible');

  // Sort by complexity (already default but click again to flip → flip back for visual)
  const ccHeader = page.locator('th').filter({ hasText: /^CC$/ }).first();
  if (await ccHeader.isVisible().catch(() => false)) {
    await ccHeader.click();
    await page.waitForTimeout(800);
    await ccHeader.click();
    console.log('  ✓ Sorted by cyclomatic complexity');
  }

  // Filter the table
  const filterInput = page.getByPlaceholder(/filter by name/i);
  if (await filterInput.isVisible().catch(() => false)) {
    await filterInput.click();
    await filterInput.type('high', { delay: 120 });
    console.log('  ✓ Filtered by "high" — high-complexity rows isolated');
  }

  await pause(page, DEMO_CONFIG.pauses.afterMetrics, 'Metrics table with flag chips visible');

  // ── Done ─────────────────────────────────────────────────────────────────
  console.log('\n╔═══════════════════════════════════════════╗');
  console.log('║   Demo complete  ✓                        ║');
  console.log('╚═══════════════════════════════════════════╝\n');
});
