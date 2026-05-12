import { useState, useEffect } from 'react';
import {
  Dialog,
  DialogTitle,
  DialogContent,
  DialogActions,
  Box,
  Button,
  Typography,
  IconButton,
  List,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Divider,
  Chip,
  CircularProgress,
  Alert,
  Tooltip,
} from '@mui/material';
import FolderIcon from '@mui/icons-material/FolderOutlined';
import ArrowUpwardIcon from '@mui/icons-material/ArrowUpward';
import StorageIcon from '@mui/icons-material/Storage';
import ArticleIcon from '@mui/icons-material/ArticleOutlined';
import { useQuery } from '@tanstack/react-query';
import { browseFolder } from '../api/workspace';
import type { BrowseProjectFile } from '../api/workspace';

const projectFileIcon: Record<string, string> = {
  sln: '⬡',
  csproj: '⬡',
  json: '{}',
  xml: '</>',
  gradle: '⚙',
  kts: '⚙',
};

const projectFileLabel: Record<string, string> = {
  sln: 'Solution',
  csproj: 'C# Project',
  json: 'TypeScript/Node',
  xml: 'Maven',
  gradle: 'Gradle',
  kts: 'Gradle',
};

interface Props {
  open: boolean;
  onClose: () => void;
  onSelect: (path: string) => void;
}

export default function FolderPickerDialog({ open, onClose, onSelect }: Props) {
  const [currentPath, setCurrentPath] = useState<string | undefined>(undefined);

  // Reset to home on open
  useEffect(() => {
    if (open) setCurrentPath(undefined);
  }, [open]);

  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['browse', currentPath ?? '__home__'],
    queryFn: () => browseFolder(currentPath),
    enabled: open,
    staleTime: 5000,
  });

  const handleSelectProjectFile = (file: BrowseProjectFile) => {
    onSelect(file.path);
    onClose();
  };

  const handleOpenFolder = () => {
    if (data) {
      onSelect(data.currentPath);
      onClose();
    }
  };

  const pathParts = data?.currentPath
    ? data.currentPath.replace(/\\/g, '/').split('/').filter(Boolean)
    : [];

  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth slotProps={{ paper: { sx: { height: 520 } } }}>
      <DialogTitle sx={{ pb: 1 }}>
        <Typography variant="h6" sx={{ fontSize: '0.9375rem', fontWeight: 600 }}>
          Open Project
        </Typography>
      </DialogTitle>

      <Divider />

      {/* Breadcrumb + up button */}
      <Box sx={{ px: 2, py: 1, display: 'flex', alignItems: 'center', gap: 0.5, flexWrap: 'wrap', borderBottom: '1px solid', borderColor: 'divider', bgcolor: 'background.default' }}>
        <Tooltip title="Go up">
          <span>
            <IconButton
              size="small"
              onClick={() => data?.parentPath && setCurrentPath(data.parentPath)}
              disabled={!data?.parentPath}
            >
              <ArrowUpwardIcon sx={{ fontSize: 16 }} />
            </IconButton>
          </span>
        </Tooltip>
        {pathParts.map((part, i) => {
          const partPath = pathParts.slice(0, i + 1).join('/');
          const fullPath = data?.currentPath.startsWith('\\') || /^[A-Z]:/i.test(data?.currentPath ?? '')
            ? pathParts.slice(0, i + 1).join('\\') + (i === 0 ? '\\' : '')
            : '/' + partPath;
          return (
            <Box key={i} sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
              {i > 0 && <Typography variant="caption" color="text.secondary">/</Typography>}
              <Typography
                variant="caption"
                sx={{
                  cursor: i < pathParts.length - 1 ? 'pointer' : 'default',
                  color: i < pathParts.length - 1 ? 'primary.main' : 'text.primary',
                  fontFamily: '"JetBrains Mono", monospace',
                  fontWeight: i === pathParts.length - 1 ? 600 : 400,
                  '&:hover': i < pathParts.length - 1 ? { textDecoration: 'underline' } : {},
                }}
                onClick={() => i < pathParts.length - 1 && setCurrentPath(fullPath)}
              >
                {part}
              </Typography>
            </Box>
          );
        })}
      </Box>

      <DialogContent sx={{ p: 0, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
        {isLoading && (
          <Box sx={{ display: 'flex', justifyContent: 'center', pt: 4 }}>
            <CircularProgress size={24} />
          </Box>
        )}
        {isError && (
          <Alert severity="error" sx={{ m: 2, fontSize: '0.8125rem' }}>
            {(error as Error).message}
          </Alert>
        )}
        {data && (
          <Box sx={{ flex: 1, overflow: 'auto' }}>
            {/* Drives */}
            {data.drives.length > 1 && (
              <Box sx={{ px: 2, pt: 1.5, pb: 0.5 }}>
                <Typography variant="overline" color="text.secondary">Drives</Typography>
                <Box sx={{ display: 'flex', gap: 0.5, flexWrap: 'wrap', mt: 0.5 }}>
                  {data.drives.map((drive) => (
                    <Chip
                      key={drive}
                      label={drive}
                      size="small"
                      icon={<StorageIcon sx={{ fontSize: '14px !important' }} />}
                      onClick={() => setCurrentPath(drive)}
                      sx={{
                        fontFamily: '"JetBrains Mono", monospace',
                        bgcolor: data.currentPath.startsWith(drive) ? 'rgba(79,70,229,0.08)' : undefined,
                        color: data.currentPath.startsWith(drive) ? 'primary.main' : undefined,
                      }}
                    />
                  ))}
                </Box>
              </Box>
            )}

            {/* Project files found */}
            {data.projectFiles.length > 0 && (
              <>
                <Box sx={{ px: 2, pt: 1.5, pb: 0.5 }}>
                  <Typography variant="overline" color="text.secondary">Project Files Found</Typography>
                </Box>
                <List dense disablePadding>
                  {data.projectFiles.map((file) => (
                    <ListItemButton
                      key={file.path}
                      onClick={() => handleSelectProjectFile(file)}
                      sx={{
                        px: 2,
                        bgcolor: 'rgba(79,70,229,0.03)',
                        '&:hover': { bgcolor: 'rgba(79,70,229,0.07)' },
                      }}
                    >
                      <ListItemIcon sx={{ minWidth: 32 }}>
                        <Box sx={{ fontSize: '0.875rem', color: 'primary.main', fontFamily: 'monospace' }}>
                          {projectFileIcon[file.type] ?? <ArticleIcon sx={{ fontSize: 16 }} />}
                        </Box>
                      </ListItemIcon>
                      <ListItemText
                        primary={
                          <Typography variant="body2" sx={{ fontWeight: 600, fontFamily: '"JetBrains Mono", monospace' }}>
                            {file.name}
                          </Typography>
                        }
                        secondary={
                          <Typography variant="caption">{projectFileLabel[file.type] ?? file.type}</Typography>
                        }
                      />
                    </ListItemButton>
                  ))}
                </List>
                <Divider />
              </>
            )}

            {/* Subdirectories */}
            {data.directories.length > 0 ? (
              <>
                <Box sx={{ px: 2, pt: 1.5, pb: 0.5 }}>
                  <Typography variant="overline" color="text.secondary">Folders</Typography>
                </Box>
                <List dense disablePadding>
                  {data.directories.map((dir) => (
                    <ListItemButton key={dir.path} onClick={() => setCurrentPath(dir.path)} sx={{ px: 2 }}>
                      <ListItemIcon sx={{ minWidth: 32 }}>
                        <FolderIcon sx={{ fontSize: 18, color: 'warning.main' }} />
                      </ListItemIcon>
                      <ListItemText
                        primary={<Typography variant="body2">{dir.name}</Typography>}
                      />
                    </ListItemButton>
                  ))}
                </List>
              </>
            ) : (
              !isLoading && data.projectFiles.length === 0 && (
                <Box sx={{ p: 3, textAlign: 'center' }}>
                  <Typography variant="caption" color="text.secondary">
                    No subdirectories or project files found
                  </Typography>
                </Box>
              )
            )}
          </Box>
        )}
      </DialogContent>

      <Divider />
      <DialogActions sx={{ px: 2, py: 1.5 }}>
        <Typography variant="caption" color="text.secondary" sx={{ flex: 1, fontFamily: '"JetBrains Mono", monospace', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {data?.currentPath ?? ''}
        </Typography>
        <Button onClick={onClose} size="small">Cancel</Button>
        <Button
          variant="contained"
          size="small"
          onClick={handleOpenFolder}
          disabled={!data}
        >
          Open This Folder
        </Button>
      </DialogActions>
    </Dialog>
  );
}
