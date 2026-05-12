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
} from '@mui/material';
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import SkullIcon from '@mui/icons-material/HeartBrokenOutlined';
import BugReportIcon from '@mui/icons-material/BugReportOutlined';
import GavelIcon from '@mui/icons-material/GavelOutlined';
import AutoStoriesIcon from '@mui/icons-material/AutoStoriesOutlined';
import { useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { getPresets, startAnalysis } from '../api/analysis';
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
};

export default function PromptSelector() {
  const [mode, setMode] = useState<Mode>('preset');
  const workspace = useWorkspaceStore((s) => s.workspace);
  const selectedFiles = useWorkspaceStore((s) => s.selectedFiles);

  const selectedPresetKey = useAnalysisStore((s) => s.selectedPresetKey);
  const freeTextPrompt = useAnalysisStore((s) => s.freeTextPrompt);
  const setPreset = useAnalysisStore((s) => s.setPreset);
  const setFreeText = useAnalysisStore((s) => s.setFreeText);
  const startRun = useAnalysisStore((s) => s.startRun);
  const runState = useAnalysisStore((s) => s.runState);

  const { data: presets = [] } = useQuery({ queryKey: ['presets'], queryFn: getPresets });

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

      <Stack direction="row" sx={{ alignItems: 'center', justifyContent: 'space-between' }}>
        <Stack direction="row" spacing={1}>
          {selectedFiles.size > 0 && (
            <Chip
              size="small"
              label={`${selectedFiles.size} ${selectedFiles.size === 1 ? 'file' : 'files'} selected`}
              sx={{ bgcolor: 'rgba(79, 70, 229, 0.08)', color: 'primary.main' }}
            />
          )}
          {selectedFiles.size === 0 && workspace && (
            <Typography variant="caption" color="warning.main">
              Select files in the left panel to run analysis
            </Typography>
          )}
          {!workspace && (
            <Typography variant="caption" color="text.secondary">
              Load a solution to begin
            </Typography>
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
