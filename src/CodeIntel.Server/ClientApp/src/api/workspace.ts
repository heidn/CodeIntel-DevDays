import { apiClient } from './client';
import type { Workspace } from '../types';

export async function loadSolution(path: string): Promise<Workspace> {
  const { data } = await apiClient.post<Workspace>('/workspace/load', { path });
  return data;
}

export async function getWorkspace(id: string): Promise<Workspace> {
  const { data } = await apiClient.get<Workspace>(`/workspace/${id}`);
  return data;
}

export async function getFile(workspaceId: string, path: string): Promise<{ path: string; content: string }> {
  const { data } = await apiClient.get(`/workspace/${workspaceId}/file`, { params: { path } });
  return data;
}

export interface BrowseEntry { name: string; path: string; }
export interface BrowseProjectFile { name: string; path: string; type: string; }
export interface BrowseResult {
  currentPath: string;
  parentPath: string | null;
  directories: BrowseEntry[];
  projectFiles: BrowseProjectFile[];
  drives: string[];
}

export async function browseFolder(path?: string): Promise<BrowseResult> {
  const { data } = await apiClient.get<BrowseResult>('/workspace/browse', { params: path ? { path } : {} });
  return data;
}
