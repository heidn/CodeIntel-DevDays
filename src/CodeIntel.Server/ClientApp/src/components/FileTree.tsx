import { useMemo } from 'react';
import { Box, Checkbox, Typography, Stack } from '@mui/material';
import FolderIcon from '@mui/icons-material/FolderOutlined';
import InsertDriveFileIcon from '@mui/icons-material/InsertDriveFileOutlined';
import { SimpleTreeView } from '@mui/x-tree-view/SimpleTreeView';
import { TreeItem } from '@mui/x-tree-view/TreeItem';
import type { FileNode, ProjectNode, Workspace } from '../types';
import { useWorkspaceStore } from '../stores/workspaceStore';

interface Props {
  workspace: Workspace;
}

interface FolderTreeNode {
  name: string;
  id: string;
  folders: FolderTreeNode[];
  files: FileNode[];
}

function projectDirOf(project: ProjectNode): string {
  // ProjectNode.path may be a .csproj/.sln file or a directory (TS/Java/SQL scans).
  if (/\.(csproj|sln|fsproj|vbproj)$/i.test(project.path)) {
    return project.path.replace(/[\\/][^\\/]+$/, '');
  }
  return project.path;
}

function relPathWithinProject(file: FileNode, projectDir: string): string {
  // Try absolutePath minus projectDir; fall back to file.relativePath if it doesn't share the prefix.
  if (projectDir && file.absolutePath.toLowerCase().startsWith(projectDir.toLowerCase())) {
    return file.absolutePath.slice(projectDir.length).replace(/^[\\/]+/, '');
  }
  return file.relativePath.replace(/^[\\/]+/, '');
}

function buildFolderTree(project: ProjectNode): FolderTreeNode {
  const projectDir = projectDirOf(project);
  const root: FolderTreeNode = {
    name: project.name,
    id: `proj-${project.name}`,
    folders: [],
    files: [],
  };

  for (const file of project.files) {
    const rel = relPathWithinProject(file, projectDir);
    const parts = rel.split(/[\\/]/).filter(Boolean);
    const folderSegments = parts.slice(0, -1);

    let current = root;
    for (const segment of folderSegments) {
      let child = current.folders.find((f) => f.name === segment);
      if (!child) {
        child = {
          name: segment,
          id: `${current.id}/${segment}`,
          folders: [],
          files: [],
        };
        current.folders.push(child);
      }
      current = child;
    }
    current.files.push(file);
  }

  const sortRec = (n: FolderTreeNode) => {
    n.folders.sort((a, b) => a.name.localeCompare(b.name));
    n.files.sort((a, b) => a.fileName.localeCompare(b.fileName));
    n.folders.forEach(sortRec);
  };
  sortRec(root);

  return root;
}

function collectFilePaths(node: FolderTreeNode): string[] {
  const out: string[] = [];
  const walk = (n: FolderTreeNode) => {
    n.files.forEach((f) => out.push(f.absolutePath));
    n.folders.forEach(walk);
  };
  walk(node);
  return out;
}

export default function FileTree({ workspace }: Props) {
  const selectedFiles    = useWorkspaceStore((s) => s.selectedFiles);
  const previewedFile    = useWorkspaceStore((s) => s.previewedFile);
  const toggleFile       = useWorkspaceStore((s) => s.toggleFile);
  const selectFiles      = useWorkspaceStore((s) => s.selectFiles);
  const setPreviewedFile = useWorkspaceStore((s) => s.setPreviewedFile);

  const projectTrees = useMemo(
    () => workspace.projects.map((p) => ({ project: p, tree: buildFolderTree(p) })),
    [workspace],
  );

  const defaultExpanded = projectTrees.map(({ tree }) => tree.id);

  const renderFolderNode = (node: FolderTreeNode, isProjectRoot: boolean) => {
    const descendantFiles = collectFilePaths(node);
    const allSelected  = descendantFiles.length > 0 && descendantFiles.every((p) => selectedFiles.has(p));
    const someSelected = descendantFiles.some((p) => selectedFiles.has(p));

    return (
      <TreeItem
        key={node.id}
        itemId={node.id}
        label={
          <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', py: 0.25 }}>
            <Checkbox
              checked={allSelected}
              indeterminate={!allSelected && someSelected}
              onChange={(e) => { e.stopPropagation(); selectFiles(descendantFiles, !allSelected); }}
              onClick={(e) => e.stopPropagation()}
              sx={{ p: 0.25 }}
              disabled={descendantFiles.length === 0}
            />
            <FolderIcon sx={{ fontSize: 16, color: isProjectRoot ? 'primary.main' : 'text.secondary' }} />
            <Typography variant="body2" sx={{ fontWeight: isProjectRoot ? 600 : 500 }}>
              {node.name}
            </Typography>
            {isProjectRoot && (
              <Typography variant="caption" color="text.secondary" sx={{ ml: 0.5 }}>
                ({descendantFiles.length})
              </Typography>
            )}
          </Stack>
        }
      >
        {node.folders.map((child) => renderFolderNode(child, false))}
        {node.files.map((file) => renderFileNode(file))}
      </TreeItem>
    );
  };

  const renderFileNode = (file: FileNode) => {
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
  };

  return (
    <SimpleTreeView
      defaultExpandedItems={defaultExpanded}
      sx={{
        '& .MuiTreeItem-content': { py: 0.25, px: 0.5, borderRadius: 0.5 },
        '& .MuiTreeItem-label':   { fontSize: '0.8125rem' },
      }}
    >
      {projectTrees.map(({ tree }) => renderFolderNode(tree, true))}
    </SimpleTreeView>
  );
}
