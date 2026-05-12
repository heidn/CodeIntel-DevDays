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
  | { type: 'contextFulfilled'; payload: { type: string; target: string; found: boolean } };

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
