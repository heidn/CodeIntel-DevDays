import { apiClient } from './client';
import type { TraceEntryPoint, TraceRequest, TraceResult, EntryPointCandidate } from '../types';

export async function startTrace(req: TraceRequest): Promise<{ traceId: string }> {
  const { data } = await apiClient.post<{ traceId: string }>('/trace/run', req);
  return data;
}

export async function resolveTraceCandidates(
  workspaceId: string,
  entryPoint: TraceEntryPoint,
): Promise<EntryPointCandidate[]> {
  const { data } = await apiClient.post<EntryPointCandidate[]>('/trace/candidates', {
    workspaceId, entryPoint,
  });
  return data;
}

export async function getTrace(traceId: string): Promise<TraceResult> {
  const { data } = await apiClient.get<TraceResult>(`/trace/${traceId}`);
  return data;
}

export interface SaveTraceResponse {
  traceId: string;
  absolutePath: string;
  relativePath: string;
  copilotReference: string;
}

export async function saveTraceReport(traceId: string, outputPath?: string): Promise<SaveTraceResponse> {
  const { data } = await apiClient.post<SaveTraceResponse>(
    `/trace/${traceId}/save`,
    { outputPath: outputPath ?? null }
  );
  return data;
}

// Cancel reuses the existing analysis cancel endpoint — same Guid, same registry.
