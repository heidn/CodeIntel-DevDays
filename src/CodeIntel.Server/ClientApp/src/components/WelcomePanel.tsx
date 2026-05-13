import { Box, Typography, Stack, Chip } from '@mui/material';
import FolderOpenOutlinedIcon from '@mui/icons-material/FolderOpenOutlined';
import PlaylistAddCheckOutlinedIcon from '@mui/icons-material/PlaylistAddCheckOutlined';
import TuneOutlinedIcon from '@mui/icons-material/TuneOutlined';
import AutoAwesomeOutlinedIcon from '@mui/icons-material/AutoAwesomeOutlined';
import CircleIcon from '@mui/icons-material/Circle';
import { useQuery } from '@tanstack/react-query';
import { getStatus } from '../api/analysis';

const steps = [
  {
    icon: <FolderOpenOutlinedIcon sx={{ fontSize: 20 }} />,
    title: 'Load a project',
    detail: 'Paste a .sln path or browse for a folder in the left panel, then click Load Project.',
  },
  {
    icon: <PlaylistAddCheckOutlinedIcon sx={{ fontSize: 20 }} />,
    title: 'Select files',
    detail: 'Check the files or whole project nodes you want analyzed. Start small — a handful of files gives fast, focused results.',
  },
  {
    icon: <TuneOutlinedIcon sx={{ fontSize: 20 }} />,
    title: 'Pick an analysis',
    detail: 'Choose a preset: Find Bugs, Dead Code, Business Rules, Summarize, and four PL/SQL presets. Or type a free-text question.',
  },
  {
    icon: <AutoAwesomeOutlinedIcon sx={{ fontSize: 20 }} />,
    title: 'Run — findings stream in live',
    detail: 'Results appear as the model works. Save the report into your repo, then reference it with #file: in GitHub Copilot Chat.',
  },
];

export default function WelcomePanel() {
  const { data: status } = useQuery({
    queryKey: ['llm-status'],
    queryFn: getStatus,
    refetchInterval: (q) => (q.state.data?.llmReady ? false : 3000),
  });

  const modelReady = !!status?.llmReady;

  return (
    <Box
      sx={{
        flex: 1,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        p: 4,
        bgcolor: 'background.default',
        minHeight: 0,
        overflow: 'auto',
      }}
    >
      <Box sx={{ maxWidth: 520, width: '100%' }}>
        {/* Header */}
        <Stack spacing={0.5} sx={{ mb: 4 }}>
          <Typography variant="h3" sx={{ color: 'text.primary', fontWeight: 700 }}>
            Code Intelligence
          </Typography>
          <Typography variant="body1" color="text.secondary">
            Local AI analysis that runs entirely on your machine — no data leaves your laptop.
            Findings are saved as Markdown into your repo for GitHub Copilot to act on.
          </Typography>
        </Stack>

        {/* Steps */}
        <Stack spacing={0}>
          {steps.map((step, i) => (
            <Stack
              key={i}
              direction="row"
              spacing={2}
              sx={{
                position: 'relative',
                pb: i < steps.length - 1 ? 3 : 0,
              }}
            >
              {/* Step number + connector line */}
              <Box sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center', flexShrink: 0 }}>
                <Box
                  sx={{
                    width: 36,
                    height: 36,
                    borderRadius: '50%',
                    bgcolor: 'rgba(79, 70, 229, 0.08)',
                    border: '1.5px solid rgba(79, 70, 229, 0.2)',
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'center',
                    color: 'primary.main',
                    flexShrink: 0,
                  }}
                >
                  {step.icon}
                </Box>
                {i < steps.length - 1 && (
                  <Box
                    sx={{
                      width: 1.5,
                      flex: 1,
                      mt: 0.5,
                      bgcolor: 'rgba(79, 70, 229, 0.15)',
                      minHeight: 20,
                    }}
                  />
                )}
              </Box>

              {/* Text */}
              <Box sx={{ pt: 0.5 }}>
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.25 }}>
                  <Typography
                    variant="caption"
                    sx={{
                      color: 'primary.main',
                      fontFamily: '"JetBrains Mono", monospace',
                      fontWeight: 600,
                    }}
                  >
                    {i + 1}
                  </Typography>
                  <Typography variant="body2" sx={{ fontWeight: 600, color: 'text.primary' }}>
                    {step.title}
                  </Typography>
                </Stack>
                <Typography variant="caption" color="text.secondary" sx={{ lineHeight: 1.5, display: 'block' }}>
                  {step.detail}
                </Typography>
              </Box>
            </Stack>
          ))}
        </Stack>

        {/* Trace callout */}
        <Box
          sx={{
            mt: 4,
            p: 2,
            bgcolor: 'background.paper',
            border: '1px solid',
            borderColor: 'divider',
            borderRadius: 1.5,
          }}
        >
          <Typography variant="caption" sx={{ fontWeight: 600, color: 'text.primary', display: 'block', mb: 0.5 }}>
            Bonus: Trace mode
          </Typography>
          <Typography variant="caption" color="text.secondary" sx={{ lineHeight: 1.5, display: 'block' }}>
            Switch to the <strong>Trace</strong> tab above once a project is loaded. Type a method name (e.g.{' '}
            <Box component="span" sx={{ fontFamily: '"JetBrains Mono", monospace', fontSize: '0.7rem' }}>
              OrderService.Submit
            </Box>
            ) and walk the call graph — callers, callees, or both — with an AI synopsis on each node.
          </Typography>
        </Box>

        {/* Model status */}
        <Stack direction="row" spacing={1} sx={{ mt: 3, alignItems: 'center' }}>
          <CircleIcon
            sx={{
              fontSize: 8,
              color: modelReady ? 'success.main' : 'warning.main',
            }}
          />
          <Typography variant="caption" color="text.secondary">
            {modelReady
              ? `Model ready — ${status?.modelName}`
              : 'Model loading… the status dot in the top-right will turn green when ready.'}
          </Typography>
          {modelReady && (
            <Chip
              size="small"
              label={status?.backendName}
              sx={{ height: 16, fontSize: '0.6rem', bgcolor: 'rgba(22,163,74,0.08)', color: 'success.main' }}
            />
          )}
        </Stack>
      </Box>
    </Box>
  );
}
