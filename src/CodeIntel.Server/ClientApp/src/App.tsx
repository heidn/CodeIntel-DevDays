import { useEffect } from 'react';
import { Box, AppBar, Toolbar, Typography, Chip, Stack } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { getStatus } from './api/analysis';
import { useWorkspaceStore } from './stores/workspaceStore';
import { useAnalysisStore } from './stores/analysisStore';
import { getAnalysisHub } from './api/signalr';
import SolutionPanel from './components/SolutionPanel';
import AnalysisPanel from './components/AnalysisPanel';
import StatusDot from './components/StatusDot';

export default function App() {
  const workspace = useWorkspaceStore((s) => s.workspace);
  const analysisActions = useAnalysisStore();

  const { data: status } = useQuery({
    queryKey: ['llm-status'],
    queryFn: getStatus,
    refetchInterval: (q) => (q.state.data?.llmReady ? false : 3000),
  });

  // wire SignalR once
  useEffect(() => {
    const hub = getAnalysisHub();
    const unsub = hub.subscribe((event) => {
      switch (event.type) {
        case 'status':
          analysisActions.setStatus(event.payload.message);
          break;
        case 'started':
          analysisActions.setStarted(event.payload.contextTokens, event.payload.fileCount);
          break;
        case 'token':
          analysisActions.appendToken(event.payload.text);
          break;
        case 'finding':
          analysisActions.addFinding(event.payload);
          break;
        case 'completed':
          analysisActions.complete(event.payload.durationSeconds);
          break;
        case 'error':
          analysisActions.error(event.payload.message);
          break;
        case 'iterationStarted':
          if (event.payload.iteration > 1)
            analysisActions.setStatus(`Investigation pass ${event.payload.iteration}/${event.payload.maxIterations}`);
          break;
        case 'contextRequested':
          analysisActions.setStatus(`Requesting ${event.payload.type}: ${event.payload.target}`);
          break;
        case 'contextFulfilled':
          break;
      }
    });
    hub.start().catch((e) => console.error('SignalR start failed', e));
    return () => {
      unsub();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100vh' }}>
      <AppBar position="static">
        <Toolbar variant="dense" sx={{ minHeight: 48, gap: 2 }}>
          <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', flexGrow: 1 }}>
            <Box
              sx={{
                width: 22,
                height: 22,
                borderRadius: '5px',
                background: 'linear-gradient(135deg, #4f46e5 0%, #7c3aed 100%)',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                fontFamily: '"JetBrains Mono", monospace',
                fontSize: '0.6875rem',
                fontWeight: 700,
                color: '#ffffff',
              }}
            >
              CI
            </Box>
            <Typography variant="h6" sx={{ textTransform: 'none', letterSpacing: 0, fontSize: '0.9375rem' }}>
              Code Intelligence
            </Typography>
            {workspace && (
              <Chip
                size="small"
                label={workspace.projectName}
                sx={{
                  ml: 1,
                  bgcolor: 'rgba(122, 162, 255, 0.1)',
                  color: 'primary.main',
                  fontFamily: '"JetBrains Mono", monospace',
                }}
              />
            )}
          </Stack>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <StatusDot ready={!!status?.llmReady} />
            <Typography
              variant="caption"
              color="text.secondary"
              sx={{ fontFamily: '"JetBrains Mono", monospace' }}
            >
              {status?.llmReady
                ? `${status.modelName} · ${status.backendName}`
                : 'loading model...'}
            </Typography>
          </Stack>
        </Toolbar>
      </AppBar>

      <Box sx={{ display: 'flex', flex: 1, minHeight: 0 }}>
        <SolutionPanel />
        <AnalysisPanel />
      </Box>
    </Box>
  );
}
