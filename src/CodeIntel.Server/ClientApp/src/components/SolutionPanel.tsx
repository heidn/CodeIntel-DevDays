import { useState } from 'react';
import {
  Box,
  TextField,
  Button,
  Typography,
  Stack,
  Alert,
  CircularProgress,
  Divider,
  IconButton,
  Tooltip,
} from '@mui/material';
import FolderOpenIcon from '@mui/icons-material/FolderOpen';
import DriveFileMoveIcon from '@mui/icons-material/DriveFileMove';
import ClearAllIcon from '@mui/icons-material/ClearAll';
import { useMutation } from '@tanstack/react-query';
import { loadSolution } from '../api/workspace';
import { useWorkspaceStore } from '../stores/workspaceStore';
import FileTree from './FileTree';
import FolderPickerDialog from './FolderPickerDialog';

export default function SolutionPanel() {
  const workspace = useWorkspaceStore((s) => s.workspace);
  const setWorkspace = useWorkspaceStore((s) => s.setWorkspace);
  const selectedFiles = useWorkspaceStore((s) => s.selectedFiles);
  const clearSelection = useWorkspaceStore((s) => s.clearSelection);

  const [path, setPath] = useState('');
  const [browseOpen, setBrowseOpen] = useState(false);

  const loadMutation = useMutation({
    mutationFn: loadSolution,
    onSuccess: (ws) => setWorkspace(ws),
  });

  const totalFiles = workspace?.projects.reduce((sum, p) => sum + p.files.length, 0) ?? 0;

  return (
    <Box
      sx={{
        width: 360,
        minWidth: 360,
        height: '100%',
        borderRight: '1px solid',
        borderColor: 'divider',
        display: 'flex',
        flexDirection: 'column',
        bgcolor: '#f8fafc',
      }}
    >
      <Box sx={{ p: 2, borderBottom: '1px solid', borderColor: 'divider' }}>
        <Typography variant="overline" color="text.secondary" sx={{ mb: 1, display: 'block' }}>
          Project
        </Typography>
        <Stack spacing={1}>
          <Stack direction="row" spacing={0.75}>
            <TextField
              fullWidth
              placeholder=".sln, tsconfig.json, or project dir"
              value={path}
              onChange={(e) => setPath(e.target.value)}
              onKeyDown={(e) => e.key === 'Enter' && path.trim() && !loadMutation.isPending && loadMutation.mutate(path.trim())}
              slotProps={{
                input: {
                  style: { fontFamily: '"JetBrains Mono", monospace', fontSize: '0.75rem' },
                },
              }}
              disabled={loadMutation.isPending}
            />
            <Tooltip title="Browse folders">
              <IconButton
                size="small"
                onClick={() => setBrowseOpen(true)}
                disabled={loadMutation.isPending}
                sx={{ border: '1px solid', borderColor: 'divider', borderRadius: 1, px: 1 }}
              >
                <DriveFileMoveIcon sx={{ fontSize: 18 }} />
              </IconButton>
            </Tooltip>
          </Stack>
          <Button
            variant="contained"
            startIcon={loadMutation.isPending ? <CircularProgress size={14} color="inherit" /> : <FolderOpenIcon sx={{ fontSize: 16 }} />}
            disabled={!path.trim() || loadMutation.isPending}
            onClick={() => loadMutation.mutate(path.trim())}
            fullWidth
          >
            {loadMutation.isPending ? 'Loading...' : 'Load Project'}
          </Button>
          {loadMutation.isError && (
            <Alert severity="error" sx={{ fontSize: '0.75rem', py: 0.5 }}>
              {(loadMutation.error as Error).message}
            </Alert>
          )}
        </Stack>

        <FolderPickerDialog
          open={browseOpen}
          onClose={() => setBrowseOpen(false)}
          onSelect={(selected) => {
            setPath(selected);
            setBrowseOpen(false);
          }}
        />
      </Box>

      {workspace && (
        <>
          <Box sx={{ px: 2, py: 1.5, borderBottom: '1px solid', borderColor: 'divider' }}>
            <Stack direction="row" sx={{ alignItems: 'center', justifyContent: 'space-between' }}>
              <Stack spacing={0.25}>
                <Typography variant="caption" color="text.secondary">
                  {workspace.projects.length} {workspace.projects.length === 1 ? 'project' : 'projects'} •{' '}
                  {totalFiles} files
                </Typography>
                <Typography
                  variant="caption"
                  color="primary.main"
                  sx={{ fontFamily: '"JetBrains Mono", monospace', fontWeight: 600 }}
                >
                  {selectedFiles.size} selected
                </Typography>
              </Stack>
              {selectedFiles.size > 0 && (
                <Tooltip title="Clear selection">
                  <IconButton size="small" onClick={clearSelection}>
                    <ClearAllIcon sx={{ fontSize: 16 }} />
                  </IconButton>
                </Tooltip>
              )}
            </Stack>
          </Box>
          <Divider />
          <Box sx={{ flex: 1, overflow: 'auto', p: 1 }}>
            <FileTree workspace={workspace} />
          </Box>
        </>
      )}

      {!workspace && (
        <Box
          sx={{
            flex: 1,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            p: 3,
            color: 'text.secondary',
          }}
        >
          <Typography variant="caption" sx={{ textAlign: 'center', maxWidth: 240 }}>
            Enter a path to a .sln, tsconfig.json, pom.xml, or project directory above and click load.
          </Typography>
        </Box>
      )}
    </Box>
  );
}
