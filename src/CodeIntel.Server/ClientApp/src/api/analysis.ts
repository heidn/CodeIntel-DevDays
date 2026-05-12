import { apiClient } from './client';
import type { AnalysisRequest, LlmStatus, PresetInfo } from '../types';

export async function getPresets(): Promise<PresetInfo[]> {
  const { data } = await apiClient.get<PresetInfo[]>('/analysis/presets');
  return data;
}

export async function getStatus(): Promise<LlmStatus> {
  const { data } = await apiClient.get<LlmStatus>('/analysis/status');
  return data;
}

export async function startAnalysis(req: AnalysisRequest): Promise<{ analysisId: string }> {
  const { data } = await apiClient.post<{ analysisId: string }>('/analysis/run', req);
  return data;
}

export function downloadReportUrl(analysisId: string): string {
  return `/api/reports/${analysisId}/download`;
}
