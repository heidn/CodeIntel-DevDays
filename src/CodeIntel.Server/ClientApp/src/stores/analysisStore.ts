import { create } from 'zustand';
import type { Finding } from '../types';
import { cancelAnalysis } from '../api/analysis';

export type RunState =
  | 'idle'
  | 'starting'
  | 'building'
  | 'streaming'
  | 'completed'
  | 'error'
  | 'cancelling'
  | 'cancelled';

export type CancelReason = 'user' | 'timeout' | 'idle' | 'unknown';

interface AnalysisState {
  currentAnalysisId: string | null;
  runState: RunState;
  statusMessage: string;
  streamedText: string;
  findings: Finding[];
  contextTokens: number;
  fileCount: number;
  durationSeconds: number;
  errorMessage: string | null;
  cancelReason: CancelReason | null;
  analyzedFilePaths: string[];

  // run-health signals from the orchestrator's `completed` event — non-zero
  // counts (or reachedDone === false) mean the run produced degraded output:
  // a finding was truncated mid-stream by the response-token cap, a JSON parse
  // failed, or the model never wrote `<done/>`. ResultsView surfaces this so
  // the user knows "0 findings" might really mean "1 finding got cut off".
  incompleteFindings: number;
  malformedFindings: number;
  reachedDone: boolean;

  // timing
  runStartedAt: number | null;   // epoch ms — set when starting, cleared on terminal state
  lastTokenAt: number | null;    // epoch ms of most recent token, for idle warnings

  // mode/inputs
  selectedPresetKey: string | null;
  freeTextPrompt: string;
  setPreset: (key: string | null) => void;
  setFreeText: (text: string) => void;

  // run control
  startRun: (analysisId: string, filePaths: string[]) => void;
  setStatus: (message: string) => void;
  appendToken: (text: string) => void;
  setStarted: (contextTokens: number, fileCount: number) => void;
  addFinding: (finding: Finding) => void;
  complete: (durationSeconds: number, health?: { incompleteFindings: number; malformedFindings: number; reachedDone: boolean }) => void;
  error: (message: string) => void;
  cancelled: (reason: CancelReason, message: string) => void;
  requestCancel: () => Promise<void>;
  reset: () => void;
}

export const useAnalysisStore = create<AnalysisState>((set, get) => ({
  currentAnalysisId: null,
  runState: 'idle',
  statusMessage: '',
  streamedText: '',
  findings: [],
  contextTokens: 0,
  fileCount: 0,
  durationSeconds: 0,
  errorMessage: null,
  cancelReason: null,
  analyzedFilePaths: [],
  incompleteFindings: 0,
  malformedFindings: 0,
  reachedDone: true,
  runStartedAt: null,
  lastTokenAt: null,
  selectedPresetKey: null,
  freeTextPrompt: '',

  setPreset: (key) => set({ selectedPresetKey: key }),
  setFreeText: (text) => set({ freeTextPrompt: text }),

  startRun: (analysisId, filePaths) =>
    set({
      currentAnalysisId: analysisId,
      runState: 'starting',
      statusMessage: 'Starting...',
      streamedText: '',
      findings: [],
      contextTokens: 0,
      fileCount: 0,
      durationSeconds: 0,
      errorMessage: null,
      cancelReason: null,
      analyzedFilePaths: filePaths,
      incompleteFindings: 0,
      malformedFindings: 0,
      reachedDone: true,
      runStartedAt: Date.now(),
      lastTokenAt: null,
    }),

  setStatus: (message) =>
    set((state) => ({
      statusMessage: message,
      runState: state.runState === 'idle' ? 'starting' : state.runState,
    })),

  setStarted: (contextTokens, fileCount) =>
    set({ contextTokens, fileCount, runState: 'streaming', statusMessage: '' }),

  appendToken: (text) =>
    set((state) => ({
      streamedText: state.streamedText + text,
      runState: state.runState === 'cancelling' ? 'cancelling' : 'streaming',
      lastTokenAt: Date.now(),
    })),

  addFinding: (finding) =>
    set((state) => ({ findings: [...state.findings, finding] })),

  complete: (durationSeconds, health) =>
    set({
      runState: 'completed',
      durationSeconds,
      statusMessage: '',
      incompleteFindings: health?.incompleteFindings ?? 0,
      malformedFindings:  health?.malformedFindings  ?? 0,
      reachedDone:        health?.reachedDone        ?? true,
    }),

  error: (message) =>
    set({ runState: 'error', errorMessage: message, statusMessage: '' }),

  cancelled: (reason, message) =>
    set({
      runState: 'cancelled',
      cancelReason: reason,
      errorMessage: message,
      statusMessage: '',
    }),

  requestCancel: async () => {
    const id = get().currentAnalysisId;
    if (!id) return;
    set({ runState: 'cancelling', statusMessage: 'Cancelling...' });
    try {
      await cancelAnalysis(id);
    } catch {
      // Server may have already finished; ignore — the eventual server
      // event ('completed' / 'cancelled' / 'error') will set the final state.
    }
  },

  reset: () =>
    set({
      currentAnalysisId: null,
      runState: 'idle',
      statusMessage: '',
      streamedText: '',
      findings: [],
      contextTokens: 0,
      fileCount: 0,
      durationSeconds: 0,
      errorMessage: null,
      cancelReason: null,
      analyzedFilePaths: [],
      incompleteFindings: 0,
      malformedFindings: 0,
      reachedDone: true,
      runStartedAt: null,
      lastTokenAt: null,
    }),
}));
