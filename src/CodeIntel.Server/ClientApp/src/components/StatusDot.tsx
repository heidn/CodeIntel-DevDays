import { Box, keyframes } from '@mui/material';

const pulse = keyframes`
  0%, 100% { opacity: 1; transform: scale(1); }
  50% { opacity: 0.4; transform: scale(0.85); }
`;

export default function StatusDot({ ready }: { ready: boolean }) {
  return (
    <Box
      sx={{
        width: 8,
        height: 8,
        borderRadius: '50%',
        bgcolor: ready ? 'success.main' : 'warning.main',
        boxShadow: ready
          ? '0 0 8px rgba(82, 209, 138, 0.6)'
          : '0 0 8px rgba(244, 184, 96, 0.6)',
        animation: ready ? 'none' : `${pulse} 1.5s ease-in-out infinite`,
      }}
    />
  );
}
