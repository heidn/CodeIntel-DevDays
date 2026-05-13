import { useState } from 'react';
import {
  Box,
  Typography,
  Stack,
  Button,
  TextField,
  ToggleButtonGroup,
  ToggleButton,
  CircularProgress,
  Alert,
  Chip,
  Dialog,
  DialogTitle,
  DialogContent,
  DialogActions,
  List,
  ListItemButton,
  ListItemText,
} from '@mui/material';
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import AccountTreeIcon from '@mui/icons-material/AccountTreeOutlined';
import { useTraceStore } from '../stores/traceStore';
import { useWorkspaceStore } from '../stores/workspaceStore';
import { startTrace, resolveTraceCandidates } from '../api/trace';
import { getAnalysisHub } from '../api/signalr';
import type { TraceDirection, TraceEntryPoint, EntryPointCandidate } from '../types';

export default function TracePanel() {
  const workspace = useWorkspaceStore((s) => s.workspace);

  const entryPointName        = useTraceStore((s) => s.entryPointName);
  const entryPointLocation    = useTraceStore((s) => s.entryPointLocation);
  const direction             = useTraceStore((s) => s.direction);
  const depth                 = useTraceStore((s) => s.depth);
  const setEntryPointName     = useTraceStore((s) => s.setEntryPointName);
  const setEntryPointLocation = useTraceStore((s) => s.setEntryPointLocation);
  const setDirection          = useTraceStore((s) => s.setDirection);
  const setDepth              = useTraceStore((s) => s.setDepth);
  const startRun              = useTraceStore((s) => s.startRun);
  const runState              = useTraceStore((s) => s.runState);

  const [submitError, setSubmitError] = useState<string | null>(null);
  const [submitting, setSubmitting]   = useState(false);
  const [candidates,  setCandidates]  = useState<EntryPointCandidate[] | null>(null);

  const isRunning = runState === 'starting' || runState === 'building' || runState === 'streaming' || runState === 'cancelling';
  const hasEntryPoint = !!entryPointLocation || entryPointName.trim().length > 0;
  const canRun = !!workspace && hasEntryPoint && !isRunning;

  function buildEntryPoint(): TraceEntryPoint {
    return entryPointLocation
      ? { methodName: null, filePath: entryPointLocation.filePath, line: entryPointLocation.line, character: entryPointLocation.character }
      : { methodName: entryPointName.trim(), filePath: null, line: null, character: null };
  }

  async function launchTrace(entryPoint: TraceEntryPoint, preferredFqn?: string | null) {
    if (!workspace) return;
    const traceId = crypto.randomUUID();
    startRun(traceId);
    const hub = getAnalysisHub();
    await hub.joinAnalysis(traceId);
    await startTrace({
      workspaceId: workspace.id,
      entryPoint,
      direction,
      depth,
      traceId,
      preferredFqn: preferredFqn ?? null,
    });
  }

  async function handleRun() {
    if (!workspace) return;
    setSubmitError(null);
    setSubmitting(true);
    try {
      const entryPoint = buildEntryPoint();
      // Name-based entry point: probe for overloads first so the user picks
      // the right one before we kick off a multi-minute graph walk.
      if (!entryPointLocation && entryPoint.methodName) {
        const found = await resolveTraceCandidates(workspace.id, entryPoint);
        if (found.length > 1) {
          setCandidates(found);
          setSubmitting(false);
          return;
        }
      }
      await launchTrace(entryPoint);
    } catch (err) {
      const msg =
        (err as { response?: { data?: { error?: string } } })?.response?.data?.error ??
        (err instanceof Error ? err.message : 'Failed to start trace');
      setSubmitError(msg);
    } finally {
      setSubmitting(false);
    }
  }

  async function pickCandidate(c: EntryPointCandidate) {
    setCandidates(null);
    setSubmitError(null);
    setSubmitting(true);
    try {
      await launchTrace(buildEntryPoint(), c.fqn);
    } catch (err) {
      const msg =
        (err as { response?: { data?: { error?: string } } })?.response?.data?.error ??
        (err instanceof Error ? err.message : 'Failed to start trace');
      setSubmitError(msg);
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Box sx={{ p: 3, bgcolor: 'background.paper' }}>
      <Stack direction="row" sx={{ alignItems: 'center', mb: 2 }}>
        <AccountTreeIcon sx={{ fontSize: 18, mr: 1, color: 'primary.main' }} />
        <Typography variant="overline" color="text.secondary">Call-trail trace</Typography>
      </Stack>

      {entryPointLocation ? (
        <Stack spacing={0.75} sx={{ mb: 2 }}>
          <Typography variant="caption" color="text.secondary">Entry point</Typography>
          <Chip
            icon={<AccountTreeIcon sx={{ fontSize: 14 }} />}
            label={`${entryPointLocation.fileShortName}:${entryPointLocation.line}  ·  ${entryPointLocation.symbolLabel}`}
            onDelete={() => setEntryPointLocation(null)}
            size="small"
            sx={{
              fontFamily: '"JetBrains Mono", monospace',
              fontSize: '0.78rem',
              alignSelf: 'flex-start',
              maxWidth: '100%',
              bgcolor: 'rgba(139, 92, 246, 0.1)',
              border: '1px solid rgba(139, 92, 246, 0.35)',
              color: 'secondary.main',
              '& .MuiChip-deleteIcon': { color: 'secondary.main', opacity: 0.6, '&:hover': { opacity: 1 } },
            }}
          />
          <Typography variant="caption" color="text.disabled" sx={{ fontSize: '0.65rem' }}>
            Trace will resolve the declaration at this location. Delete the chip to type a name instead.
          </Typography>
        </Stack>
      ) : (
        <TextField
          label="Entry point"
          fullWidth
          size="small"
          placeholder="OrderService.Submit  or  Namespace.OrderService.Submit"
          value={entryPointName}
          onChange={(e) => setEntryPointName(e.target.value)}
          helperText="Type the method name. We'll resolve it through the loaded solution."
          disabled={isRunning}
          sx={{
            mb: 2,
            '& input': { fontFamily: '"JetBrains Mono", monospace', fontSize: '0.875rem' },
          }}
        />
      )}

      <Stack direction="row" spacing={2} sx={{ alignItems: 'center', mb: 2 }}>
        <Stack spacing={0.5}>
          <Typography variant="caption" color="text.secondary">Direction</Typography>
          <ToggleButtonGroup
            value={direction}
            exclusive
            size="small"
            onChange={(_, v: TraceDirection | null) => v && setDirection(v)}
            sx={{
              '& .MuiToggleButton-root': {
                py: 0.25, px: 1.5,
                fontSize: '0.72rem',
                textTransform: 'none',
                border: '1px solid',
                borderColor: 'divider',
              },
            }}
          >
            <ToggleButton value="callers">Callers</ToggleButton>
            <ToggleButton value="callees">Callees</ToggleButton>
            <ToggleButton value="both">Both</ToggleButton>
          </ToggleButtonGroup>
        </Stack>

        <Stack spacing={0.5}>
          <Typography variant="caption" color="text.secondary">Depth</Typography>
          <TextField
            type="number"
            size="small"
            value={depth}
            onChange={(e) => setDepth(Number(e.target.value))}
            slotProps={{ htmlInput: { min: 1, max: 5, style: { width: 50, textAlign: 'center' } } }}
          />
        </Stack>
      </Stack>

      <Stack direction="row" sx={{ alignItems: 'center', justifyContent: 'space-between' }}>
        <Typography variant="caption" color="text.secondary">
          {!workspace
            ? 'Load a solution to begin'
            : 'Tip: deeper traces produce more LLM calls. Start with depth 2.'}
        </Typography>
        <Button
          variant="contained"
          startIcon={submitting ? <CircularProgress size={14} color="inherit" /> : <PlayArrowIcon sx={{ fontSize: 18 }} />}
          onClick={handleRun}
          disabled={!canRun || submitting}
        >
          Run trace
        </Button>
      </Stack>

      {submitError && (
        <Alert severity="error" sx={{ mt: 2, fontSize: '0.75rem' }}>{submitError}</Alert>
      )}

      <Dialog open={!!candidates} onClose={() => setCandidates(null)} maxWidth="sm" fullWidth>
        <DialogTitle>Multiple matches for "{entryPointName}"</DialogTitle>
        <DialogContent dividers>
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
            Pick the overload to trace.
          </Typography>
          <List dense>
            {candidates?.map((c) => (
              <ListItemButton key={c.fqn} onClick={() => pickCandidate(c)}>
                <ListItemText
                  primary={c.signature}
                  secondary={`${c.filePath.split(/[\\/]/).pop()}:${c.line}`}
                  slotProps={{
                    primary:   { sx: { fontFamily: '"JetBrains Mono", monospace', fontSize: '0.85rem' } },
                    secondary: { sx: { fontFamily: '"JetBrains Mono", monospace', fontSize: '0.7rem' } },
                  }}
                />
              </ListItemButton>
            ))}
          </List>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setCandidates(null)}>Cancel</Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
