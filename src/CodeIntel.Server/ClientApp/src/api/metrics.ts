import { apiClient } from './client';
import type { WorkspaceMetricsResult } from '../types';

export async function computeMetrics(
  workspaceId: string,
  filePaths: string[] | null = null,
): Promise<WorkspaceMetricsResult> {
  const res = await apiClient.post<WorkspaceMetricsResult>('/metrics/compute', {
    workspaceId,
    filePaths,
  });
  return res.data;
}
