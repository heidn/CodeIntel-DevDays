import type { AnalysisResult, Finding, Severity, TraceNode } from '../types';

/**
 * Maps each trace-node id to the analysis findings that fall inside its body span.
 *
 * Match rule: same file (case-insensitive, separator-normalized) AND the finding's
 * lineNumber lies inside [node.line, node.line + lineCount(bodySnippet) - 1].
 *
 * Findings without a file path or line number are skipped — they can't be anchored
 * to a specific node, and showing them on every node would create false signal.
 */
export function matchFindingsToNodes(
  nodes: TraceNode[],
  findings: Finding[],
): Map<string, Finding[]> {
  const out = new Map<string, Finding[]>();
  if (!nodes.length || !findings.length) return out;

  // Pre-compute body spans once per node.
  const spans = nodes.map((n) => {
    if (!n.filePath || n.line == null) return null;
    const lines = n.bodySnippet ? n.bodySnippet.split('\n').length : 1;
    return {
      id: n.id,
      file: normalizePath(n.filePath),
      lineStart: n.line,
      lineEnd: n.line + Math.max(lines, 1) - 1,
    };
  });

  for (const f of findings) {
    if (!f.filePath || f.lineNumber == null) continue;
    const fFile = normalizePath(f.filePath);
    const fLine = f.lineNumber;
    for (const span of spans) {
      if (!span) continue;
      if (span.file !== fFile) continue;
      if (fLine < span.lineStart || fLine > span.lineEnd) continue;
      const list = out.get(span.id);
      if (list) list.push(f);
      else out.set(span.id, [f]);
    }
  }

  return out;
}

/**
 * Appends classDef + class statements to the base Mermaid source so nodes with
 * findings get a severity-coloured outline. Highest-severity match wins per node.
 *
 * The base graph (entry-point purple, DB/HTTP shapes, dashed back-edges) is
 * preserved — we only add new classDefs at the bottom.
 */
export function decorateMermaidWithFindings(
  baseMermaid: string,
  findingsByNode: Map<string, Finding[]>,
): string {
  if (findingsByNode.size === 0) return baseMermaid;

  const buckets: Record<Severity, string[]> = {
    bug:        [],
    warning:    [],
    deadCode:   [],
    suggestion: [],
    info:       [],
  };
  for (const [nodeId, list] of findingsByNode) {
    const top = list.reduce((a, b) => (SEVERITY_RANK[b.severity] > SEVERITY_RANK[a.severity] ? b : a));
    buckets[top.severity].push(nodeId);
  }

  const lines: string[] = [];
  // Stroke-only style so we don't fight the existing DB/HTTP fills. Bug gets the
  // strongest ring; suggestion/info get a softer one.
  if (buckets.bug.length) {
    lines.push('  classDef findBug stroke:#ef4444,stroke-width:3px');
    lines.push(`  class ${buckets.bug.join(',')} findBug`);
  }
  if (buckets.warning.length) {
    lines.push('  classDef findWarn stroke:#f59e0b,stroke-width:3px');
    lines.push(`  class ${buckets.warning.join(',')} findWarn`);
  }
  if (buckets.deadCode.length) {
    lines.push('  classDef findDead stroke:#a855f7,stroke-width:3px,stroke-dasharray:4 2');
    lines.push(`  class ${buckets.deadCode.join(',')} findDead`);
  }
  if (buckets.suggestion.length) {
    lines.push('  classDef findSug stroke:#38bdf8,stroke-width:2px');
    lines.push(`  class ${buckets.suggestion.join(',')} findSug`);
  }
  if (buckets.info.length) {
    lines.push('  classDef findInfo stroke:#94a3b8,stroke-width:2px');
    lines.push(`  class ${buckets.info.join(',')} findInfo`);
  }

  return baseMermaid.trimEnd() + '\n' + lines.join('\n') + '\n';
}

function normalizePath(p: string): string {
  return p.replace(/\\/g, '/').toLowerCase();
}

export const SEVERITY_RANK: Record<Severity, number> = {
  info:       0,
  suggestion: 1,
  deadCode:   2,
  warning:    3,
  bug:        4,
};
