import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Box,
  Stack,
  Typography,
  Button,
  Chip,
  Paper,
  Alert,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TableSortLabel,
  TextField,
  IconButton,
  Tooltip,
  CircularProgress,
} from '@mui/material';
import RefreshIcon from '@mui/icons-material/RefreshOutlined';
import LaunchIcon from '@mui/icons-material/LaunchOutlined';
import { useWorkspaceStore } from '../stores/workspaceStore';
import { computeMetrics } from '../api/metrics';
import { openInVsCode } from '../utils/openInVsCode';
import type { MethodMetric, WorkspaceMetricsResult } from '../types';

type SortKey =
  | 'cyclomaticComplexity'
  | 'lengthLines'
  | 'nestingDepth'
  | 'parameterCount'
  | 'name'
  | 'file';

interface Row extends MethodMetric {
  filePath: string;
  relativePath: string;
}

const FLAG_COLORS: Record<string, string> = {
  'high-complexity':       '#dc2626',
  'long':                  '#ca8a04',
  'empty-catch':           '#dc2626',
  'sync-over-async':       '#ca8a04',
  'swallowed-when-others': '#dc2626',
  'many-cursors':          '#ca8a04',
  'many-params':           '#0284c7',
  'deep-nesting':          '#ca8a04',
  'spec-only':             '#64748b',
};

function languageLabel(lang: string): string {
  switch (lang) {
    case 'cSharp':     return 'C#';
    case 'typeScript': return 'TypeScript';
    case 'java':       return 'Java';
    case 'sql':        return 'PL/SQL';
    default:           return lang;
  }
}

export default function MetricsPanel() {
  const workspace      = useWorkspaceStore((s) => s.workspace);
  const selectedFiles  = useWorkspaceStore((s) => s.selectedFiles);

  const [loading, setLoading]   = useState(false);
  const [error, setError]       = useState<string | null>(null);
  const [result, setResult]     = useState<WorkspaceMetricsResult | null>(null);
  const [sortKey, setSortKey]   = useState<SortKey>('cyclomaticComplexity');
  const [sortDir, setSortDir]   = useState<'asc' | 'desc'>('desc');
  const [filter, setFilter]     = useState('');

  const load = useCallback(async () => {
    if (!workspace) return;
    setLoading(true);
    setError(null);
    try {
      const paths = selectedFiles.size > 0 ? Array.from(selectedFiles) : null;
      const r = await computeMetrics(workspace.id, paths);
      setResult(r);
    } catch (err: unknown) {
      const msg =
        (err as { response?: { data?: { error?: string } } })?.response?.data?.error ??
        (err instanceof Error ? err.message : 'Failed to compute metrics');
      setError(msg);
    } finally {
      setLoading(false);
    }
  }, [workspace, selectedFiles]);

  // Lazy load on first mount (when tab opens).
  useEffect(() => {
    if (workspace && !result && !loading) {
      void load();
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workspace?.id]);

  const allRows: Row[] = useMemo(() => {
    if (!result) return [];
    return result.files.flatMap((f) =>
      f.methods.map((m) => ({ ...m, filePath: f.filePath, relativePath: f.relativePath }))
    );
  }, [result]);

  const filteredSortedRows = useMemo(() => {
    let rows = allRows;
    if (filter.trim()) {
      const q = filter.trim().toLowerCase();
      rows = rows.filter(
        (r) =>
          r.name.toLowerCase().includes(q) ||
          r.relativePath.toLowerCase().includes(q) ||
          r.container.toLowerCase().includes(q) ||
          r.flags.some((f) => f.toLowerCase().includes(q))
      );
    }
    const dir = sortDir === 'asc' ? 1 : -1;
    return [...rows].sort((a, b) => {
      switch (sortKey) {
        case 'name':           return dir * a.name.localeCompare(b.name);
        case 'file':           return dir * a.relativePath.localeCompare(b.relativePath);
        case 'lengthLines':    return dir * (a.lengthLines - b.lengthLines);
        case 'nestingDepth':   return dir * (a.nestingDepth - b.nestingDepth);
        case 'parameterCount': return dir * (a.parameterCount - b.parameterCount);
        case 'cyclomaticComplexity':
        default:               return dir * (a.cyclomaticComplexity - b.cyclomaticComplexity);
      }
    });
  }, [allRows, sortKey, sortDir, filter]);

  function flipSort(key: SortKey) {
    if (sortKey === key) {
      setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      setSortKey(key);
      setSortDir('desc');
    }
  }

  if (!workspace) {
    return (
      <Box sx={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', p: 4 }}>
        <Typography variant="body2" color="text.secondary">
          Load a workspace to view metrics.
        </Typography>
      </Box>
    );
  }

  const isSql = workspace.language === 'sql';

  return (
    <Box sx={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0, bgcolor: 'background.paper' }}>
      {/* Header */}
      <Stack
        direction="row"
        sx={{ px: 3, py: 1.5, borderBottom: '1px solid', borderColor: 'divider', alignItems: 'center', gap: 1, flexShrink: 0 }}
      >
        <Typography variant="overline" color="text.secondary">Metrics</Typography>
        {loading && <CircularProgress size={14} />}
        {result && !loading && (
          <Chip
            size="small"
            label={`${result.summary.fileCount} files · ${result.summary.methodCount} routines`}
            sx={{ bgcolor: 'rgba(79,70,229,0.08)', color: 'primary.main', fontFamily: '"JetBrains Mono", monospace' }}
          />
        )}
        {selectedFiles.size > 0 && (
          <Typography variant="caption" color="text.secondary" sx={{ fontFamily: '"JetBrains Mono", monospace' }}>
            scope: {selectedFiles.size} selected
          </Typography>
        )}
        <Box sx={{ flex: 1 }} />
        <TextField
          size="small"
          placeholder="filter by name / file / flag"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          sx={{
            width: 240,
            '& input': { fontFamily: '"JetBrains Mono", monospace', fontSize: '0.75rem', py: 0.5 },
          }}
        />
        <Tooltip title="Recompute">
          <span>
            <IconButton size="small" onClick={load} disabled={loading}>
              <RefreshIcon sx={{ fontSize: 16 }} />
            </IconButton>
          </span>
        </Tooltip>
      </Stack>

      {error && (
        <Alert severity="error" sx={{ m: 2, fontSize: '0.8125rem' }}>
          {error}
        </Alert>
      )}

      {/* Unsupported-language placeholder — distinguishes "no analyzer" from "0 methods found" */}
      {result && !result.supported && (
        <Alert severity="info" sx={{ m: 2, fontSize: '0.8125rem' }}>
          Metrics are not yet implemented for {languageLabel(result.language)}. Structural metrics
          (cyclomatic complexity, nesting depth, method length, parameter count) are currently
          computed for C# via Roslyn and PL/SQL via ANTLR. TypeScript / Java support is on the roadmap.
        </Alert>
      )}

      {/* Summary stat cards */}
      {result && result.supported && (
        <Box sx={{ px: 3, py: 1.5, borderBottom: '1px solid', borderColor: 'divider', flexShrink: 0 }}>
          <Stack direction="row" spacing={1.5} sx={{ flexWrap: 'wrap', gap: 1 }}>
            <SummaryCard label="High complexity (≥10)" value={result.summary.highComplexityCount} accent="#dc2626" />
            <SummaryCard label="Long methods (≥50 lines)" value={result.summary.longMethodCount} accent="#ca8a04" />
            {!isSql && (
              <>
                <SummaryCard label="Empty catches" value={result.summary.emptyCatchCount} accent="#dc2626" />
                <SummaryCard label="Sync-over-async" value={result.summary.syncOverAsyncCount} accent="#ca8a04" />
              </>
            )}
            {isSql && (
              <>
                <SummaryCard label="Cursor declarations" value={result.summary.cursorTotal} accent="#0284c7" />
                <SummaryCard label="Swallowed WHEN OTHERS" value={result.summary.swallowedWhenOthersTotal} accent="#dc2626" />
              </>
            )}
          </Stack>
        </Box>
      )}

      {/* Methods table */}
      <Box sx={{ flex: 1, overflow: 'auto', minHeight: 0 }}>
        {!result && !loading && !error && (
          <Box sx={{ p: 4, color: 'text.secondary' }}>
            <Typography variant="body2">Metrics will load when you open this tab.</Typography>
          </Box>
        )}
        {result && result.supported && filteredSortedRows.length === 0 && !loading && (
          <Box sx={{ p: 4, color: 'text.secondary' }}>
            <Typography variant="body2">
              {allRows.length === 0
                ? 'No methods or routines found in the scoped files.'
                : 'No methods match the current filter.'}
            </Typography>
          </Box>
        )}
        {result && result.supported && filteredSortedRows.length > 0 && (
          <TableContainer>
            <Table size="small" stickyHeader>
              <TableHead>
                <TableRow>
                  <SortableHeader label="Method" sortKey="name"          current={sortKey} dir={sortDir} onClick={flipSort} />
                  <SortableHeader label="File"   sortKey="file"          current={sortKey} dir={sortDir} onClick={flipSort} />
                  <SortableHeader label="CC"     sortKey="cyclomaticComplexity" current={sortKey} dir={sortDir} onClick={flipSort} align="right" tip="Cyclomatic complexity" />
                  <SortableHeader label="Lines"  sortKey="lengthLines"   current={sortKey} dir={sortDir} onClick={flipSort} align="right" />
                  <SortableHeader label="Depth"  sortKey="nestingDepth"  current={sortKey} dir={sortDir} onClick={flipSort} align="right" tip="Max nesting depth" />
                  <SortableHeader label="Params" sortKey="parameterCount" current={sortKey} dir={sortDir} onClick={flipSort} align="right" />
                  <TableCell sx={{ fontWeight: 600, fontSize: '0.7rem', textTransform: 'uppercase' }}>Flags</TableCell>
                  <TableCell sx={{ width: 36 }} />
                </TableRow>
              </TableHead>
              <TableBody>
                {filteredSortedRows.map((row, i) => (
                  <MethodRow key={`${row.filePath}-${row.startLine}-${i}`} row={row} />
                ))}
              </TableBody>
            </Table>
          </TableContainer>
        )}
      </Box>
    </Box>
  );
}

function SummaryCard({ label, value, accent }: { label: string; value: number; accent: string }) {
  return (
    <Paper
      elevation={0}
      sx={{
        px: 1.5,
        py: 1,
        minWidth: 140,
        border: '1px solid',
        borderColor: 'divider',
        bgcolor: 'background.default',
        position: 'relative',
        '&::before': {
          content: '""', position: 'absolute', left: 0, top: 0, bottom: 0, width: 3, bgcolor: accent, borderRadius: '4px 0 0 4px',
        },
      }}
    >
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', fontSize: '0.65rem', textTransform: 'uppercase', letterSpacing: '0.05em' }}>
        {label}
      </Typography>
      <Typography sx={{ fontFamily: '"JetBrains Mono", monospace', fontWeight: 600, fontSize: '1.1rem', color: value > 0 ? accent : 'text.primary' }}>
        {value}
      </Typography>
    </Paper>
  );
}

function SortableHeader({
  label, sortKey, current, dir, onClick, align, tip,
}: {
  label: string;
  sortKey: SortKey;
  current: SortKey;
  dir: 'asc' | 'desc';
  onClick: (k: SortKey) => void;
  align?: 'right';
  tip?: string;
}) {
  const cell = (
    <TableCell
      align={align}
      sx={{ fontWeight: 600, fontSize: '0.7rem', textTransform: 'uppercase', cursor: 'pointer', whiteSpace: 'nowrap' }}
    >
      <TableSortLabel
        active={current === sortKey}
        direction={current === sortKey ? dir : 'asc'}
        onClick={() => onClick(sortKey)}
      >
        {label}
      </TableSortLabel>
    </TableCell>
  );
  return tip ? <Tooltip title={tip}>{cell}</Tooltip> : cell;
}

function MethodRow({ row }: { row: Row }) {
  const ccColor = row.cyclomaticComplexity >= 10 ? '#dc2626' : row.cyclomaticComplexity >= 6 ? '#ca8a04' : 'text.primary';
  const lenColor = row.lengthLines >= 50 ? '#ca8a04' : 'text.primary';

  return (
    <TableRow hover sx={{ '& .MuiTableCell-root': { fontSize: '0.78rem', py: 0.5 } }}>
      <TableCell sx={{ fontFamily: '"JetBrains Mono", monospace' }}>
        <Box>
          <Typography variant="caption" sx={{ display: 'block', color: 'text.secondary', fontSize: '0.65rem' }}>
            {row.container}
          </Typography>
          <Typography sx={{ fontFamily: 'inherit', fontSize: 'inherit' }}>{row.name}</Typography>
        </Box>
      </TableCell>
      <TableCell sx={{ fontFamily: '"JetBrains Mono", monospace', color: 'text.secondary', fontSize: '0.7rem' }}>
        {row.relativePath}:{row.startLine}
      </TableCell>
      <TableCell align="right" sx={{ fontFamily: '"JetBrains Mono", monospace', color: ccColor, fontWeight: 600 }}>
        {row.cyclomaticComplexity}
      </TableCell>
      <TableCell align="right" sx={{ fontFamily: '"JetBrains Mono", monospace', color: lenColor }}>
        {row.lengthLines}
      </TableCell>
      <TableCell align="right" sx={{ fontFamily: '"JetBrains Mono", monospace', color: row.nestingDepth >= 4 ? '#ca8a04' : 'text.primary' }}>
        {row.nestingDepth}
      </TableCell>
      <TableCell align="right" sx={{ fontFamily: '"JetBrains Mono", monospace' }}>
        {row.parameterCount}
      </TableCell>
      <TableCell>
        <Stack direction="row" spacing={0.5} sx={{ flexWrap: 'wrap', gap: 0.25 }}>
          {row.flags.map((f) => (
            <Chip
              key={f}
              size="small"
              label={f}
              sx={{
                height: 16,
                fontSize: '0.625rem',
                fontFamily: '"JetBrains Mono", monospace',
                bgcolor: `${FLAG_COLORS[f] ?? '#64748b'}15`,
                color: FLAG_COLORS[f] ?? '#64748b',
                border: '1px solid',
                borderColor: `${FLAG_COLORS[f] ?? '#64748b'}40`,
                '& .MuiChip-label': { px: 0.5 },
              }}
            />
          ))}
        </Stack>
      </TableCell>
      <TableCell>
        <Tooltip title="Open in VS Code">
          <IconButton size="small" sx={{ p: 0.25 }} onClick={() => openInVsCode(row.filePath, row.startLine)}>
            <LaunchIcon sx={{ fontSize: 12 }} />
          </IconButton>
        </Tooltip>
      </TableCell>
    </TableRow>
  );
}
