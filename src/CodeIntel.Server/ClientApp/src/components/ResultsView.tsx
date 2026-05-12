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
} from '@mui/material';
import DownloadIcon from '@mui/icons-material/Download';
import RestartAltIcon from '@mui/icons-material/RestartAlt';
import ContentCopyIcon from '@mui/icons-material/ContentCopy';
import CodeIcon from '@mui/icons-material/CodeOutlined';
import SubjectIcon from '@mui/icons-material/SubjectOutlined';
import SaveAltIcon from '@mui/icons-material/SaveAltOutlined';
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutlineOutlined';
import { useAnalysisStore } from '../stores/analysisStore';
import { useWorkspaceStore } from '../stores/workspaceStore';
import { downloadReportUrl, saveReport } from '../api/analysis';
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
  return (
    <Paper
      sx={{
        p: 1.5,
        bgcolor: cfg.bg,
        border: '1px solid',
        borderColor: `${cfg.color}22`,
        position: 'relative',
        animation: 'slideIn 0.25s ease',
        '@keyframes slideIn': {
          from: { opacity: 0, transform: 'translateY(4px)' },
          to:   { opacity: 1, transform: 'translateY(0)' },
        },
        '&::before': {
          content: '""',
          position: 'absolute',
          left: 0, top: 0, bottom: 0,
          width: 3,
          bgcolor: cfg.color,
          borderRadius: '6px 0 0 6px',
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
        <Typography variant="caption" color="text.secondary" sx={{ fontFamily: '"JetBrains Mono", monospace' }}>
          #{index + 1}
        </Typography>
        {finding.filePath && (
          <Typography
            variant="caption"
            color="text.secondary"
            sx={{ fontFamily: '"JetBrains Mono", monospace', ml: 'auto', fontSize: '0.6875rem' }}
          >
            {finding.filePath}{finding.lineNumber && `:${finding.lineNumber}`}
          </Typography>
        )}
      </Stack>
      <Typography variant="body2" sx={{ fontWeight: 600, mb: 0.5 }}>
        {finding.title}
      </Typography>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: finding.codeSnippet ? 1 : 0 }}>
        {finding.description}
      </Typography>
      {finding.codeSnippet && (
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
            maxHeight: 120,
          }}
        >
          {finding.codeSnippet}
        </Box>
      )}
    </Paper>
  );
}

export default function ResultsView() {
  const runState          = useAnalysisStore((s) => s.runState);
  const statusMessage     = useAnalysisStore((s) => s.statusMessage);
  const streamedText      = useAnalysisStore((s) => s.streamedText);
  const findings          = useAnalysisStore((s) => s.findings);
  const contextTokens     = useAnalysisStore((s) => s.contextTokens);
  const fileCount         = useAnalysisStore((s) => s.fileCount);
  const durationSeconds   = useAnalysisStore((s) => s.durationSeconds);
  const errorMessage      = useAnalysisStore((s) => s.errorMessage);
  const currentAnalysisId = useAnalysisStore((s) => s.currentAnalysisId);
  const analyzedFilePaths = useAnalysisStore((s) => s.analyzedFilePaths);
  const selectedPresetKey = useAnalysisStore((s) => s.selectedPresetKey);
  const reset             = useAnalysisStore((s) => s.reset);
  const workspace         = useWorkspaceStore((s) => s.workspace);

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

  const isActive   = runState === 'starting' || runState === 'building' || runState === 'streaming';
  const isComplete = runState === 'completed';
  const isError    = runState === 'error';

  const showCodeTab = isComplete && !!selectedPresetKey && CODE_VIEW_PRESETS.has(selectedPresetKey);

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
    <Box sx={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0, bgcolor: 'background.paper' }}>
      {/* Header */}
      <Stack
        direction="row"
        sx={{ px: 3, py: 1.5, borderBottom: '1px solid', borderColor: 'divider', alignItems: 'center', justifyContent: 'space-between', flexShrink: 0 }}
      >
        <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center' }}>
          <Typography variant="overline" color="text.secondary">Results</Typography>
          {isActive && (
            <Chip
              size="small"
              label={statusMessage || 'running'}
              sx={{ bgcolor: 'rgba(79,70,229,0.08)', color: 'primary.main', fontFamily: '"JetBrains Mono", monospace' }}
            />
          )}
          {isComplete && (
            <Chip
              size="small"
              label={`${durationSeconds.toFixed(1)}s • ${findings.length} ${findings.length === 1 ? 'finding' : 'findings'}`}
              sx={{ bgcolor: 'rgba(22,163,74,0.08)', color: 'success.main', fontFamily: '"JetBrains Mono", monospace' }}
            />
          )}
          {contextTokens > 0 && (
            <Typography variant="caption" color="text.secondary" sx={{ fontFamily: '"JetBrains Mono", monospace' }}>
              {fileCount} {fileCount === 1 ? 'file' : 'files'} • ~{contextTokens.toLocaleString()} tokens
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

        <Stack direction="row" spacing={1}>
          {isComplete && currentAnalysisId && (
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
                href={downloadReportUrl(currentAnalysisId)}
                target="_blank"
              >
                Export MD
              </Button>
            </>
          )}
          {(isComplete || isError) && (
            <Tooltip title="Reset">
              <IconButton size="small" onClick={reset}>
                <RestartAltIcon sx={{ fontSize: 16 }} />
              </IconButton>
            </Tooltip>
          )}
        </Stack>
      </Stack>

      {/* Save panel — appears under header when user clicks Save to repo */}
      {savePanelOpen && isComplete && (
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
              <Alert severity="error" sx={{ fontSize: '0.8125rem' }}>{errorMessage}</Alert>
            )}
            {(isActive || isComplete) && (
              <>
                {displayText}
                {isActive && (
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
                width: 380,
                minWidth: 380,
                p: 2,
                overflow: 'auto',
                bgcolor: 'background.default',
              }}
            >
              <Typography variant="overline" color="text.secondary" sx={{ display: 'block', mb: 1.5 }}>
                Findings ({findings.length})
              </Typography>
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
