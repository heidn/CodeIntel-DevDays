import {
  Box,
  Typography,
  Stack,
  Button,
  TextField,
  ToggleButtonGroup,
  ToggleButton,
  Chip,
  CircularProgress,
  Alert,
  Paper,
} from '@mui/material';
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import SkullIcon from '@mui/icons-material/HeartBrokenOutlined';
import BugReportIcon from '@mui/icons-material/BugReportOutlined';
import GavelIcon from '@mui/icons-material/GavelOutlined';
import AutoStoriesIcon from '@mui/icons-material/AutoStoriesOutlined';
import PushPinOutlinedIcon from '@mui/icons-material/PushPinOutlined';
import CleaningServicesOutlinedIcon from '@mui/icons-material/CleaningServicesOutlined';
import SpeedOutlinedIcon from '@mui/icons-material/SpeedOutlined';
import PlaylistAddCheckOutlinedIcon from '@mui/icons-material/PlaylistAddCheckOutlined';
import { useEffect, useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { getPresets, startAnalysis, estimateRun } from '../api/analysis';
import type { EstimateResult } from '../api/analysis';
import { useWorkspaceStore } from '../stores/workspaceStore';
import { useAnalysisStore } from '../stores/analysisStore';
import { getAnalysisHub } from '../api/signalr';
import type { AnalysisRequest } from '../types';

type Mode = 'preset' | 'freeText';

const iconMap: Record<string, React.ReactNode> = {
  skull: <SkullIcon sx={{ fontSize: 18 }} />,
  bug: <BugReportIcon sx={{ fontSize: 18 }} />,
  scale: <GavelIcon sx={{ fontSize: 18 }} />,
  book: <AutoStoriesIcon sx={{ fontSize: 18 }} />,
  broom: <CleaningServicesOutlinedIcon sx={{ fontSize: 18 }} />,
  speed: <SpeedOutlinedIcon sx={{ fontSize: 18 }} />,
};

function baseFileName(path: string): string {
  return path.replace(/\\/g, '/').split('/').pop() ?? path;
}

export default function PromptSelector() {
  const [mode, setMode] = useState<Mode>('preset');
  const workspace     = useWorkspaceStore((s) => s.workspace);
  const selectedFiles = useWorkspaceStore((s) => s.selectedFiles);
  const pinnedSnippet = useWorkspaceStore((s) => s.pinnedSnippet);
  const pinSnippet    = useWorkspaceStore((s) => s.pinSnippet);

  const selectedPresetKey = useAnalysisStore((s) => s.selectedPresetKey);
  const freeTextPrompt    = useAnalysisStore((s) => s.freeTextPrompt);
  const setPreset         = useAnalysisStore((s) => s.setPreset);
  const setFreeText       = useAnalysisStore((s) => s.setFreeText);
  const startRun          = useAnalysisStore((s) => s.startRun);
  const runState          = useAnalysisStore((s) => s.runState);

  const { data: allPresets = [] } = useQuery({ queryKey: ['presets'], queryFn: getPresets });
  const presets = workspace
    ? allPresets.filter(
        (p) =>
          !p.applicableLanguages ||
          p.applicableLanguages.length === 0 ||
          p.applicableLanguages.includes(workspace.language),
      )
    : allPresets;

  // If the active preset is no longer applicable to this workspace's language, clear it.
  useEffect(() => {
    if (!selectedPresetKey) return;
    if (presets.some((p) => p.key === selectedPresetKey)) return;
    setPreset(null);
  }, [selectedPresetKey, presets, setPreset]);

  // Debounced cost/time estimate when the selection changes.
  const [estimate, setEstimate] = useState<EstimateResult | null>(null);
  useEffect(() => {
    if (!workspace || selectedFiles.size === 0) {
      setEstimate(null);
      return;
    }
    const handle = setTimeout(async () => {
      try {
        const result = await estimateRun(workspace.id, Array.from(selectedFiles));
        setEstimate(result);
      } catch {
        setEstimate(null);
      }
    }, 300);
    return () => clearTimeout(handle);
  }, [workspace, selectedFiles]);

  function formatDuration(seconds: number): string {
    if (seconds < 60) return `~${Math.round(seconds)}s`;
    const m = Math.floor(seconds / 60);
    const s = Math.round(seconds % 60);
    return s === 0 ? `~${m}m` : `~${m}m ${s}s`;
  }

  const runMutation = useMutation({
    mutationFn: async (req: Omit<AnalysisRequest, 'analysisId'>) => {
      const analysisId = crypto.randomUUID();
      startRun(analysisId, req.selectedFilePaths);
      const hub = getAnalysisHub();
      await hub.joinAnalysis(analysisId);
      return startAnalysis({ ...req, analysisId });
    },
  });

  const canRun =
    !!workspace &&
    selectedFiles.size > 0 &&
    (mode === 'preset' ? !!selectedPresetKey : freeTextPrompt.trim().length > 5) &&
    runState !== 'starting' &&
    runState !== 'streaming' &&
    runState !== 'building';

  const handleRun = () => {
    if (!workspace) return;
    runMutation.mutate({
      mode,
      presetKey: mode === 'preset' ? selectedPresetKey : null,
      freeTextPrompt: mode === 'freeText' ? freeTextPrompt : null,
      selectedFilePaths: Array.from(selectedFiles),
      workspaceId: workspace.id,
      pinnedSnippet: pinnedSnippet ?? null,
    });
  };

  return (
    <Box sx={{ p: 3, bgcolor: 'background.paper' }}>
      <Stack direction="row" sx={{ alignItems: 'center', justifyContent: 'space-between', mb: 2 }}>
        <Typography variant="overline" color="text.secondary">
          Analysis
        </Typography>
        <ToggleButtonGroup
          value={mode}
          exclusive
          size="small"
          onChange={(_, v) => v && setMode(v)}
          sx={{
            '& .MuiToggleButton-root': {
              py: 0.25,
              px: 1.5,
              fontSize: '0.75rem',
              textTransform: 'none',
              border: '1px solid',
              borderColor: 'divider',
            },
          }}
        >
          <ToggleButton value="preset">Preset</ToggleButton>
          <ToggleButton value="freeText">Free Text</ToggleButton>
        </ToggleButtonGroup>
      </Stack>

      {mode === 'preset' && (
        <Box
          sx={{
            display: 'grid',
            gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))',
            gap: 1.5,
            mb: 2,
          }}
        >
          {presets.map((preset) => {
            const isSelected = preset.key === selectedPresetKey;
            return (
              <Box
                key={preset.key}
                onClick={() => setPreset(isSelected ? null : preset.key)}
                sx={{
                  p: 1.5,
                  border: '1px solid',
                  borderColor: isSelected ? 'primary.main' : 'divider',
                  borderRadius: 1,
                  cursor: 'pointer',
                  bgcolor: isSelected ? 'rgba(79, 70, 229, 0.05)' : 'background.paper',
                  transition: 'all 0.12s ease',
                  '&:hover': {
                    borderColor: isSelected ? 'primary.main' : 'rgba(0,0,0,0.18)',
                    bgcolor: isSelected ? 'rgba(79, 70, 229, 0.08)' : 'rgba(0,0,0,0.02)',
                  },
                }}
              >
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.5 }}>
                  <Box sx={{ color: isSelected ? 'primary.main' : 'text.secondary' }}>
                    {iconMap[preset.icon] ?? <BugReportIcon sx={{ fontSize: 18 }} />}
                  </Box>
                  <Typography variant="body2" sx={{ fontWeight: 600 }}>
                    {preset.name}
                  </Typography>
                </Stack>
                <Typography variant="caption" color="text.secondary" sx={{ display: 'block', lineHeight: 1.4 }}>
                  {preset.description}
                </Typography>
              </Box>
            );
          })}
        </Box>
      )}

      {mode === 'freeText' && (
        <TextField
          fullWidth
          multiline
          minRows={3}
          maxRows={6}
          placeholder="Ask anything about the selected code. e.g., 'Why does this service have so many dependencies?' or 'What edge cases are missing here?'"
          value={freeTextPrompt}
          onChange={(e) => setFreeText(e.target.value)}
          sx={{
            mb: 2,
            '& .MuiOutlinedInput-root': { fontSize: '0.875rem' },
          }}
        />
      )}

      {/* Step 2 nudge: files not yet selected */}
      {workspace && selectedFiles.size === 0 && (
        <Paper
          sx={{
            mb: 2,
            p: 1.5,
            bgcolor: 'rgba(79,70,229,0.04)',
            border: '1px solid rgba(79,70,229,0.15)',
            display: 'flex',
            alignItems: 'center',
            gap: 1.5,
          }}
        >
          <PlaylistAddCheckOutlinedIcon sx={{ fontSize: 18, color: 'primary.main', flexShrink: 0 }} />
          <Box>
            <Typography variant="caption" sx={{ fontWeight: 600, color: 'primary.main', display: 'block' }}>
              Step 2 — Select files
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Check files or whole projects in the left panel, then pick a preset above and run.
            </Typography>
          </Box>
        </Paper>
      )}

      <Stack direction="row" sx={{ alignItems: 'center', justifyContent: 'space-between' }}>
        <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', gap: 0.5 }}>
          {selectedFiles.size > 0 && (
            <Chip
              size="small"
              label={`${selectedFiles.size} ${selectedFiles.size === 1 ? 'file' : 'files'} selected`}
              sx={{ bgcolor: 'rgba(79, 70, 229, 0.08)', color: 'primary.main' }}
            />
          )}
          {estimate && estimate.estimatedTokens > 0 && (
            <Chip
              size="small"
              variant="outlined"
              title={estimate.explanation}
              label={`~${estimate.estimatedTokens.toLocaleString()} tokens · ${formatDuration(estimate.estimatedSeconds)}`}
              sx={{ color: 'text.secondary', borderStyle: estimate.sampleSize < 2 ? 'dashed' : 'solid' }}
            />
          )}
          {pinnedSnippet && (
            <Chip
              size="small"
              icon={<PushPinOutlinedIcon sx={{ fontSize: 12 }} />}
              label={`Lines ${pinnedSnippet.startLine}–${pinnedSnippet.endLine} of ${baseFileName(pinnedSnippet.absolutePath)}`}
              onDelete={() => pinSnippet(null)}
              sx={{ bgcolor: 'rgba(79, 70, 229, 0.08)', color: 'primary.main' }}
            />
          )}
        </Stack>
        <Button
          variant="contained"
          startIcon={
            runMutation.isPending ? (
              <CircularProgress size={14} color="inherit" />
            ) : (
              <PlayArrowIcon sx={{ fontSize: 18 }} />
            )
          }
          onClick={handleRun}
          disabled={!canRun}
        >
          Run Analysis
        </Button>
      </Stack>

      {runMutation.isError && (
        <Alert severity="error" sx={{ mt: 2, fontSize: '0.75rem' }}>
          {(runMutation.error as Error).message}
        </Alert>
      )}
    </Box>
  );
}
