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

export async function cancelAnalysis(analysisId: string): Promise<void> {
  await apiClient.post(`/analysis/${analysisId}/cancel`);
}

export function downloadReportUrl(analysisId: string): string {
  return `/api/reports/${analysisId}/download`;
}

export interface SaveReportResponse {
  analysisId: string;
  absolutePath: string;
  relativePath: string;
  copilotReference: string;
}

export async function saveReport(analysisId: string, outputPath?: string): Promise<SaveReportResponse> {
  const { data } = await apiClient.post<SaveReportResponse>(
    `/reports/${analysisId}/save`,
    { outputPath: outputPath ?? null }
  );
  return data;
}
