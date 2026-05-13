import { create } from 'zustand';
import type { Workspace, PinnedSnippet } from '../types';

interface WorkspaceState {
  workspace: Workspace | null;
  selectedFiles: Set<string>;
  previewedFile: string | null;
  pinnedSnippet: PinnedSnippet | null;
  paneMode: 'analysis' | 'trace' | 'metrics';
  solutionPanelCollapsed: boolean;
  wordWrap: boolean;

  setWorkspace: (ws: Workspace | null) => void;
  toggleFile: (absolutePath: string) => void;
  selectFiles: (absolutePaths: string[], selected: boolean) => void;
  clearSelection: () => void;
  setPreviewedFile: (absolutePath: string | null) => void;
  pinSnippet: (snippet: PinnedSnippet | null) => void;
  setPaneMode: (mode: 'analysis' | 'trace' | 'metrics') => void;
  setSolutionPanelCollapsed: (collapsed: boolean) => void;
  setWordWrap: (wrap: boolean) => void;
}

export const useWorkspaceStore = create<WorkspaceState>((set) => ({
  workspace: null,
  selectedFiles: new Set(),
  previewedFile: null,
  pinnedSnippet: null,
  paneMode: 'analysis',
  solutionPanelCollapsed: false,
  wordWrap: false,

  setWorkspace: (ws) => set({ workspace: ws, selectedFiles: new Set(), previewedFile: null, pinnedSnippet: null }),

  toggleFile: (absolutePath) =>
    set((state) => {
      const next = new Set(state.selectedFiles);
      if (next.has(absolutePath)) next.delete(absolutePath);
      else next.add(absolutePath);
      return { selectedFiles: next };
    }),

  selectFiles: (absolutePaths, selected) =>
    set((state) => {
      const next = new Set(state.selectedFiles);
      if (selected) absolutePaths.forEach((p) => next.add(p));
      else absolutePaths.forEach((p) => next.delete(p));
      return { selectedFiles: next };
    }),

  clearSelection: () => set({ selectedFiles: new Set() }),
  setPreviewedFile: (absolutePath) => set({ previewedFile: absolutePath }),
  pinSnippet: (snippet) => set({ pinnedSnippet: snippet }),
  setPaneMode: (mode) => set({ paneMode: mode }),
  setSolutionPanelCollapsed: (collapsed) => set({ solutionPanelCollapsed: collapsed }),
  setWordWrap: (wrap) => set({ wordWrap: wrap }),
}));
