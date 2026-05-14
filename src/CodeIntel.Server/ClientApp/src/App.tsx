import { useEffect } from 'react';
import { Box, AppBar, Toolbar, Typography, Chip, Stack } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { getStatus } from './api/analysis';
import { useWorkspaceStore } from './stores/workspaceStore';
import { useAnalysisStore } from './stores/analysisStore';
import { useTraceStore } from './stores/traceStore';
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

    // Route an event to either the analysis store or the trace store based on which has an
    // active run. Trace-specific event types always go to the trace store; analysis-specific
    // ones always go to the analysis store; shared types (status/started/completed/error/
    // cancelled) follow the active owner.
    function routeShared(handler: (kind: 'analysis' | 'trace') => void) {
      const traceState = useTraceStore.getState();
      const analysisState = useAnalysisStore.getState();
      const traceActive = traceState.currentTraceId && traceState.runState !== 'idle' && traceState.runState !== 'completed' && traceState.runState !== 'cancelled' && traceState.runState !== 'error';
      const analysisActive = analysisState.currentAnalysisId && analysisState.runState !== 'idle' && analysisState.runState !== 'completed' && analysisState.runState !== 'cancelled' && analysisState.runState !== 'error';
      if (traceActive) handler('trace');
      else if (analysisActive) handler('analysis');
    }

    const unsub = hub.subscribe((event) => {
      const traceActions = useTraceStore.getState();
      switch (event.type) {
        // Analysis-only
        case 'started':
          analysisActions.setStarted(event.payload.contextTokens, event.payload.fileCount);
          break;
        case 'token':
          analysisActions.appendToken(event.payload.text);
          break;
        case 'finding':
          analysisActions.addFinding(event.payload);
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

        // Trace-only
        case 'traceGraphReady':
          // The graph itself isn't on the event payload — fetch from the GET endpoint.
          // The trace store will be populated when the trace's run completes via API poll.
          // For now we just update status.
          traceActions.setStatus(`Walking graph (${event.payload.nodeCount} nodes)...`);
          break;
        case 'traceNodeSynopsis':
          traceActions.applySynopsis(event.payload.nodeId, event.payload.synopsis);
          break;

        // Shared — route by active owner
        case 'status':
          routeShared((kind) => kind === 'trace'
            ? traceActions.setStatus(event.payload.message)
            : analysisActions.setStatus(event.payload.message));
          break;
        case 'completed':
          routeShared((kind) => kind === 'trace'
            ? traceActions.complete(event.payload.durationSeconds)
            : analysisActions.complete(event.payload.durationSeconds, {
                incompleteFindings: event.payload.incompleteFindings ?? 0,
                malformedFindings:  event.payload.malformedFindings  ?? 0,
                reachedDone:        event.payload.reachedDone        ?? true,
              }));
          break;
        case 'error':
          routeShared((kind) => kind === 'trace'
            ? traceActions.error(event.payload.message)
            : analysisActions.error(event.payload.message));
          break;
        case 'cancelled':
          routeShared((kind) => kind === 'trace'
            ? traceActions.cancelled(event.payload.reason, event.payload.message)
            : analysisActions.cancelled(event.payload.reason, event.payload.message));
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
