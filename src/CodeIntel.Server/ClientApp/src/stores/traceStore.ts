import { create } from 'zustand';
import type { TraceDirection, TraceNode, TraceEdge, CancelReason } from '../types';
import { cancelAnalysis } from '../api/analysis';

export type TraceRunState =
  | 'idle'
  | 'starting'
  | 'building'    // walking the call graph
  | 'streaming'   // synopsizing nodes
  | 'completed'
  | 'error'
  | 'cancelling'
  | 'cancelled';

interface TraceState {
  // inputs (persist between runs)
  entryPointName: string;
  direction:      TraceDirection;
  depth:          number;
  setEntryPointName: (s: string) => void;
  setDirection:      (d: TraceDirection) => void;
  setDepth:          (n: number) => void;

  // run state
  currentTraceId:    string | null;
  runState:          TraceRunState;
  statusMessage:     string;
  entryPointFqn:     string | null;
  nodes:             TraceNode[];
  edges:             TraceEdge[];
  mermaid:           string;
  truncated:         boolean;
  durationSeconds:   number;
  errorMessage:      string | null;
  cancelReason:      CancelReason | null;
  runStartedAt:      number | null;
  lastEventAt:       number | null;

  // run control
  startRun:        (traceId: string) => void;
  setStatus:       (m: string) => void;
  setGraph:        (entryPointFqn: string, nodes: TraceNode[], edges: TraceEdge[], mermaid: string, truncated: boolean) => void;
  applySynopsis:   (nodeId: string, synopsis: string) => void;
  complete:        (durationSeconds: number) => void;
  error:           (message: string) => void;
  cancelled:       (reason: CancelReason, message: string) => void;
  requestCancel:   () => Promise<void>;
  reset:           () => void;
}

export const useTraceStore = create<TraceState>((set, get) => ({
  entryPointName: '',
  direction:      'callers',
  depth:          2,
  setEntryPointName: (s) => set({ entryPointName: s }),
  setDirection:      (d) => set({ direction: d }),
  setDepth:          (n) => set({ depth: Math.max(1, Math.min(5, Math.floor(n))) }),

  currentTraceId:  null,
  runState:        'idle',
  statusMessage:   '',
  entryPointFqn:   null,
  nodes:           [],
  edges:           [],
  mermaid:         '',
  truncated:       false,
  durationSeconds: 0,
  errorMessage:    null,
  cancelReason:    null,
  runStartedAt:    null,
  lastEventAt:     null,

  startRun: (traceId) =>
    set({
      currentTraceId:  traceId,
      runState:        'starting',
      statusMessage:   'Starting...',
      entryPointFqn:   null,
      nodes:           [],
      edges:           [],
      mermaid:         '',
      truncated:       false,
      durationSeconds: 0,
      errorMessage:    null,
      cancelReason:    null,
      runStartedAt:    Date.now(),
      lastEventAt:     Date.now(),
    }),

  setStatus: (m) =>
    set((s) => ({
      statusMessage: m,
      runState: s.runState === 'idle' ? 'starting' : s.runState,
      lastEventAt: Date.now(),
    })),

  setGraph: (entryPointFqn, nodes, edges, mermaid, truncated) =>
    // Note: does NOT touch runState. State transitions are owned by startRun /
    // complete / error / cancelled / requestCancel. setGraph is data-only —
    // otherwise it overwrites a freshly-completed state back to streaming when
    // TraceResultsView's post-completion fetch lands.
    set({ entryPointFqn, nodes, edges, mermaid, truncated, lastEventAt: Date.now() }),

  applySynopsis: (nodeId, synopsis) =>
    set((s) => ({
      nodes: s.nodes.map((n) => (n.id === nodeId ? { ...n, synopsis } : n)),
      lastEventAt: Date.now(),
    })),

  complete: (durationSeconds) =>
    set({ runState: 'completed', durationSeconds, statusMessage: '' }),

  error: (message) =>
    set({ runState: 'error', errorMessage: message, statusMessage: '' }),

  cancelled: (reason, message) =>
    set({ runState: 'cancelled', cancelReason: reason, errorMessage: message, statusMessage: '' }),

  requestCancel: async () => {
    const id = get().currentTraceId;
    if (!id) return;
    set({ runState: 'cancelling', statusMessage: 'Cancelling...' });
    try { await cancelAnalysis(id); }
    catch { /* server will emit terminal event */ }
  },

  reset: () =>
    set({
      currentTraceId:  null,
      runState:        'idle',
      statusMessage:   '',
      entryPointFqn:   null,
      nodes:           [],
      edges:           [],
      mermaid:         '',
      truncated:       false,
      durationSeconds: 0,
      errorMessage:    null,
      cancelReason:    null,
      runStartedAt:    null,
      lastEventAt:     null,
    }),
}));
