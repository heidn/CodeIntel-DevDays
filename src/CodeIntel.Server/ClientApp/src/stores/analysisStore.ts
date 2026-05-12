import { create } from 'zustand';
import type { Finding } from '../types';

export type RunState = 'idle' | 'starting' | 'building' | 'streaming' | 'completed' | 'error';

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
  analyzedFilePaths: string[];

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
  complete: (durationSeconds: number) => void;
  error: (message: string) => void;
  reset: () => void;
}

export const useAnalysisStore = create<AnalysisState>((set) => ({
  currentAnalysisId: null,
  runState: 'idle',
  statusMessage: '',
  streamedText: '',
  findings: [],
  contextTokens: 0,
  fileCount: 0,
  durationSeconds: 0,
  errorMessage: null,
  analyzedFilePaths: [],
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
      analyzedFilePaths: filePaths,
    }),

  setStatus: (message) =>
    set((state) => ({
      statusMessage: message,
      runState: state.runState === 'idle' ? 'starting' : state.runState,
    })),

  setStarted: (contextTokens, fileCount) =>
    set({ contextTokens, fileCount, runState: 'streaming', statusMessage: '' }),

  appendToken: (text) =>
    set((state) => ({ streamedText: state.streamedText + text, runState: 'streaming' })),

  addFinding: (finding) =>
    set((state) => ({ findings: [...state.findings, finding] })),

  complete: (durationSeconds) =>
    set({ runState: 'completed', durationSeconds }),

  error: (message) =>
    set({ runState: 'error', errorMessage: message }),

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
      analyzedFilePaths: [],
    }),
}));
