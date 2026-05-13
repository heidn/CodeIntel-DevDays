export type Severity = 'info' | 'suggestion' | 'warning' | 'bug' | 'deadCode';

export interface PinnedSnippet {
  absolutePath: string;
  startLine: number;
  endLine: number;
  text: string;
}

export type AnalysisMode = 'preset' | 'freeText';

export interface Finding {
  severity: Severity;
  title: string;
  description: string;
  filePath: string | null;
  lineNumber: number | null;
  codeSnippet: string | null;
}

export interface PresetInfo {
  key: string;
  name: string;
  description: string;
  icon: string;
  applicableLanguages?: Language[];
}

export interface FileNode {
  absolutePath: string;
  relativePath: string;
  fileName: string;
  lineCount: number;
  sizeBytes: number;
}

export interface ProjectNode {
  name: string;
  path: string;
  files: FileNode[];
}

export type Language = 'cSharp' | 'typeScript' | 'java' | 'sql';

export interface Workspace {
  id: string;
  projectPath: string;
  projectName: string;
  projects: ProjectNode[];
  loadedAt: string;
  language: Language;
}

export interface AnalysisRequest {
  mode: AnalysisMode;
  presetKey: string | null;
  freeTextPrompt: string | null;
  selectedFilePaths: string[];
  workspaceId: string;
  analysisId: string;
  pinnedSnippet?: PinnedSnippet | null;
}

export type AnalysisEvent =
  | { type: 'started'; payload: { contextTokens: number; fileCount: number } }
  | { type: 'token'; payload: { text: string } }
  | { type: 'finding'; payload: Finding }
  | { type: 'status'; payload: { message: string } }
  | { type: 'completed'; payload: { analysisId: string; durationSeconds: number; findingCount: number } }
  | { type: 'error'; payload: { message: string } }
  | { type: 'cancelled'; payload: { reason: 'user' | 'timeout' | 'idle' | 'unknown'; message: string } }
  | { type: 'iterationStarted'; payload: { iteration: number; maxIterations: number } }
  | { type: 'contextRequested'; payload: { type: string; target: string } }
  | { type: 'contextFulfilled'; payload: { type: string; target: string; found: boolean } }
  | { type: 'traceGraphReady'; payload: { traceId: string; entryPointFqn: string; nodeCount: number; edgeCount: number; truncated: boolean } }
  | { type: 'traceNodeSynopsis'; payload: { nodeId: string; synopsis: string } };

export interface LlmStatus {
  llmReady: boolean;
  modelName: string;
  backendName: string;
}

export interface DefinitionLocation {
  filePath: string;
  line: number;
  character: number;
  symbolName: string;
}

// ── Trace ─────────────────────────────────────────────────────────────────────

export type TraceDirection = 'callers' | 'callees' | 'both';
export type EdgeKind       = 'calls'   | 'calledBy';
export type NodeKind       = 'normal'  | 'dbAccess' | 'httpCall';
export type CancelReason   = 'user' | 'timeout' | 'idle' | 'unknown';

export interface TraceEntryPoint {
  methodName: string | null;
  filePath:   string | null;
  line:       number | null;
  character:  number | null;
}

export interface TraceRequest {
  workspaceId: string;
  entryPoint:  TraceEntryPoint;
  direction:   TraceDirection;
  depth:       number;
  traceId?:    string | null;
}

export interface TraceNode {
  id:           string;
  symbolFqn:    string;
  displayName:  string;
  filePath:     string | null;
  line:         number | null;
  bodySnippet:  string | null;
  synopsis:     string | null;
  kind:         NodeKind;
}

export interface TraceEdge {
  fromId: string;
  toId:   string;
  kind:   EdgeKind;
}

export interface TraceResult {
  id:                   string;
  startedAt:            string;
  completedAt:          string;
  workspaceId:          string;
  entryPoint:           TraceEntryPoint;
  entryPointSymbolFqn:  string;
  direction:            TraceDirection;
  depth:                number;
  nodes:                TraceNode[];
  edges:                TraceEdge[];
  mermaid:              string;
  truncated:            boolean;
  duration:             string;
  reportPath:           string | null;
}
