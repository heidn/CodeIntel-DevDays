import { createTheme } from '@mui/material/styles';

const theme = createTheme({
  palette: {
    mode: 'light',
    background: {
      default: '#f1f5f9',
      paper: '#ffffff',
    },
    primary: {
      main: '#4f46e5',
      dark: '#4338ca',
    },
    secondary: {
      main: '#7c3aed',
    },
    success: {
      main: '#16a34a',
    },
    warning: {
      main: '#ca8a04',
    },
    error: {
      main: '#dc2626',
    },
    info: {
      main: '#0284c7',
    },
    divider: 'rgba(0,0,0,0.08)',
    text: {
      primary: '#0f172a',
      secondary: '#64748b',
    },
  },
  typography: {
    fontFamily: '"Inter Tight", -apple-system, system-ui, sans-serif',
    fontWeightLight: 400,
    fontWeightRegular: 500,
    fontWeightMedium: 600,
    fontWeightBold: 700,
    h1: { fontSize: '1.875rem', fontWeight: 700, letterSpacing: '-0.02em' },
    h2: { fontSize: '1.5rem', fontWeight: 700, letterSpacing: '-0.015em' },
    h3: { fontSize: '1.25rem', fontWeight: 600, letterSpacing: '-0.01em' },
    h4: { fontSize: '1.0625rem', fontWeight: 600 },
    h5: { fontSize: '0.9375rem', fontWeight: 600 },
    h6: { fontSize: '0.8125rem', fontWeight: 600, textTransform: 'uppercase', letterSpacing: '0.05em' },
    body1: { fontSize: '0.875rem', lineHeight: 1.55 },
    body2: { fontSize: '0.8125rem', lineHeight: 1.5 },
    button: { textTransform: 'none', fontWeight: 600, letterSpacing: 0 },
    caption: { fontSize: '0.75rem', letterSpacing: '0.01em' },
    overline: { fontSize: '0.6875rem', fontWeight: 600, letterSpacing: '0.08em' },
  },
  shape: {
    borderRadius: 6,
  },
  components: {
    MuiCssBaseline: {
      styleOverrides: {
        body: {
          scrollbarColor: '#cbd5e1 #f1f5f9',
        },
        '*::selection': {
          background: 'rgba(79, 70, 229, 0.15)',
        },
        '*::-webkit-scrollbar': {
          width: '10px',
          height: '10px',
        },
        '*::-webkit-scrollbar-track': {
          background: '#f1f5f9',
        },
        '*::-webkit-scrollbar-thumb': {
          background: '#cbd5e1',
          borderRadius: '6px',
        },
        '*::-webkit-scrollbar-thumb:hover': {
          background: '#94a3b8',
        },
        'code, pre, .mono': {
          fontFamily: '"JetBrains Mono", "SF Mono", Consolas, monospace',
        },
      },
    },
    MuiPaper: {
      defaultProps: { elevation: 0 },
      styleOverrides: {
        root: {
          backgroundImage: 'none',
          border: '1px solid rgba(0,0,0,0.08)',
        },
      },
    },
    MuiButton: {
      defaultProps: { disableElevation: true },
      styleOverrides: {
        root: {
          borderRadius: 5,
          padding: '6px 14px',
          fontSize: '0.8125rem',
        },
        contained: {
          boxShadow: 'none',
          '&:hover': { boxShadow: 'none' },
        },
      },
    },
    MuiAppBar: {
      defaultProps: { elevation: 0 },
      styleOverrides: {
        root: {
          backgroundColor: '#ffffff',
          borderBottom: '1px solid rgba(0,0,0,0.08)',
          backgroundImage: 'none',
          color: '#0f172a',
        },
      },
    },
    MuiDrawer: {
      styleOverrides: {
        paper: {
          backgroundColor: '#f8fafc',
          borderRight: '1px solid rgba(0,0,0,0.08)',
          backgroundImage: 'none',
        },
      },
    },
    MuiTextField: {
      defaultProps: { variant: 'outlined', size: 'small' },
    },
    MuiOutlinedInput: {
      styleOverrides: {
        root: {
          fontSize: '0.8125rem',
          backgroundColor: '#ffffff',
        },
      },
    },
    MuiChip: {
      styleOverrides: {
        root: {
          fontWeight: 500,
          fontSize: '0.6875rem',
          height: '20px',
        },
      },
    },
    MuiCheckbox: {
      defaultProps: { size: 'small' },
    },
    MuiTooltip: {
      styleOverrides: {
        tooltip: {
          fontSize: '0.75rem',
          backgroundColor: '#1e293b',
          border: '1px solid rgba(0,0,0,0.1)',
        },
      },
    },
    MuiToggleButton: {
      styleOverrides: {
        root: {
          '&.Mui-selected': {
            backgroundColor: 'rgba(79, 70, 229, 0.08)',
            color: '#4f46e5',
            '&:hover': {
              backgroundColor: 'rgba(79, 70, 229, 0.12)',
            },
          },
        },
      },
    },
    MuiLinearProgress: {
      styleOverrides: {
        root: {
          backgroundColor: 'rgba(79, 70, 229, 0.12)',
        },
      },
    },
  },
});

export default theme;
