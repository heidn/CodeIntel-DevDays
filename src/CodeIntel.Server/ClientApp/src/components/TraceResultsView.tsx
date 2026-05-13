import { useEffect, useMemo, useState } from 'react';
import {
  Box,
  Typography,
  Stack,
  Button,
  Chip,
  Alert,
  IconButton,
  Tooltip,
  TextField,
  Snackbar,
  CircularProgress,
  Paper,
} from '@mui/material';
import RestartAltIcon from '@mui/icons-material/RestartAlt';
import ContentCopyIcon from '@mui/icons-material/ContentCopy';
import SaveAltIcon from '@mui/icons-material/SaveAltOutlined';
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutlineOutlined';
import StopIcon from '@mui/icons-material/StopCircleOutlined';
import StorageIcon from '@mui/icons-material/StorageOutlined';
import CloudIcon from '@mui/icons-material/CloudOutlined';
import LaunchIcon from '@mui/icons-material/LaunchOutlined';
import WarningAmberIcon from '@mui/icons-material/WarningAmberOutlined';
import CloseIcon from '@mui/icons-material/Close';
import BugReportIcon from '@mui/icons-material/BugReportOutlined';
import { openInVsCode } from '../utils/openInVsCode';
import type { AnalysisResult, Finding, NodeKind, Severity } from '../types';
import { getRecentAnalyses } from '../api/analysis';
import { decorateMermaidWithFindings, matchFindingsToNodes, SEVERITY_RANK } from '../utils/findingsOverlay';

const NODE_KIND_STYLE: Record<NodeKind, { label: string; bg: string; color: string; icon: typeof StorageIcon } | null> = {
  normal: null,
  dbAccess: { label: 'DB',   bg: 'rgba(59,130,246,0.10)', color: '#93c5fd', icon: StorageIcon },
  httpCall: { label: 'HTTP', bg: 'rgba(34,197,94,0.10)',  color: '#86efac', icon: CloudIcon   },
};

const SEVERITY_STYLE: Record<Severity, { label: string; bg: string; color: string }> = {
  bug:        { label: 'BUG',     bg: 'rgba(239,68,68,0.12)',  color: '#fca5a5' },
  warning:    { label: 'WARN',    bg: 'rgba(245,158,11,0.12)', color: '#fcd34d' },
  deadCode:   { label: 'DEAD',    bg: 'rgba(168,85,247,0.12)', color: '#d8b4fe' },
  suggestion: { label: 'HINT',    bg: 'rgba(56,189,248,0.10)', color: '#7dd3fc' },
  info:       { label: 'INFO',    bg: 'rgba(148,163,184,0.10)', color: '#cbd5e1' },
};

function formatAnalysisLabel(a: AnalysisResult): string {
  const when = new Date(a.startedAt);
  const date = `${when.getMonth() + 1}/${when.getDate()}`;
  const time = when.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  const what = a.presetKey ?? (a.freeTextPrompt ? 'free-text' : 'analysis');
  return `${date} ${time} · ${what}`;
}
import { useTraceStore } from '../stores/traceStore';
import { useWorkspaceStore } from '../stores/workspaceStore';
import { getTrace, saveTraceReport } from '../api/trace';
import MermaidDiagram from './MermaidDiagram';

function buildDiagramFilenameStem(entryPointFqn: string | null, traceId: string | null): string {
  if (!entryPointFqn) return 'trace-diagram';
  // Strip the parameter list (which contains its own dots) then take the last two segments.
  const noParams = entryPointFqn.split('(')[0];
  const parts = noParams.split('.');
  const label = parts.length >= 2 ? `${parts[parts.length - 2]}-${parts[parts.length - 1]}` : parts[parts.length - 1];
  const slug = label.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  const idSuffix = traceId ? `-${traceId.slice(0, 8)}` : '';
  return `trace-${slug || 'diagram'}${idSuffix}`;
}

export default function TraceResultsView() {
  const runState         = useTraceStore((s) => s.runState);
  const statusMessage    = useTraceStore((s) => s.statusMessage);
  const entryPointFqn    = useTraceStore((s) => s.entryPointFqn);
  const nodes            = useTraceStore((s) => s.nodes);
  const edges            = useTraceStore((s) => s.edges);
  const mermaid          = useTraceStore((s) => s.mermaid);
  const truncated        = useTraceStore((s) => s.truncated);
  const durationSeconds  = useTraceStore((s) => s.durationSeconds);
  const errorMessage     = useTraceStore((s) => s.errorMessage);
  const cancelReason     = useTraceStore((s) => s.cancelReason);
  const currentTraceId   = useTraceStore((s) => s.currentTraceId);
  const runStartedAt     = useTraceStore((s) => s.runStartedAt);
  const lastEventAt      = useTraceStore((s) => s.lastEventAt);
  const requestCancel    = useTraceStore((s) => s.requestCancel);
  const reset            = useTraceStore((s) => s.reset);
  const setGraph         = useTraceStore((s) => s.setGraph);
  const workspace        = useWorkspaceStore((s) => s.workspace);
  const setPreviewedFile = useWorkspaceStore((s) => s.setPreviewedFile);

  const [savePanelOpen, setSavePanelOpen] = useState(false);
  const [savePath, setSavePath]           = useState('');
  const [saving, setSaving]               = useState(false);
  const [savedInfo, setSavedInfo]         = useState<{ relativePath: string; copilotRef: string } | null>(null);
  const [saveError, setSaveError]         = useState<string | null>(null);
  const [toast, setToast]                 = useState<string | null>(null);

  // Findings overlay: most-recent analysis for this workspace, mapped onto trace nodes.
  const [overlayAnalysis, setOverlayAnalysis] = useState<AnalysisResult | null>(null);
  const [overlayDismissed, setOverlayDismissed] = useState(false);

  // Reset save state on a new run.
  useEffect(() => {
    if (runState === 'starting') {
      setSavePanelOpen(false);
      setSavePath('');
      setSavedInfo(null);
      setSaveError(null);
      setOverlayAnalysis(null);
      setOverlayDismissed(false);
    }
  }, [runState]);

  // When the trace completes, fetch the most-recent analysis for this workspace
  // and auto-apply it as a finding overlay. The user can dismiss it.
  useEffect(() => {
    if (runState !== 'completed' && runState !== 'cancelled') return;
    if (!workspace || nodes.length === 0 || overlayDismissed) return;
    if (overlayAnalysis) return;
    let cancelled = false;
    (async () => {
      try {
        const list = await getRecentAnalyses(workspace.id, 1);
        if (cancelled) return;
        if (list.length > 0 && list[0].findings.length > 0) {
          setOverlayAnalysis(list[0]);
        }
      } catch {
        // Non-fatal — overlay is opt-in enhancement.
      }
    })();
    return () => { cancelled = true; };
  }, [runState, workspace, nodes.length, overlayAnalysis, overlayDismissed]);

  // Match findings to nodes (recomputes when overlay or nodes change).
  const findingsByNode = useMemo<Map<string, Finding[]>>(
    () => (overlayAnalysis ? matchFindingsToNodes(nodes, overlayAnalysis.findings) : new Map()),
    [overlayAnalysis, nodes],
  );

  const matchedFindingCount = useMemo(() => {
    let n = 0;
    for (const list of findingsByNode.values()) n += list.length;
    return n;
  }, [findingsByNode]);

  // Decorate the Mermaid source with finding rings when the overlay is active.
  const decoratedMermaid = useMemo(
    () => (mermaid && findingsByNode.size > 0 ? decorateMermaidWithFindings(mermaid, findingsByNode) : mermaid),
    [mermaid, findingsByNode],
  );

  // When the trace completes, pull the full result (graph + mermaid + edges) from the API.
  // The trace SignalR stream only carries traceGraphReady + per-node synopses incrementally.
  useEffect(() => {
    if (runState !== 'completed' && runState !== 'cancelled') return;
    if (!currentTraceId) return;
    if (mermaid) return; // already populated
    (async () => {
      try {
        const r = await getTrace(currentTraceId);
        setGraph(r.entryPointSymbolFqn, r.nodes, r.edges, r.mermaid, r.truncated);
      } catch {
        // The store still has incremental data; not fatal.
      }
    })();
  }, [runState, currentTraceId, mermaid, setGraph]);

  const isActive     = runState === 'starting' || runState === 'building' || runState === 'streaming';
  const isCancelling = runState === 'cancelling';
  const isComplete   = runState === 'completed';
  const isError      = runState === 'error';
  const isCancelled  = runState === 'cancelled';
  const isTerminal   = isComplete || isError || isCancelled;
  const canSave      = (isComplete || isCancelled) && !!currentTraceId && nodes.length > 0;

  const [nowMs, setNowMs] = useState<number>(() => Date.now());
  useEffect(() => {
    if (!isActive && !isCancelling) return;
    const i = setInterval(() => setNowMs(Date.now()), 1000);
    return () => clearInterval(i);
  }, [isActive, isCancelling]);

  const elapsedSec = runStartedAt && (isActive || isCancelling)
    ? Math.floor((nowMs - runStartedAt) / 1000)
    : 0;

  // Surface the same "no output for Ns" idle warn the analysis view shows when
  // the per-node synopsis call has stalled.
  const IDLE_WARN_SECONDS = 30;
  const idleSec = lastEventAt && isActive
    ? Math.floor((nowMs - lastEventAt) / 1000)
    : 0;
  const showIdleWarn = isActive && idleSec >= IDLE_WARN_SECONDS;

  const synopsizedCount = nodes.filter((n) => n.synopsis).length;

  async function handleSave() {
    if (!currentTraceId) return;
    setSaving(true);
    setSaveError(null);
    try {
      const trimmed = savePath.trim();
      const res = await saveTraceReport(currentTraceId, trimmed || undefined);
      setSavedInfo({ relativePath: res.relativePath, copilotRef: res.copilotReference });
      setSavePanelOpen(false);
    } catch (err) {
      const msg =
        (err as { response?: { data?: { error?: string } } })?.response?.data?.error ??
        (err instanceof Error ? err.message : 'Save failed');
      setSaveError(msg);
    } finally {
      setSaving(false);
    }
  }

  async function copyToClipboard(text: string, label: string) {
    try { await navigator.clipboard.writeText(text); setToast(`${label} copied`); }
    catch { setToast(`Couldn't copy ${label.toLowerCase()}`); }
  }

  return (
    <Box sx={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0, bgcolor: 'background.paper' }}>
      {/* Header */}
      <Stack
        direction="row"
        sx={{ px: 3, py: 1.5, borderBottom: '1px solid', borderColor: 'divider', alignItems: 'center', justifyContent: 'space-between', flexShrink: 0 }}
      >
        <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
          <Typography variant="overline" color="text.secondary">Trace</Typography>
          {isActive && (
            <Chip
              size="small"
              label={`${statusMessage || 'running'} · ${elapsedSec}s`}
              sx={{ bgcolor: 'rgba(79,70,229,0.08)', color: 'primary.main', fontFamily: '"JetBrains Mono", monospace' }}
            />
          )}
          {isCancelling && (
            <Chip
              size="small"
              label={`cancelling… · ${elapsedSec}s`}
              sx={{ bgcolor: 'rgba(202,138,4,0.08)', color: 'warning.main', fontFamily: '"JetBrains Mono", monospace' }}
            />
          )}
          {showIdleWarn && (
            <Chip
              size="small"
              icon={<WarningAmberIcon sx={{ fontSize: 14 }} />}
              label={`no output for ${idleSec}s`}
              sx={{ bgcolor: 'rgba(202,138,4,0.10)', color: 'warning.main', fontFamily: '"JetBrains Mono", monospace' }}
            />
          )}
          {nodes.length > 0 && (
            <Chip
              size="small"
              label={`${synopsizedCount}/${nodes.length} synopses · ${edges.length} edges${truncated ? ' (truncated)' : ''}`}
              sx={{
                bgcolor: isComplete ? 'rgba(22,163,74,0.08)' : 'rgba(99,102,241,0.08)',
                color:   isComplete ? 'success.main' : 'primary.main',
                fontFamily: '"JetBrains Mono", monospace',
              }}
            />
          )}
          {isComplete && (
            <Typography variant="caption" color="text.secondary" sx={{ fontFamily: '"JetBrains Mono", monospace' }}>
              {durationSeconds.toFixed(1)}s
            </Typography>
          )}
          {isCancelled && (
            <Chip
              size="small"
              label={`cancelled${cancelReason && cancelReason !== 'user' ? ` (${cancelReason})` : ''}`}
              sx={{
                bgcolor: cancelReason === 'user' ? 'rgba(100,116,139,0.10)' : 'rgba(202,138,4,0.10)',
                color:   cancelReason === 'user' ? 'text.secondary' : 'warning.main',
                fontFamily: '"JetBrains Mono", monospace',
              }}
            />
          )}
          {overlayAnalysis && (
            <Chip
              size="small"
              icon={<BugReportIcon sx={{ fontSize: 14 }} />}
              label={`overlay: ${formatAnalysisLabel(overlayAnalysis)} · ${matchedFindingCount}/${overlayAnalysis.findings.length} on this trace`}
              onDelete={() => { setOverlayAnalysis(null); setOverlayDismissed(true); }}
              deleteIcon={<CloseIcon sx={{ fontSize: 14 }} />}
              sx={{
                bgcolor: 'rgba(239,68,68,0.08)',
                color: '#fca5a5',
                fontFamily: '"JetBrains Mono", monospace',
                '& .MuiChip-icon': { color: '#fca5a5' },
                '& .MuiChip-deleteIcon': { color: '#fca5a5', '&:hover': { color: '#fecaca' } },
              }}
            />
          )}
        </Stack>

        <Stack direction="row" spacing={1}>
          {(isActive || isCancelling) && (
            <Button
              size="small"
              color="warning"
              variant="outlined"
              startIcon={<StopIcon sx={{ fontSize: 16 }} />}
              onClick={requestCancel}
              disabled={isCancelling}
            >
              {isCancelling ? 'Cancelling…' : 'Cancel'}
            </Button>
          )}
          {canSave && (
            <Button
              size="small"
              variant="outlined"
              startIcon={<SaveAltIcon sx={{ fontSize: 16 }} />}
              onClick={() => setSavePanelOpen((o) => !o)}
              disabled={!workspace}
            >
              Save to repo
            </Button>
          )}
          {isTerminal && (
            <Tooltip title="Reset">
              <IconButton size="small" onClick={reset}>
                <RestartAltIcon sx={{ fontSize: 16 }} />
              </IconButton>
            </Tooltip>
          )}
        </Stack>
      </Stack>

      {/* Save panel */}
      {savePanelOpen && canSave && (
        <Box sx={{ px: 3, py: 1.5, borderBottom: '1px solid', borderColor: 'divider', bgcolor: 'background.default', flexShrink: 0 }}>
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
            Write the trace as markdown into the loaded repo. Leave blank for the default (<code>docs/codeintel</code>).
          </Typography>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <TextField
              size="small"
              fullWidth
              placeholder="docs/codeintel"
              value={savePath}
              onChange={(e) => setSavePath(e.target.value)}
              disabled={saving}
              sx={{ '& input': { fontFamily: '"JetBrains Mono", monospace', fontSize: '0.8125rem' } }}
            />
            <Button
              size="small"
              variant="contained"
              onClick={handleSave}
              disabled={saving}
              startIcon={saving ? <CircularProgress size={14} color="inherit" /> : <SaveAltIcon sx={{ fontSize: 16 }} />}
            >
              {saving ? 'Saving' : 'Save'}
            </Button>
            <Button size="small" variant="text" onClick={() => { setSavePanelOpen(false); setSaveError(null); }} disabled={saving}>Cancel</Button>
          </Stack>
          {saveError && (<Alert severity="error" sx={{ mt: 1, fontSize: '0.8125rem' }}>{saveError}</Alert>)}
        </Box>
      )}

      {/* Saved banner */}
      {savedInfo && (
        <Box sx={{ px: 3, py: 1.25, borderBottom: '1px solid', borderColor: 'divider', bgcolor: 'rgba(22,163,74,0.06)', flexShrink: 0, display: 'flex', alignItems: 'center', gap: 1 }}>
          <CheckCircleOutlineIcon sx={{ fontSize: 16, color: 'success.main', flexShrink: 0 }} />
          <Typography variant="caption" sx={{ fontFamily: '"JetBrains Mono", monospace', fontSize: '0.75rem', color: 'text.primary', flex: 1, minWidth: 0, wordBreak: 'break-all' }}>
            Saved to <strong>{savedInfo.relativePath}</strong>
          </Typography>
          <Button size="small" variant="text" onClick={() => copyToClipboard(savedInfo.relativePath, 'Path')} startIcon={<ContentCopyIcon sx={{ fontSize: 14 }} />} sx={{ fontSize: '0.7rem', textTransform: 'none' }}>path</Button>
          <Button size="small" variant="outlined" onClick={() => copyToClipboard(savedInfo.copilotRef, 'Copilot reference')} startIcon={<ContentCopyIcon sx={{ fontSize: 14 }} />} sx={{ fontSize: '0.7rem', textTransform: 'none' }}>#file: reference</Button>
        </Box>
      )}

      <Snackbar
        open={!!toast}
        autoHideDuration={2000}
        onClose={() => setToast(null)}
        message={toast}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
      />

      {/* Body */}
      <Box sx={{ flex: 1, overflow: 'auto', minHeight: 0 }}>
        {runState === 'idle' && (
          <Box sx={{ p: 3, color: 'text.secondary' }}>
            <Typography variant="body2" sx={{ mb: 1 }}>Ready.</Typography>
            <Typography variant="caption" sx={{ display: 'block', maxWidth: 500, opacity: 0.7 }}>
              Enter an entry-point method name and run. We'll walk the call graph and synopsize each node.
            </Typography>
          </Box>
        )}

        {isError && errorMessage && (
          <Box sx={{ px: 3, pt: 2 }}>
            <Alert severity="error" sx={{ fontSize: '0.8125rem' }}>{errorMessage}</Alert>
          </Box>
        )}

        {isCancelled && errorMessage && (
          <Box sx={{ px: 3, pt: 2 }}>
            <Alert severity={cancelReason === 'user' ? 'info' : 'warning'} sx={{ fontSize: '0.8125rem' }}>{errorMessage}</Alert>
          </Box>
        )}

        {entryPointFqn && (
          <Box sx={{ px: 3, pt: 2 }}>
            <Typography variant="caption" color="text.secondary">Entry point</Typography>
            <Typography sx={{ fontFamily: '"JetBrains Mono", monospace', fontSize: '0.8125rem', wordBreak: 'break-all', mb: 1 }}>
              {entryPointFqn}
            </Typography>
          </Box>
        )}

        {decoratedMermaid && (
          <MermaidDiagram
            source={decoratedMermaid}
            filenameStem={buildDiagramFilenameStem(entryPointFqn, currentTraceId)}
          />
        )}

        {nodes.length > 0 && (
          <Box sx={{ px: 3, pb: 3 }}>
            <Typography variant="overline" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
              Node synopses
            </Typography>
            <Stack spacing={1}>
              {nodes.map((node) => {
                const nodeFindings = findingsByNode.get(node.id) ?? [];
                return (
                <Paper
                  key={node.id}
                  variant="outlined"
                  sx={{ p: 1.5, cursor: node.filePath ? 'pointer' : 'default' }}
                  onClick={() => node.filePath && setPreviewedFile(node.filePath)}
                >
                  <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.5, flexWrap: 'wrap' }}>
                    <Typography variant="body2" sx={{ fontWeight: 600, fontFamily: '"JetBrains Mono", monospace' }}>
                      {node.displayName}
                    </Typography>
                    {(() => {
                      const ks = NODE_KIND_STYLE[node.kind];
                      if (!ks) return null;
                      const Icon = ks.icon;
                      return (
                        <Chip
                          size="small"
                          icon={<Icon sx={{ fontSize: 12 }} />}
                          label={ks.label}
                          sx={{
                            height: 18,
                            fontSize: '0.625rem',
                            fontFamily: '"JetBrains Mono", monospace',
                            bgcolor: ks.bg,
                            color: ks.color,
                            '& .MuiChip-icon': { color: ks.color, ml: 0.5 },
                          }}
                        />
                      );
                    })()}
                    {nodeFindings.length > 0 && (() => {
                      const top = nodeFindings.reduce((a, b) =>
                        SEVERITY_RANK[b.severity] > SEVERITY_RANK[a.severity] ? b : a);
                      const ss = SEVERITY_STYLE[top.severity];
                      return (
                        <Chip
                          size="small"
                          icon={<BugReportIcon sx={{ fontSize: 12 }} />}
                          label={`${nodeFindings.length} finding${nodeFindings.length === 1 ? '' : 's'}`}
                          sx={{
                            height: 18,
                            fontSize: '0.625rem',
                            fontFamily: '"JetBrains Mono", monospace',
                            bgcolor: ss.bg,
                            color: ss.color,
                            '& .MuiChip-icon': { color: ss.color, ml: 0.5 },
                          }}
                        />
                      );
                    })()}
                    {node.filePath && (
                      <Stack direction="row" sx={{ ml: 'auto', alignItems: 'center', gap: 0.5 }}>
                        <Typography variant="caption" color="text.secondary" sx={{ fontFamily: '"JetBrains Mono", monospace', fontSize: '0.6875rem' }}>
                          {node.filePath.split(/[\\/]/).pop()}{node.line ? `:${node.line}` : ''}
                        </Typography>
                        <Tooltip title="Open in VS Code">
                          <IconButton
                            size="small"
                            sx={{ p: 0.25 }}
                            onClick={(e) => {
                              e.stopPropagation();
                              openInVsCode(node.filePath!, node.line ?? undefined);
                            }}
                          >
                            <LaunchIcon sx={{ fontSize: 12 }} />
                          </IconButton>
                        </Tooltip>
                      </Stack>
                    )}
                  </Stack>
                  {node.synopsis
                    ? (
                      <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                        {node.synopsis}
                      </Typography>
                    )
                    : (
                      <Typography variant="caption" color="text.disabled" sx={{ display: 'block', fontStyle: 'italic' }}>
                        synopsizing…
                      </Typography>
                    )}
                  {nodeFindings.length > 0 && (
                    <Stack spacing={0.5} sx={{ mt: 1, pt: 1, borderTop: '1px dashed', borderColor: 'divider' }}>
                      {nodeFindings.map((f, i) => {
                        const ss = SEVERITY_STYLE[f.severity];
                        return (
                          <Box key={i} sx={{ display: 'flex', gap: 0.75, alignItems: 'flex-start' }}>
                            <Chip
                              size="small"
                              label={ss.label}
                              sx={{
                                height: 16,
                                fontSize: '0.6rem',
                                fontFamily: '"JetBrains Mono", monospace',
                                bgcolor: ss.bg,
                                color: ss.color,
                                fontWeight: 600,
                                flexShrink: 0,
                                '& .MuiChip-label': { px: 0.75 },
                              }}
                            />
                            <Box sx={{ minWidth: 0, flex: 1 }}>
                              <Typography
                                variant="caption"
                                sx={{ display: 'block', fontWeight: 600, color: 'text.primary', lineHeight: 1.3 }}
                              >
                                {f.title}
                                {f.lineNumber != null && (
                                  <Box component="span" sx={{ ml: 0.75, color: 'text.disabled', fontFamily: '"JetBrains Mono", monospace', fontWeight: 400, fontSize: '0.65rem' }}>
                                    :{f.lineNumber}
                                  </Box>
                                )}
                              </Typography>
                              {f.description && (
                                <Typography variant="caption" color="text.secondary" sx={{ display: 'block', lineHeight: 1.4 }}>
                                  {f.description}
                                </Typography>
                              )}
                            </Box>
                          </Box>
                        );
                      })}
                    </Stack>
                  )}
                </Paper>
                );
              })}
            </Stack>
          </Box>
        )}
      </Box>
    </Box>
  );
}
