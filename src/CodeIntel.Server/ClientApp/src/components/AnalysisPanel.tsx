import { useCallback, useEffect, useRef, useState } from 'react';
import { Box, Stack, Typography, IconButton, Tooltip, ToggleButtonGroup, ToggleButton } from '@mui/material';
import CloseIcon from '@mui/icons-material/Close';
import InsertDriveFileOutlinedIcon from '@mui/icons-material/InsertDriveFileOutlined';
import SubjectIcon from '@mui/icons-material/SubjectOutlined';
import AccountTreeIcon from '@mui/icons-material/AccountTreeOutlined';
import PromptSelector from './PromptSelector';
import ResultsView from './ResultsView';
import TracePanel from './TracePanel';
import TraceResultsView from './TraceResultsView';
import FilePreviewPanel from './FilePreviewPanel';
import WelcomePanel from './WelcomePanel';
import { useWorkspaceStore } from '../stores/workspaceStore';

function fileName(path: string): string {
  return path.replace(/\\/g, '/').split('/').pop() ?? path;
}

const MIN_PREVIEW_WIDTH = 250;
const MIN_ANALYSIS_WIDTH = 320;

export default function AnalysisPanel() {
  const workspace     = useWorkspaceStore((s) => s.workspace);
  const previewedFile = useWorkspaceStore((s) => s.previewedFile);
  const paneMode      = useWorkspaceStore((s) => s.paneMode);
  const setPaneMode   = useWorkspaceStore((s) => s.setPaneMode);
  const [openFileTabs, setOpenFileTabs] = useState<string[]>([]);
  const [activeTab, setActiveTab]       = useState<string | null>(null);
  const [previewWidth, setPreviewWidth] = useState(560);
  const [scrollTargets, setScrollTargets] = useState<Record<string, number>>({});

  const containerRef   = useRef<HTMLDivElement>(null);
  const isDragging     = useRef(false);
  const dragStartX     = useRef(0);
  const dragStartWidth = useRef(0);

  const onDragStart = useCallback((e: React.MouseEvent) => {
    isDragging.current    = true;
    dragStartX.current    = e.clientX;
    dragStartWidth.current = previewWidth;
    e.preventDefault();
  }, [previewWidth]);

  useEffect(() => {
    function onMouseMove(e: MouseEvent) {
      if (!isDragging.current || !containerRef.current) return;
      const delta    = dragStartX.current - e.clientX;
      const totalW   = containerRef.current.offsetWidth;
      const maxPreview = totalW - MIN_ANALYSIS_WIDTH;
      setPreviewWidth(Math.max(MIN_PREVIEW_WIDTH, Math.min(dragStartWidth.current + delta, maxPreview)));
    }
    function onMouseUp() { isDragging.current = false; }
    window.addEventListener('mousemove', onMouseMove);
    window.addEventListener('mouseup', onMouseUp);
    return () => {
      window.removeEventListener('mousemove', onMouseMove);
      window.removeEventListener('mouseup', onMouseUp);
    };
  }, []);

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
      if (activeTab === path) {
        const idx = prev.indexOf(path);
        setActiveTab(next[idx - 1] ?? next[0] ?? null);
      }
      return next;
    });
  }

  const handleNavigate = useCallback((filePath: string, line: number) => {
    setOpenFileTabs((prev) => prev.includes(filePath) ? prev : [...prev, filePath]);
    setActiveTab(filePath);
    setScrollTargets((prev) => ({ ...prev, [filePath]: line }));
  }, []);

  const hasFiles   = openFileTabs.length > 0;
  const activeWsId = workspace?.id ?? '';

  return (
    <Box ref={containerRef} sx={{ flex: 1, display: 'flex', minWidth: 0, height: '100%' }}>

      {/* ── Analysis pane (always visible, left) ── */}
      <Box
        sx={{
          display: 'flex',
          flexDirection: 'column',
          flex: 1,
          minWidth: MIN_ANALYSIS_WIDTH,
          height: '100%',
        }}
      >
        {!workspace ? (
          <WelcomePanel />
        ) : (
          <>
            {/* Mode toggle: Analysis | Trace */}
            <Stack
              direction="row"
              sx={{
                px: 2,
                py: 1,
                borderBottom: '1px solid',
                borderColor: 'divider',
                bgcolor: 'background.paper',
                flexShrink: 0,
                alignItems: 'center',
                gap: 1,
              }}
            >
              <ToggleButtonGroup
                value={paneMode}
                exclusive
                size="small"
                onChange={(_, v) => v && setPaneMode(v)}
                sx={{
                  '& .MuiToggleButton-root': {
                    py: 0.25, px: 1.25,
                    fontSize: '0.72rem',
                    textTransform: 'none',
                    border: '1px solid',
                    borderColor: 'divider',
                    gap: 0.5,
                  },
                }}
              >
                <ToggleButton value="analysis">
                  <SubjectIcon sx={{ fontSize: 14 }} /> Analysis
                </ToggleButton>
                <ToggleButton value="trace">
                  <AccountTreeIcon sx={{ fontSize: 14 }} /> Trace
                </ToggleButton>
              </ToggleButtonGroup>
            </Stack>

            <Stack
              sx={{ flex: 1, minHeight: 0 }}
              divider={<Box sx={{ borderBottom: '1px solid', borderColor: 'divider' }} />}
            >
              {paneMode === 'analysis'
                ? (<><PromptSelector /><ResultsView /></>)
                : (<><TracePanel /><TraceResultsView /></>)}
            </Stack>
          </>
        )}
      </Box>

      {/* ── File preview pane (right, only when tabs are open) ── */}
      {hasFiles && (
        <>
        {/* Drag handle */}
        <Box
          onMouseDown={onDragStart}
          sx={{
            width: 5,
            flexShrink: 0,
            cursor: 'col-resize',
            bgcolor: 'divider',
            position: 'relative',
            transition: 'background-color 0.15s',
            '&:hover': { bgcolor: 'primary.main' },
            '&::after': {
              content: '""',
              position: 'absolute',
              inset: '0 -3px',
            },
          }}
        />
        <Box
          sx={{
            width: previewWidth,
            flexShrink: 0,
            display: 'flex',
            flexDirection: 'column',
            minWidth: MIN_PREVIEW_WIDTH,
            height: '100%',
          }}
        >

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
            {openFileTabs.map((path) => (
              <FileTab
                key={path}
                active={activeTab === path}
                label={fileName(path)}
                onClick={() => setActiveTab(path)}
                onClose={(e) => closeTab(path, e)}
              />
            ))}
          </Box>

          {/* Tab content — all mounted, only active one is visible */}
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
              {/* Path breadcrumb */}
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
                  scrollToLine={scrollTargets[path]}
                  isActive={activeTab === path}
                  onNavigate={handleNavigate}
                />
              )}
            </Box>
          ))}
        </Box>
        </>
      )}
    </Box>
  );
}

// ── FileTab ──────────────────────────────────────────────────────────────────

interface FileTabProps {
  active: boolean;
  label: string;
  onClick: () => void;
  onClose: (e: React.MouseEvent) => void;
}

function FileTab({ active, label, onClick, onClose }: FileTabProps) {
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
      <Box sx={{ display: 'flex', flexShrink: 0 }}>
        <InsertDriveFileOutlinedIcon sx={{ fontSize: 13 }} />
      </Box>
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
    </Box>
  );
}
