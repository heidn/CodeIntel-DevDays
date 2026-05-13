/**
 * Launches VS Code at the given file + line via the `vscode://` URL scheme.
 * The browser's permission dialog will appear on first use per origin.
 *
 * URL format: vscode://file/{absolute_path}[:line[:column]]
 *   - Windows paths use forward slashes and a leading slash.
 *   - Spaces and other reserved chars are percent-encoded by encodeURI.
 */
export function openInVsCode(absolutePath: string, line?: number, column?: number): void {
  if (!absolutePath) return;
  const normalized = absolutePath.replace(/\\/g, '/');
  const withLeadingSlash = normalized.startsWith('/') ? normalized : '/' + normalized;
  let url = `vscode://file${encodeURI(withLeadingSlash)}`;
  if (line && line > 0) {
    url += `:${line}`;
    if (column && column > 0) url += `:${column}`;
  }
  window.location.href = url;
}

export function isVsCodeOpenable(filePath: string | null | undefined): boolean {
  return !!filePath && filePath.length > 0;
}
