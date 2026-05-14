import { useEffect, useRef, useState } from 'react';
import {
  Box,
  Typography,
  Stack,
  Button,
  Chip,
  Paper,
  Alert,
  IconButton,
  Tooltip,
  ToggleButtonGroup,
  ToggleButton,
  TextField,
  Snackbar,
  CircularProgress,
  Link,
} from '@mui/material';
import DownloadIcon from '@mui/icons-material/Download';
import RestartAltIcon from '@mui/icons-material/RestartAlt';
import ContentCopyIcon from '@mui/icons-material/ContentCopy';
import CodeIcon from '@mui/icons-material/CodeOutlined';
import SubjectIcon from '@mui/icons-material/SubjectOutlined';
import SaveAltIcon from '@mui/icons-material/SaveAltOutlined';
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutlineOutlined';
import StopIcon from '@mui/icons-material/StopCircleOutlined';
import WarningAmberIcon from '@mui/icons-material/WarningAmberOutlined';
import LaunchIcon from '@mui/icons-material/LaunchOutlined';
import { useAnalysisStore } from '../stores/analysisStore';
import { useWorkspaceStore } from '../stores/workspaceStore';
import { downloadReportUrl, saveReport } from '../api/analysis';
import { openInVsCode } from '../utils/openInVsCode';
import CodeAnnotationView from './CodeAnnotationView';
import type { Finding, Severity } from '../types';

const CODE_VIEW_PRESETS = new Set(['find-bugs', 'find-dead-code', 'find-business-rules']);

const severityConfig: Record<Severity, { color: string; bg: string; icon: string; label: string }> = {
  bug:        { color: '#dc2626', bg: 'rgba(220,38,38,0.06)',      icon: '●', label: 'Bug' },
  warning:    { color: '#ca8a04', bg: 'rgba(202,138,4,0.06)',      icon: '●', label: 'Warning' },
  suggestion: { color: '#16a34a', bg: 'rgba(22,163,74,0.06)',      icon: '●', label: 'Suggestion' },
  info:       { color: '#0284c7', bg: 'rgba(2,132,199,0.06)',      icon: '●', label: 'Info' },
  deadCode:   { color: '#64748b', bg: 'rgba(100,116,139,0.06)',    icon: '●', label: 'Dead Code' },
};

function FindingCard({ finding, index }: { finding: Finding; index: number }) {
  const cfg = severityConfig[finding.severity] ?? severityConfig.info;
  const isLowConfidence = finding.confidence === 'low';
  const [snippetExpanded, setSnippetExpanded] = useState(false);
  return (
    <Paper
      sx={{
        p: 1.5,
        bgcolor: cfg.bg,
        border: '1px solid',
        borderColor: `${cfg.color}22`,
        position: 'relative',
        opacity: isLowConfidence ? 0.78 : 1,
        animation: 'slideIn 0.25s ease',
        '@keyframes slideIn': {
          from: { opacity: 0, transform: 'translateY(4px)' },
          to:   { opacity: isLowConfidence ? 0.78 : 1, transform: 'translateY(0)' },
        },
        '&::before': {
          content: '""',
          position: 'absolute',
          left: 0, top: 0, bottom: 0,
          width: 3,
          bgcolor: cfg.color,
          borderRadius: '6px 0 0 6px',
          ...(isLowConfidence && {
            backgroundImage: `repeating-linear-gradient(45deg, ${cfg.color}, ${cfg.color} 3px, transparent 3px, transparent 6px)`,
            bgcolor: 'transparent',
          }),
        },
      }}
    >
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.5 }}>
        <Typography
          variant="caption"
          sx={{ color: cfg.color, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.05em', fontSize: '0.6875rem' }}
        >
          {cfg.label}
        </Typography>
        {isLowConfidence && (
          <Tooltip title="The local model couldn't fully prove this from the visible code. Verify with Copilot before acting.">
            <Chip
              size="small"
              label="low confidence"
              sx={{
                height: 16,
                fontSize: '0.625rem',
                fontFamily: '"JetBrains Mono", monospace',
                bgcolor: 'rgba(100,116,139,0.12)',
                color: 'text.secondary',
                border: '1px dashed',
                borderColor: 'rgba(100,116,139,0.4)',
                '& .MuiChip-label': { px: 0.75 },
              }}
            />
          </Tooltip>
        )}
        <Typography variant="caption" color="text.secondary" sx={{ fontFamily: '"JetBrains Mono", monospace' }}>
          #{index + 1}
        </Typography>
        {finding.filePath && (
          <Stack direction="row" sx={{ ml: 'auto', alignItems: 'center', gap: 0.5 }}>
            <Typography
              variant="caption"
              color="text.secondary"
              sx={{ fontFamily: '"JetBrains Mono", monospace', fontSize: '0.6875rem' }}
            >
              {finding.filePath}{finding.lineNumber && `:${finding.lineNumber}`}
            </Typography>
            <Tooltip title="Open in VS Code">
              <IconButton
                size="small"
                sx={{ p: 0.25 }}
                onClick={() => openInVsCode(finding.filePath!, finding.lineNumber ?? undefined)}
              >
                <LaunchIcon sx={{ fontSize: 12 }} />
              </IconButton>
            </Tooltip>
          </Stack>
        )}
      </Stack>
      <Typography variant="body2" sx={{ fontWeight: 600, mb: 0.5 }}>
        {finding.title}
      </Typography>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: finding.codeSnippet ? 1 : 0 }}>
        {finding.description}
      </Typography>
      {finding.codeSnippet && (
        <>
          <Box
            component="pre"
            sx={{
              mt: 1, mb: 0, p: 1,
              bgcolor: '#1e1e2e',
              border: '1px solid rgba(0,0,0,0.15)',
              borderRadius: 0.75,
              fontFamily: '"JetBrains Mono", monospace',
              fontSize: '0.6875rem',
              color: '#cdd6f4',
              overflow: 'auto',
              maxHeight: snippetExpanded ? 'none' : 400,
              whiteSpace: 'pre',
              '&::-webkit-scrollbar':       { width: 8, height: 8 },
              '&::-webkit-scrollbar-thumb': { bgcolor: 'rgba(255,255,255,0.14)', borderRadius: 4 },
            }}
          >
            {finding.codeSnippet}
          </Box>
          <Link
            component="button"
            type="button"
            onClick={() => setSnippetExpanded((e) => !e)}
            underline="hover"
            sx={{ mt: 0.5, fontSize: '0.65rem', fontFamily: '"JetBrains Mono", monospace', color: 'text.secondary' }}
          >
            {snippetExpanded ? 'Collapse snippet' : 'Expand snippet'}
          </Link>
        </>
      )}
    </Paper>
  );
}

const IDLE_WARN_SECONDS = 30;

export default function ResultsView() {
  const runState          = useAnalysisStore((s) => s.runState);
  const statusMessage     = useAnalysisStore((s) => s.statusMessage);
  const streamedText      = useAnalysisStore((s) => s.streamedText);
  const findings          = useAnalysisStore((s) => s.findings);
  const contextTokens     = useAnalysisStore((s) => s.contextTokens);
  const fileCount         = useAnalysisStore((s) => s.fileCount);
  const durationSeconds   = useAnalysisStore((s) => s.durationSeconds);
  const errorMessage      = useAnalysisStore((s) => s.errorMessage);
  const cancelReason      = useAnalysisStore((s) => s.cancelReason);
  const currentAnalysisId = useAnalysisStore((s) => s.currentAnalysisId);
  const analyzedFilePaths = useAnalysisStore((s) => s.analyzedFilePaths);
  const selectedPresetKey = useAnalysisStore((s) => s.selectedPresetKey);
  const incompleteFindings = useAnalysisStore((s) => s.incompleteFindings);
  const malformedFindings  = useAnalysisStore((s) => s.malformedFindings);
  const reachedDone        = useAnalysisStore((s) => s.reachedDone);
  const runStartedAt      = useAnalysisStore((s) => s.runStartedAt);
  const lastTokenAt       = useAnalysisStore((s) => s.lastTokenAt);
  const requestCancel     = useAnalysisStore((s) => s.requestCancel);
  const reset             = useAnalysisStore((s) => s.reset);
  const workspace         = useWorkspaceStore((s) => s.workspace);
  const selectedFiles     = useWorkspaceStore((s) => s.selectedFiles);

  const [activeTab, setActiveTab] = useState<'output' | 'code'>('output');

  const [savePanelOpen, setSavePanelOpen] = useState(false);
  const [savePath, setSavePath]           = useState('');
  const [saving, setSaving]               = useState(false);
  const [savedInfo, setSavedInfo]         = useState<{ relativePath: string; copilotRef: string } | null>(null);
  const [saveError, setSaveError]         = useState<string | null>(null);
  const [toast, setToast]                 = useState<string | null>(null);

  // Reset save state whenever a new analysis starts
  useEffect(() => {
    if (runState === 'starting') {
      setSavePanelOpen(false);
      setSavePath('');
      setSavedInfo(null);
      setSaveError(null);
    }
  }, [runState]);

  const streamRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (streamRef.current) {
      streamRef.current.scrollTop = streamRef.current.scrollHeight;
    }
  }, [streamedText]);

  // Switch to output tab when a new run starts
  useEffect(() => {
    if (runState === 'starting') setActiveTab('output');
  }, [runState]);

  const isActive     = runState === 'starting' || runState === 'building' || runState === 'streaming';
  const isCancelling = runState === 'cancelling';
  const isComplete   = runState === 'completed';
  const isError      = runState === 'error';
  const isCancelled  = runState === 'cancelled';
  const isTerminal   = isComplete || isError || isCancelled;
  const canSaveOrExport = (isComplete || isCancelled) && !!currentAnalysisId;

  // Live elapsed-time tick while running.
  const [nowMs, setNowMs] = useState<number>(() => Date.now());
  useEffect(() => {
    if (!isActive && !isCancelling) return;
    const i = setInterval(() => setNowMs(Date.now()), 1000);
    return () => clearInterval(i);
  }, [isActive, isCancelling]);

  const elapsedSec = runStartedAt && (isActive || isCancelling)
    ? Math.floor((nowMs - runStartedAt) / 1000)
    : 0;

  const idleSec = lastTokenAt && isActive
    ? Math.floor((nowMs - lastTokenAt) / 1000)
    : 0;
  const showIdleWarn = isActive && idleSec >= IDLE_WARN_SECONDS;

  const showCodeTab = (isComplete || isCancelled) && !!selectedPresetKey && CODE_VIEW_PRESETS.has(selectedPresetKey);

  const selectionChanged =
    (isComplete || isCancelled) &&
    analyzedFilePaths.length > 0 &&
    (selectedFiles.size !== analyzedFilePaths.length ||
      analyzedFilePaths.some((p) => !selectedFiles.has(p)));

  const displayText = streamedText
    .replace(/<finding>[\s\S]*?<\/finding>/g, '')
    .replace(/<done\s*\/>/g, '')
    .trim();

  async function handleSave() {
    if (!currentAnalysisId) return;
    setSaving(true);
    setSaveError(null);
    try {
      const trimmed = savePath.trim();
      const res = await saveReport(currentAnalysisId, trimmed || undefined);
      setSavedInfo({ relativePath: res.relativePath, copilotRef: res.copilotReference });
      setSavePanelOpen(false);
    } catch (err: unknown) {
      const msg =
        (err as { response?: { data?: { error?: string } } })?.response?.data?.error ??
        (err instanceof Error ? err.message : 'Save failed');
      setSaveError(msg);
    } finally {
      setSaving(false);
    }
  }

  async function copyToClipboard(text: string, label: string) {
    try {
      await navigator.clipboard.writeText(text);
      setToast(`${label} copied`);
    } catch {
      setToast(`Couldn't copy ${label.toLowerCase()}`);
    }
  }

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', flex: '1 0 480px', bgcolor: 'background.paper' }}>
      {/* Header */}
      <Stack
        direction="row"
        sx={{ px: 3, py: 1.5, borderBottom: '1px solid', borderColor: 'divider', alignItems: 'center', justifyContent: 'space-between', flexShrink: 0, gap: 1, rowGap: 1, flexWrap: 'wrap', minWidth: 0 }}
      >
        <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', flexWrap: 'wrap', rowGap: 0.75, minWidth: 0 }}>
          <Typography variant="overline" color="text.secondary" sx={{ flexShrink: 0 }}>Results</Typography>
          {isActive && (
            <Tooltip title={statusMessage || 'running'} placement="bottom-start">
              <Chip
                size="small"
                label={`${statusMessage || 'running'} · ${elapsedSec}s`}
                sx={{
                  bgcolor: 'rgba(79,70,229,0.08)',
                  color: 'primary.main',
                  fontFamily: '"JetBrains Mono", monospace',
                  maxWidth: '100%',
                  minWidth: 0,
                  '& .MuiChip-label': { overflow: 'hidden', textOverflow: 'ellipsis' },
                }}
              />
            </Tooltip>
          )}
          {isCancelling && (
            <Chip
              size="small"
              label={`cancelling… · ${elapsedSec}s`}
              sx={{ bgcolor: 'rgba(202,138,4,0.08)', color: 'warning.main', fontFamily: '"JetBrains Mono", monospace', flexShrink: 0 }}
            />
          )}
          {showIdleWarn && (
            <Chip
              size="small"
              icon={<WarningAmberIcon sx={{ fontSize: 14 }} />}
              label={`no output for ${idleSec}s`}
              sx={{ bgcolor: 'rgba(202,138,4,0.10)', color: 'warning.main', fontFamily: '"JetBrains Mono", monospace', flexShrink: 0 }}
            />
          )}
          {isComplete && (
            <Chip
              size="small"
              label={`${durationSeconds.toFixed(1)}s · ${findings.length} ${findings.length === 1 ? 'finding' : 'findings'}`}
              sx={{ bgcolor: 'rgba(22,163,74,0.08)', color: 'success.main', fontFamily: '"JetBrains Mono", monospace', flexShrink: 0 }}
            />
          )}
          {isCancelled && (
            <Chip
              size="small"
              label={`cancelled${cancelReason && cancelReason !== 'user' ? ` (${cancelReason})` : ''} · ${findings.length} ${findings.length === 1 ? 'finding' : 'findings'}`}
              sx={{
                bgcolor: cancelReason === 'user' ? 'rgba(100,116,139,0.10)' : 'rgba(202,138,4,0.10)',
                color:   cancelReason === 'user' ? 'text.secondary' : 'warning.main',
                fontFamily: '"JetBrains Mono", monospace',
                flexShrink: 0,
              }}
            />
          )}
          {!isActive && !isCancelling && contextTokens > 0 && (
            <Typography variant="caption" color="text.secondary" sx={{ fontFamily: '"JetBrains Mono", monospace', flexShrink: 0 }}>
              {fileCount} {fileCount === 1 ? 'file' : 'files'} · ~{contextTokens.toLocaleString()} tokens
            </Typography>
          )}

          {/* Output / Code tab toggle */}
          {showCodeTab && (
            <ToggleButtonGroup
              value={activeTab}
              exclusive
              size="small"
              onChange={(_, v) => v && setActiveTab(v)}
              sx={{
                ml: 1,
                '& .MuiToggleButton-root': {
                  py: 0.25, px: 1.25,
                  fontSize: '0.72rem',
                  textTransform: 'none',
                  border: '1px solid',
                  borderColor: 'divider',
                  gap: 0.5,
                },
              }}
            >
              <ToggleButton value="output">
                <SubjectIcon sx={{ fontSize: 14 }} /> Output
              </ToggleButton>
              <ToggleButton value="code">
                <CodeIcon sx={{ fontSize: 14 }} /> Code
              </ToggleButton>
            </ToggleButtonGroup>
          )}
        </Stack>

        <Stack direction="row" spacing={1} sx={{ flexShrink: 0 }}>
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
          {canSaveOrExport && (
            <>
              <Tooltip title="Copy raw output">
                <IconButton size="small" onClick={() => copyToClipboard(streamedText, 'Output')}>
                  <ContentCopyIcon sx={{ fontSize: 16 }} />
                </IconButton>
              </Tooltip>
              <Button
                size="small"
                variant="outlined"
                startIcon={<SaveAltIcon sx={{ fontSize: 16 }} />}
                onClick={() => setSavePanelOpen((o) => !o)}
                disabled={!workspace}
              >
                Save to repo
              </Button>
              <Button
                size="small"
                variant="outlined"
                startIcon={<DownloadIcon sx={{ fontSize: 16 }} />}
                href={downloadReportUrl(currentAnalysisId!)}
                target="_blank"
              >
                Export MD
              </Button>
            </>
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

      {/* Save panel — appears under header when user clicks Save to repo */}
      {savePanelOpen && canSaveOrExport && (
        <Box
          sx={{
            px: 3,
            py: 1.5,
            borderBottom: '1px solid',
            borderColor: 'divider',
            bgcolor: 'background.default',
            flexShrink: 0,
          }}
        >
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
            Write a markdown report into the loaded repo, relative to {workspace?.projectName ?? 'repo'} root.
            Leave path blank to use the default (<code>docs/codeintel</code>).
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
            <Button
              size="small"
              variant="text"
              onClick={() => { setSavePanelOpen(false); setSaveError(null); }}
              disabled={saving}
            >
              Cancel
            </Button>
          </Stack>
          {saveError && (
            <Alert severity="error" sx={{ mt: 1, fontSize: '0.8125rem' }}>
              {saveError}
            </Alert>
          )}
        </Box>
      )}

      {/* Saved confirmation banner */}
      {savedInfo && (
        <Box
          sx={{
            px: 3,
            py: 1.25,
            borderBottom: '1px solid',
            borderColor: 'divider',
            bgcolor: 'rgba(22,163,74,0.06)',
            flexShrink: 0,
            display: 'flex',
            alignItems: 'center',
            gap: 1,
          }}
        >
          <CheckCircleOutlineIcon sx={{ fontSize: 16, color: 'success.main', flexShrink: 0 }} />
          <Typography
            variant="caption"
            sx={{ fontFamily: '"JetBrains Mono", monospace', fontSize: '0.75rem', color: 'text.primary', flex: 1, minWidth: 0, wordBreak: 'break-all' }}
          >
            Saved to <strong>{savedInfo.relativePath}</strong>
          </Typography>
          <Tooltip title="Copy file path">
            <Button
              size="small"
              variant="text"
              onClick={() => copyToClipboard(savedInfo.relativePath, 'Path')}
              startIcon={<ContentCopyIcon sx={{ fontSize: 14 }} />}
              sx={{ fontSize: '0.7rem', textTransform: 'none' }}
            >
              path
            </Button>
          </Tooltip>
          <Tooltip title={`Copy "${savedInfo.copilotRef}" for Copilot Chat`}>
            <Button
              size="small"
              variant="outlined"
              onClick={() => copyToClipboard(savedInfo.copilotRef, 'Copilot reference')}
              startIcon={<ContentCopyIcon sx={{ fontSize: 14 }} />}
              sx={{ fontSize: '0.7rem', textTransform: 'none' }}
            >
              #file: reference
            </Button>
          </Tooltip>
        </Box>
      )}

      <Snackbar
        open={!!toast}
        autoHideDuration={2000}
        onClose={() => setToast(null)}
        message={toast}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
      />

      {/* Stale-selection banner — current file selection no longer matches the run */}
      {selectionChanged && (
        <Box
          sx={{
            px: 3,
            py: 1,
            borderBottom: '1px solid',
            borderColor: 'divider',
            bgcolor: 'rgba(202,138,4,0.06)',
            flexShrink: 0,
            display: 'flex',
            alignItems: 'center',
            gap: 1,
          }}
        >
          <WarningAmberIcon sx={{ fontSize: 14, color: 'warning.main', flexShrink: 0 }} />
          <Typography
            variant="caption"
            sx={{
              fontFamily: '"JetBrains Mono", monospace',
              fontSize: '0.7rem',
              color: 'text.secondary',
              flex: 1,
              minWidth: 0,
            }}
          >
            Showing results from a previous selection ({analyzedFilePaths.length}{' '}
            {analyzedFilePaths.length === 1 ? 'file' : 'files'}). Your current selection has changed —
            re-run to refresh.
          </Typography>
          <Button
            size="small"
            variant="text"
            onClick={reset}
            sx={{ fontSize: '0.7rem', textTransform: 'none', flexShrink: 0 }}
          >
            Clear
          </Button>
        </Box>
      )}

      {isActive && (
        <Box
          sx={{
            height: 2,
            flexShrink: 0,
            position: 'relative',
            overflow: 'hidden',
            bgcolor: 'rgba(99,102,241,0.08)',
            '&::after': {
              content: '""',
              position: 'absolute',
              top: 0, left: 0, right: 0, bottom: 0,
              background: 'linear-gradient(90deg, transparent, rgba(99,102,241,0.7) 35%, rgba(167,139,250,1) 50%, rgba(99,102,241,0.7) 65%, transparent)',
              backgroundSize: '40% 100%',
              backgroundRepeat: 'no-repeat',
              backgroundPosition: '-40% 0',
              animation: 'llmScan 2s ease-in-out infinite',
            },
            '@keyframes llmScan': {
              '0%':   { backgroundPosition: '-40% 0' },
              '100%': { backgroundPosition: '140% 0' },
            },
          }}
        />
      )}

      {/* Code annotation view */}
      {activeTab === 'code' && showCodeTab && workspace && (
        <CodeAnnotationView
          workspaceId={workspace.id}
          filePaths={analyzedFilePaths}
          findings={findings}
        />
      )}

      {/* Output view */}
      {activeTab === 'output' && (
        <Box sx={{ flex: 1, display: 'flex', minHeight: 0 }}>
          {/* Streaming text */}
          <Box
            ref={streamRef}
            sx={{
              flex: 1,
              minWidth: 0,
              p: 3,
              overflow: 'auto',
              fontFamily: '"JetBrains Mono", monospace',
              fontSize: '0.8125rem',
              lineHeight: 1.55,
              color: 'text.primary',
              whiteSpace: 'pre-wrap',
              wordBreak: 'break-word',
              borderRight: findings.length > 0 ? '1px solid' : 'none',
              borderColor: 'divider',
              '&::-webkit-scrollbar':       { width: 8, height: 8 },
              '&::-webkit-scrollbar-thumb': { bgcolor: 'rgba(255,255,255,0.14)', borderRadius: 4 },
            }}
          >
            {runState === 'idle' && (
              <Box sx={{ color: 'text.secondary' }}>
                <Typography variant="body2" sx={{ mb: 1 }}>Ready.</Typography>
                <Typography variant="caption" sx={{ display: 'block', maxWidth: 500, opacity: 0.7 }}>
                  Load a solution, select files, choose a preset or write a question, then run.
                  Tokens stream here in real time. Findings appear as cards on the right.
                </Typography>
              </Box>
            )}
            {isError && errorMessage && (
              <Alert severity="error" sx={{ fontSize: '0.8125rem', mb: 1 }}>{errorMessage}</Alert>
            )}
            {isCancelled && errorMessage && (
              <Alert
                severity={cancelReason === 'user' ? 'info' : 'warning'}
                sx={{ fontSize: '0.8125rem', mb: 1 }}
              >
                {errorMessage}
              </Alert>
            )}
            {isComplete && (incompleteFindings > 0 || malformedFindings > 0 || !reachedDone) && (
              <Alert severity="warning" sx={{ fontSize: '0.8125rem', mb: 1.5 }}>
                <Typography variant="body2" sx={{ fontWeight: 600, mb: 0.5, fontSize: '0.8125rem' }}>
                  Run completed with degraded output
                </Typography>
                {incompleteFindings > 0 && (
                  <Typography variant="caption" sx={{ display: 'block' }}>
                    {incompleteFindings} finding{incompleteFindings === 1 ? '' : 's'} cut off mid-stream — the model hit its response-token cap before closing the {'<finding>'} tag. Try a smaller file selection or bump <code>Llm:MaxResponseTokens</code> in <code>appsettings.Development.json</code>.
                  </Typography>
                )}
                {malformedFindings > 0 && (
                  <Typography variant="caption" sx={{ display: 'block' }}>
                    {malformedFindings} finding{malformedFindings === 1 ? '' : 's'} had invalid JSON and {malformedFindings === 1 ? 'was' : 'were'} dropped by the parser.
                  </Typography>
                )}
                {!reachedDone && incompleteFindings === 0 && malformedFindings === 0 && (
                  <Typography variant="caption" sx={{ display: 'block' }}>
                    The model stopped without writing <code>&lt;done /&gt;</code>. Output may be incomplete.
                  </Typography>
                )}
                <Typography variant="caption" sx={{ display: 'block', mt: 0.5, opacity: 0.8 }}>
                  This run was not cached, so a future re-run will retry from scratch.
                </Typography>
              </Alert>
            )}
            {isComplete
              && findings.length === 0
              && incompleteFindings === 0
              && malformedFindings === 0
              && reachedDone
              && analyzedFilePaths.length > 0
              && selectedPresetKey
              && CODE_VIEW_PRESETS.has(selectedPresetKey) && (
              <Alert
                severity="info"
                icon={<CheckCircleOutlineIcon sx={{ fontSize: 18 }} />}
                sx={{ fontSize: '0.8125rem', mb: 1.5 }}
              >
                Analysis completed. The model reviewed {analyzedFilePaths.length}{' '}
                {analyzedFilePaths.length === 1 ? 'file' : 'files'} and emitted no findings.
                {displayText
                  ? " See the model's rationale below."
                  : ' No rationale was provided — try a different preset or a broader file selection.'}
              </Alert>
            )}
            {isActive && !displayText && (
              <Box
                sx={{
                  mb: 1.5,
                  p: 1.5,
                  bgcolor: 'rgba(99,102,241,0.05)',
                  border: '1px solid rgba(99,102,241,0.18)',
                  borderRadius: 1,
                  fontFamily: '"JetBrains Mono", monospace',
                  fontSize: '0.75rem',
                  color: 'text.secondary',
                  lineHeight: 1.6,
                }}
              >
                <Typography
                  variant="caption"
                  sx={{
                    display: 'block',
                    fontWeight: 600,
                    color: 'primary.main',
                    fontFamily: '"JetBrains Mono", monospace',
                    fontSize: '0.7rem',
                    textTransform: 'uppercase',
                    letterSpacing: '0.05em',
                    mb: 0.5,
                  }}
                >
                  {statusMessage || 'Working…'}
                </Typography>
                {contextTokens > 0 && (
                  <Box sx={{ mb: 0.5 }}>
                    Reading ~{contextTokens.toLocaleString()} tokens of context from{' '}
                    {fileCount} {fileCount === 1 ? 'file' : 'files'}. The model has to process the full
                    prompt before producing its first output token.
                  </Box>
                )}
                <Box sx={{ opacity: 0.75 }}>
                  First-token latency on cold start: typically 30–60s on the home laptop (Vulkan/CPU),
                  faster on machines with a dedicated GPU. Tokens will stream into this pane as soon
                  as the model starts generating.
                </Box>
                {elapsedSec >= 5 && (
                  <Box sx={{ mt: 0.75, opacity: 0.7, fontSize: '0.7rem' }}>
                    Waiting {elapsedSec}s…
                  </Box>
                )}
              </Box>
            )}
            {(isActive || isCancelling || isComplete || isCancelled) && (
              <>
                {displayText}
                {(isActive || isCancelling) && (
                  <Box
                    component="span"
                    sx={{
                      display: 'inline-block',
                      width: '0.5em',
                      height: '1em',
                      bgcolor: 'primary.main',
                      ml: 0.25,
                      verticalAlign: 'text-bottom',
                      animation: 'blink 1s infinite',
                      '@keyframes blink': {
                        '0%, 50%':   { opacity: 1 },
                        '51%, 100%': { opacity: 0 },
                      },
                    }}
                  />
                )}
              </>
            )}
          </Box>

          {/* Findings sidebar */}
          {findings.length > 0 && (
            <Box
              sx={{
                flex: '0 0 360px',
                minWidth: 280,
                p: 2,
                overflow: 'auto',
                bgcolor: 'background.default',
                '&::-webkit-scrollbar':       { width: 8, height: 8 },
                '&::-webkit-scrollbar-thumb': { bgcolor: 'rgba(255,255,255,0.14)', borderRadius: 4 },
              }}
            >
              <Stack direction="row" sx={{ alignItems: 'baseline', justifyContent: 'space-between', mb: 1.5 }}>
                <Typography variant="overline" color="text.secondary">
                  Findings ({findings.length})
                </Typography>
                {findings.some((f) => f.confidence === 'low') && (
                  <Typography
                    variant="caption"
                    color="text.secondary"
                    sx={{ fontFamily: '"JetBrains Mono", monospace', fontSize: '0.6875rem' }}
                  >
                    {findings.filter((f) => f.confidence !== 'low').length} high · {findings.filter((f) => f.confidence === 'low').length} low
                  </Typography>
                )}
              </Stack>
              <Stack spacing={1}>
                {findings.map((finding, i) => (
                  <FindingCard key={i} finding={finding} index={i} />
                ))}
              </Stack>
            </Box>
          )}
        </Box>
      )}
    </Box>
  );
}
