import { Box, Typography, CircularProgress, Alert } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { getFile } from '../api/workspace';

interface Props {
  workspaceId: string;
  absolutePath: string;
}

export default function FilePreviewPanel({ workspaceId, absolutePath }: Props) {
  const { data, isLoading, error } = useQuery({
    queryKey: ['file', workspaceId, absolutePath],
    queryFn: () => getFile(workspaceId, absolutePath),
    staleTime: Infinity,
  });

  if (isLoading) {
    return (
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, p: 3, flex: 1 }}>
        <CircularProgress size={16} />
        <Typography variant="caption" color="text.secondary">Loading…</Typography>
      </Box>
    );
  }

  if (error || !data) {
    return <Alert severity="error" sx={{ m: 2 }}>Could not load file.</Alert>;
  }

  const lines = data.content.split('\n');
  const gutterWidth = String(lines.length).length;

  return (
    <Box
      sx={{
        flex: 1,
        minHeight: 0,
        overflow: 'auto',
        bgcolor: '#1a1a2e',
        fontFamily: '"JetBrains Mono", monospace',
        fontSize: '0.8rem',
        lineHeight: 1.6,
      }}
    >
      {lines.map((lineText, idx) => {
        const lineNo = idx + 1;
        return (
          <Box
            key={lineNo}
            sx={{
              display: 'flex',
              alignItems: 'stretch',
              borderLeft: '3px solid transparent',
              '&:hover': { bgcolor: 'rgba(255,255,255,0.03)' },
            }}
          >
            {/* Gutter */}
            <Box
              sx={{
                minWidth: `${gutterWidth + 2}ch`,
                px: 1,
                color: 'rgba(255,255,255,0.2)',
                textAlign: 'right',
                userSelect: 'none',
                flexShrink: 0,
                borderRight: '1px solid rgba(255,255,255,0.06)',
              }}
            >
              {lineNo}
            </Box>

            {/* Code */}
            <Box
              component="span"
              sx={{
                px: 1.5,
                whiteSpace: 'pre',
                color: 'rgba(255,255,255,0.78)',
                flex: 1,
                overflow: 'hidden',
              }}
            >
              {lineText || ' '}
            </Box>
          </Box>
        );
      })}
    </Box>
  );
}
