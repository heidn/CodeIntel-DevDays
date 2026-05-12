import { useEffect, useRef, useState } from 'react';
import { Box, Stack, Typography, IconButton, Tooltip } from '@mui/material';
import CloseIcon from '@mui/icons-material/Close';
import AnalyticsOutlinedIcon from '@mui/icons-material/AnalyticsOutlined';
import InsertDriveFileOutlinedIcon from '@mui/icons-material/InsertDriveFileOutlined';
import PromptSelector from './PromptSelector';
import ResultsView from './ResultsView';
import FilePreviewPanel from './FilePreviewPanel';
import { useWorkspaceStore } from '../stores/workspaceStore';

const ANALYSIS_TAB = '__analysis__';

function fileName(path: string): string {
  return path.replace(/\\/g, '/').split('/').pop() ?? path;
}

export default function AnalysisPanel() {
  const workspace     = useWorkspaceStore((s) => s.workspace);
  const previewedFile = useWorkspaceStore((s) => s.previewedFile);

  const [openFileTabs, setOpenFileTabs] = useState<string[]>([]);
  const [activeTab, setActiveTab]       = useState<string>(ANALYSIS_TAB);

  // When a new file is previewed from the left nav, open/focus its tab
  const prevPreviewedRef = useRef<string | null>(null);
  useEffect(() => {
    if (!previewedFile || previewedFile === prevPreviewedRef.current) return;
    prevPreviewedRef.current = previewedFile;
    setOpenFileTabs((prev) =>
      prev.includes(previewedFile) ? prev : [...prev, previewedFile]
    );
    setActiveTab(previewedFile);
  }, [previewedFile]);

  function closeTab(path: string, e: React.MouseEvent) {
    e.stopPropagation();
    setOpenFileTabs((prev) => {
      const next = prev.filter((p) => p !== path);
      // If we just closed the active tab, go to the one before it or Analysis
      if (activeTab === path) {
        const idx = prev.indexOf(path);
        const fallback = next[idx - 1] ?? next[0] ?? ANALYSIS_TAB;
        setActiveTab(fallback);
      }
      return next;
    });
  }

  const isFileTab   = activeTab !== ANALYSIS_TAB;
  const activeWsId  = workspace?.id ?? '';

  return (
    <Box sx={{ flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0, height: '100%' }}>
      {/* Tab bar */}
      <Box
        sx={{
          display: 'flex',
          alignItems: 'stretch',
          borderBottom: '1px solid',
          borderColor: 'divider',
          bgcolor: 'background.default',
          overflowX: 'auto',
          flexShrink: 0,
          '&::-webkit-scrollbar': { height: 3 },
          '&::-webkit-scrollbar-thumb': { bgcolor: 'rgba(0,0,0,0.15)', borderRadius: 2 },
        }}
      >
        {/* Analysis tab */}
        <Tab
          active={activeTab === ANALYSIS_TAB}
          onClick={() => setActiveTab(ANALYSIS_TAB)}
          icon={<AnalyticsOutlinedIcon sx={{ fontSize: 13 }} />}
          label="Analysis"
        />

        {/* File tabs */}
        {openFileTabs.map((path) => (
          <Tab
            key={path}
            active={activeTab === path}
            onClick={() => setActiveTab(path)}
            onClose={(e) => closeTab(path, e)}
            icon={<InsertDriveFileOutlinedIcon sx={{ fontSize: 13 }} />}
            label={fileName(path)}
          />
        ))}
      </Box>

      {/* Tab content */}
      <Box sx={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
        {/* Analysis tab — always mounted so results are never unmounted/cleared */}
        <Box
          sx={{
            flex: 1,
            display: activeTab === ANALYSIS_TAB ? 'flex' : 'none',
            flexDirection: 'column',
            minHeight: 0,
          }}
        >
          <Stack
            sx={{ flex: 1, minHeight: 0 }}
            divider={<Box sx={{ borderBottom: '1px solid', borderColor: 'divider' }} />}
          >
            <PromptSelector />
            <ResultsView />
          </Stack>
        </Box>

        {/* File preview tabs */}
        {openFileTabs.map((path) => (
          <Box
            key={path}
            sx={{
              flex: 1,
              display: activeTab === path ? 'flex' : 'none',
              flexDirection: 'column',
              minHeight: 0,
            }}
          >
            {/* File path breadcrumb */}
            <Box
              sx={{
                px: 2,
                py: 0.75,
                borderBottom: '1px solid',
                borderColor: 'divider',
                bgcolor: 'background.paper',
                flexShrink: 0,
              }}
            >
              <Typography
                variant="caption"
                sx={{
                  fontFamily: '"JetBrains Mono", monospace',
                  color: 'text.secondary',
                  fontSize: '0.7rem',
                }}
              >
                {path.replace(/\\/g, '/')}
              </Typography>
            </Box>

            {activeWsId && (
              <FilePreviewPanel
                workspaceId={activeWsId}
                absolutePath={path}
              />
            )}
          </Box>
        ))}

        {/* Empty state when a file tab is active but workspace isn't loaded yet */}
        {isFileTab && !activeWsId && (
          <Box sx={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
            <Typography variant="caption" color="text.secondary">No workspace loaded.</Typography>
          </Box>
        )}
      </Box>
    </Box>
  );
}

interface TabProps {
  active: boolean;
  label: string;
  icon?: React.ReactNode;
  onClick: () => void;
  onClose?: (e: React.MouseEvent) => void;
}

function Tab({ active, label, icon, onClick, onClose }: TabProps) {
  return (
    <Box
      onClick={onClick}
      sx={{
        display: 'flex',
        alignItems: 'center',
        gap: 0.75,
        px: 1.5,
        py: 0,
        minWidth: 0,
        maxWidth: 200,
        height: 36,
        cursor: 'pointer',
        flexShrink: 0,
        borderRight: '1px solid',
        borderColor: 'divider',
        bgcolor: active ? 'background.paper' : 'transparent',
        borderBottom: active ? '2px solid' : '2px solid transparent',
        borderBottomColor: active ? 'primary.main' : 'transparent',
        color: active ? 'primary.main' : 'text.secondary',
        transition: 'color 0.12s, background-color 0.12s',
        '&:hover': {
          bgcolor: active ? 'background.paper' : 'rgba(0,0,0,0.03)',
          color: active ? 'primary.main' : 'text.primary',
        },
      }}
    >
      {icon && <Box sx={{ display: 'flex', flexShrink: 0 }}>{icon}</Box>}
      <Typography
        variant="caption"
        noWrap
        sx={{
          fontSize: '0.75rem',
          fontFamily: '"JetBrains Mono", monospace',
          fontWeight: active ? 600 : 400,
          flex: 1,
          minWidth: 0,
        }}
      >
        {label}
      </Typography>
      {onClose && (
        <Tooltip title="Close" placement="top">
          <IconButton
            size="small"
            onClick={onClose}
            sx={{
              p: 0.25,
              ml: 0.25,
              flexShrink: 0,
              color: 'inherit',
              opacity: 0.5,
              '&:hover': { opacity: 1, bgcolor: 'rgba(0,0,0,0.08)' },
            }}
          >
            <CloseIcon sx={{ fontSize: 12 }} />
          </IconButton>
        </Tooltip>
      )}
    </Box>
  );
}
