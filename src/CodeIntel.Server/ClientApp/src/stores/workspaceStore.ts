import { create } from 'zustand';
import type { Workspace } from '../types';

interface WorkspaceState {
  workspace: Workspace | null;
  selectedFiles: Set<string>; // absolutePaths
  previewedFile: string | null;

  setWorkspace: (ws: Workspace | null) => void;
  toggleFile: (absolutePath: string) => void;
  selectFiles: (absolutePaths: string[], selected: boolean) => void;
  clearSelection: () => void;
  setPreviewedFile: (absolutePath: string | null) => void;
}

export const useWorkspaceStore = create<WorkspaceState>((set) => ({
  workspace: null,
  selectedFiles: new Set(),
  previewedFile: null,

  setWorkspace: (ws) => set({ workspace: ws, selectedFiles: new Set(), previewedFile: null }),

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
}));
