import { Box, Checkbox, Typography, Stack } from '@mui/material';
import FolderIcon from '@mui/icons-material/FolderOutlined';
import InsertDriveFileIcon from '@mui/icons-material/InsertDriveFileOutlined';
import { SimpleTreeView } from '@mui/x-tree-view/SimpleTreeView';
import { TreeItem } from '@mui/x-tree-view/TreeItem';
import type { Workspace } from '../types';
import { useWorkspaceStore } from '../stores/workspaceStore';

interface Props {
  workspace: Workspace;
}

export default function FileTree({ workspace }: Props) {
  const selectedFiles    = useWorkspaceStore((s) => s.selectedFiles);
  const previewedFile    = useWorkspaceStore((s) => s.previewedFile);
  const toggleFile       = useWorkspaceStore((s) => s.toggleFile);
  const selectFiles      = useWorkspaceStore((s) => s.selectFiles);
  const setPreviewedFile = useWorkspaceStore((s) => s.setPreviewedFile);

  const defaultExpanded = workspace.projects.map((p) => `proj-${p.name}`);

  return (
    <SimpleTreeView
      defaultExpandedItems={defaultExpanded}
      sx={{
        '& .MuiTreeItem-content': { py: 0.25, px: 0.5, borderRadius: 0.5 },
        '& .MuiTreeItem-label':   { fontSize: '0.8125rem' },
      }}
    >
      {workspace.projects.map((project) => {
        const projectFilePaths = project.files.map((f) => f.absolutePath);
        const allSelected  = projectFilePaths.length > 0 && projectFilePaths.every((p) => selectedFiles.has(p));
        const someSelected = projectFilePaths.some((p) => selectedFiles.has(p));

        return (
          <TreeItem
            key={project.name}
            itemId={`proj-${project.name}`}
            label={
              <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', py: 0.25 }}>
                <Checkbox
                  checked={allSelected}
                  indeterminate={!allSelected && someSelected}
                  onChange={(e) => { e.stopPropagation(); selectFiles(projectFilePaths, !allSelected); }}
                  onClick={(e) => e.stopPropagation()}
                  sx={{ p: 0.25 }}
                />
                <FolderIcon sx={{ fontSize: 16, color: 'primary.main' }} />
                <Typography variant="body2" sx={{ fontWeight: 600 }}>
                  {project.name}
                </Typography>
                <Typography variant="caption" color="text.secondary" sx={{ ml: 0.5 }}>
                  ({project.files.length})
                </Typography>
              </Stack>
            }
          >
            {project.files.map((file) => {
              const isSelected  = selectedFiles.has(file.absolutePath);
              const isPreviewed = previewedFile === file.absolutePath;

              return (
                <TreeItem
                  key={file.absolutePath}
                  itemId={file.absolutePath}
                  label={
                    <Stack
                      direction="row"
                      spacing={0.5}
                      sx={{
                        alignItems: 'center',
                        py: 0.25,
                        borderRadius: 0.5,
                        bgcolor: isPreviewed ? 'rgba(79,70,229,0.08)' : 'transparent',
                      }}
                    >
                      <Checkbox
                        checked={isSelected}
                        onChange={(e) => { e.stopPropagation(); toggleFile(file.absolutePath); }}
                        onClick={(e) => e.stopPropagation()}
                        sx={{ p: 0.25 }}
                      />

                      {/* Clickable filename area — opens preview */}
                      <Box
                        sx={{
                          display: 'flex',
                          alignItems: 'center',
                          gap: 0.5,
                          flex: 1,
                          minWidth: 0,
                          cursor: 'pointer',
                          '&:hover .file-name': { color: 'primary.main' },
                        }}
                        onClick={(e) => {
                          e.stopPropagation();
                          setPreviewedFile(file.absolutePath);
                        }}
                      >
                        <InsertDriveFileIcon
                          sx={{
                            fontSize: 14,
                            color: isPreviewed ? 'primary.main' : 'text.secondary',
                            flexShrink: 0,
                          }}
                        />
                        <Typography
                          className="file-name"
                          variant="body2"
                          noWrap
                          sx={{
                            fontFamily: '"JetBrains Mono", monospace',
                            fontSize: '0.75rem',
                            color: isPreviewed ? 'primary.main' : 'text.primary',
                            flex: 1,
                            minWidth: 0,
                          }}
                        >
                          {file.fileName}
                        </Typography>
                        <Typography
                          variant="caption"
                          color="text.secondary"
                          sx={{ fontSize: '0.6875rem', flexShrink: 0 }}
                        >
                          {file.lineCount}L
                        </Typography>
                      </Box>
                    </Stack>
                  }
                />
              );
            })}
          </TreeItem>
        );
      })}
    </SimpleTreeView>
  );
}
