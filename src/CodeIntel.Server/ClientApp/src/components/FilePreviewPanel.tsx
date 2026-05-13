import { useCallback, useEffect, useRef, useState } from 'react';
import { Box, Typography, CircularProgress, Alert, Button, IconButton, Tooltip } from '@mui/material';
import CloseIcon from '@mui/icons-material/Close';
import PushPinOutlinedIcon from '@mui/icons-material/PushPinOutlined';
import AccountTreeIcon from '@mui/icons-material/AccountTreeOutlined';
import { useQuery } from '@tanstack/react-query';
import { getFile, getDefinition } from '../api/workspace';
import { useWorkspaceStore } from '../stores/workspaceStore';
import { useTraceStore } from '../stores/traceStore';
import type { PinnedSnippet } from '../types';

interface Props {
  workspaceId: string;
  absolutePath: string;
  scrollToLine?: number;
  isActive?: boolean;
  onNavigate?: (filePath: string, line: number) => void;
}

const DEFINITION_EXTS = ['.cs', '.ts', '.tsx', '.js', '.jsx', '.java', '.sql', '.pkg', '.pkb'];
const supportsDefinition = (path: string) =>
  DEFINITION_EXTS.some((ext) => path.toLowerCase().endsWith(ext));

function wordAtOffset(text: string, offset: number): { word: string; charStart: number } | null {
  const isWordChar = (ch: string) => /\w/.test(ch);
  let pos = offset;
  if (pos >= text.length || !isWordChar(text[pos])) {
    if (pos > 0 && isWordChar(text[pos - 1])) pos -= 1;
    else return null;
  }
  let start = pos;
  let end = pos;
  while (start > 0 && isWordChar(text[start - 1])) start--;
  while (end < text.length - 1 && isWordChar(text[end + 1])) end++;
  const word = text.slice(start, end + 1);
  return word.length > 0 ? { word, charStart: start } : null;
}

function caretCharOffset(x: number, y: number): number | null {
  if ('caretRangeFromPoint' in document) {
    const range = (document as Document & { caretRangeFromPoint(x: number, y: number): Range | null })
      .caretRangeFromPoint(x, y);
    if (range?.startContainer.nodeType === Node.TEXT_NODE) return range.startOffset;
  } else if ('caretPositionFromPoint' in document) {
    const pos = (document as Document & { caretPositionFromPoint(x: number, y: number): { offset: number } | null })
      .caretPositionFromPoint(x, y);
    if (pos) return pos.offset;
  }
  return null;
}

export default function FilePreviewPanel({
  workspaceId,
  absolutePath,
  scrollToLine,
  isActive = true,
  onNavigate,
}: Props) {
  const { data, isLoading, error } = useQuery({
    queryKey: ['file', workspaceId, absolutePath],
    queryFn: () => getFile(workspaceId, absolutePath),
    staleTime: Infinity,
  });

  const [selStart, setSelStart] = useState<number | null>(null);
  const [selEnd,   setSelEnd]   = useState<number | null>(null);
  const [clickSymbol, setClickSymbol] = useState<{ line: number; character: number; word: string } | null>(null);
  const [tracing, setTracing] = useState(false);

  const pinSnippet       = useWorkspaceStore((s) => s.pinSnippet);
  const setPaneMode      = useWorkspaceStore((s) => s.setPaneMode);
  const setEntryPointLocation = useTraceStore((s) => s.setEntryPointLocation);

  const codeContainerRef = useRef<HTMLDivElement>(null);

  // Scroll to the target line once the tab is active and the line is known.
  useEffect(() => {
    if (!isActive || scrollToLine == null) return;
    const el = codeContainerRef.current?.querySelector<HTMLElement>(`[data-line="${scrollToLine}"]`);
    el?.scrollIntoView({ block: 'center', behavior: 'smooth' });
  }, [isActive, scrollToLine]);

  const handleGoToDefinition = useCallback(async (lineNo: number, lineText: string, e: React.MouseEvent) => {
    const charOffset = caretCharOffset(e.clientX, e.clientY) ?? 0;
    const found = wordAtOffset(lineText, charOffset);
    if (!found) return;
    const result = await getDefinition(workspaceId, absolutePath, lineNo, found.charStart);
    if (result) onNavigate?.(result.filePath, result.line);
  }, [workspaceId, absolutePath, onNavigate]);

  if (isLoading) {
    return (
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, p: 3, flex: 1 }}>
        <CircularProgress size={16} />
        <Typography variant="caption" color="text.secondary">Loading…</Typography>
      </Box>
    );
  }

  if (error || !data) {
    return <Alert severity="error" sx={{ m: 2 }}>Could not load file.</Alert>;
  }

  const lines      = data.content.split('\n');
  const gutterWidth = String(lines.length).length;

  const hasSelection = selStart !== null && selEnd !== null;
  const selLo = hasSelection ? Math.min(selStart!, selEnd!) : 0;
  const selHi = hasSelection ? Math.max(selStart!, selEnd!) : 0;

  const isSelected = (lineNo: number) => hasSelection && lineNo >= selLo && lineNo <= selHi;

  const handleLineClick = (lineNo: number, lineText: string, e: React.MouseEvent) => {
    if (e.ctrlKey || e.metaKey) {
      handleGoToDefinition(lineNo, lineText, e);
      return;
    }
    if (e.shiftKey && selStart !== null) {
      setSelEnd(lineNo);
      setClickSymbol(null); // range selection — clear single-symbol context
    } else {
      setSelStart(lineNo);
      setSelEnd(lineNo);
      // Capture which word was clicked so we can offer "Trace from here".
      const offset = caretCharOffset(e.clientX, e.clientY);
      if (offset !== null) {
        const w = wordAtOffset(lineText, offset);
        setClickSymbol(w ? { line: lineNo, character: w.charStart, word: w.word } : null);
      } else {
        setClickSymbol(null);
      }
    }
  };

  const clearSelection = () => { setSelStart(null); setSelEnd(null); setClickSymbol(null); };

  const baseName = (p: string) => p.replace(/\\/g, '/').split('/').pop() ?? p;

  const handleTraceFromHere = async () => {
    if (!clickSymbol || !workspaceId) return;
    setTracing(true);
    try {
      const def = await getDefinition(workspaceId, absolutePath, clickSymbol.line, clickSymbol.character);
      const loc = def
        ? { filePath: def.filePath, line: def.line, character: def.character, symbolLabel: def.symbolName, fileShortName: baseName(def.filePath) }
        : { filePath: absolutePath, line: clickSymbol.line, character: clickSymbol.character, symbolLabel: clickSymbol.word, fileShortName: baseName(absolutePath) };
      setEntryPointLocation(loc);
      setPaneMode('trace');
      clearSelection();
    } catch {
      // Fallback: use the clicked position even without Roslyn resolution.
      setEntryPointLocation({
        filePath: absolutePath,
        line: clickSymbol.line,
        character: clickSymbol.character,
        symbolLabel: clickSymbol.word,
        fileShortName: baseName(absolutePath),
      });
      setPaneMode('trace');
      clearSelection();
    } finally {
      setTracing(false);
    }
  };

  const handlePin = () => {
    if (!hasSelection) return;
    const snippet: PinnedSnippet = {
      absolutePath,
      startLine: selLo,
      endLine:   selHi,
      text: lines.slice(selLo - 1, selHi).join('\n'),
    };
    pinSnippet(snippet);
    clearSelection();
  };

  return (
    <Box sx={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>

      {/* Selection toolbar */}
      {hasSelection && (
        <Box
          sx={{
            display: 'flex',
            alignItems: 'center',
            gap: 1.5,
            px: 2,
            py: 0.75,
            bgcolor: 'rgba(79, 70, 229, 0.08)',
            borderBottom: '1px solid rgba(79, 70, 229, 0.3)',
            flexShrink: 0,
          }}
        >
          <Typography
            variant="caption"
            sx={{
              fontFamily: '"JetBrains Mono", monospace',
              color: 'primary.main',
              fontSize: '0.7rem',
              flex: 1,
            }}
          >
            Lines {selLo}–{selHi} selected ({selHi - selLo + 1} lines)
          </Typography>
          <Button
            size="small"
            startIcon={<PushPinOutlinedIcon sx={{ fontSize: 13 }} />}
            onClick={handlePin}
            sx={{
              py: 0.25,
              px: 1,
              fontSize: '0.7rem',
              textTransform: 'none',
              color: 'primary.main',
              border: '1px solid rgba(79, 70, 229, 0.5)',
              borderRadius: 0.5,
              '&:hover': { bgcolor: 'rgba(79, 70, 229, 0.15)' },
            }}
          >
            Pin to analysis
          </Button>
          {clickSymbol && selStart === selEnd && (
            <Button
              size="small"
              startIcon={tracing ? <CircularProgress size={12} color="inherit" /> : <AccountTreeIcon sx={{ fontSize: 13 }} />}
              onClick={handleTraceFromHere}
              disabled={tracing}
              sx={{
                py: 0.25,
                px: 1,
                fontSize: '0.7rem',
                textTransform: 'none',
                color: 'secondary.main',
                border: '1px solid rgba(139, 92, 246, 0.5)',
                borderRadius: 0.5,
                '&:hover': { bgcolor: 'rgba(139, 92, 246, 0.12)' },
              }}
            >
              Trace from `{clickSymbol.word}`
            </Button>
          )}
          <IconButton
            size="small"
            onClick={clearSelection}
            sx={{ p: 0.25, color: 'text.secondary', '&:hover': { color: 'text.primary' } }}
          >
            <CloseIcon sx={{ fontSize: 13 }} />
          </IconButton>
        </Box>
      )}

      {/* Hint bar */}
      {supportsDefinition(absolutePath) && onNavigate && (
        <Box
          sx={{
            px: 2,
            py: 0.4,
            bgcolor: 'rgba(255,255,255,0.03)',
            borderBottom: '1px solid rgba(255,255,255,0.05)',
            flexShrink: 0,
          }}
        >
          <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.65rem' }}>
            {absolutePath.toLowerCase().endsWith('.cs')
              ? 'Click a line to select · Ctrl+click to go to definition · click a word then "Trace from" to trace'
              : 'Click a line to select · Ctrl+click to find definition'}
          </Typography>
        </Box>
      )}

      {/* Code viewer */}
      <Box
        ref={codeContainerRef}
        sx={{
          flex: 1,
          minHeight: 0,
          overflow: 'auto',
          bgcolor: '#1a1a2e',
          fontFamily: '"JetBrains Mono", monospace',
          fontSize: '0.8rem',
          lineHeight: 1.6,
        }}
      >
        {lines.map((lineText, idx) => {
          const lineNo    = idx + 1;
          const selected  = isSelected(lineNo);
          const isTarget  = scrollToLine != null && lineNo === scrollToLine;

          const borderColor = selected ? 'primary.main' : isTarget ? 'warning.main' : 'transparent';
          const bgColor = selected
            ? 'rgba(79, 70, 229, 0.12)'
            : isTarget
            ? 'rgba(251, 191, 36, 0.08)'
            : 'transparent';

          return (
            <Tooltip
              key={lineNo}
              title={supportsDefinition(absolutePath) && onNavigate ? 'Ctrl+click to go to definition' : ''}
              placement="left"
              enterDelay={800}
              disableHoverListener={!supportsDefinition(absolutePath) || !onNavigate}
            >
              <Box
                data-line={lineNo}
                onClick={(e) => handleLineClick(lineNo, lineText, e)}
                sx={{
                  display: 'flex',
                  alignItems: 'stretch',
                  borderLeft: '3px solid',
                  borderLeftColor: borderColor,
                  bgcolor: bgColor,
                  cursor: 'pointer',
                  userSelect: 'none',
                  '&:hover': {
                    bgcolor: selected
                      ? 'rgba(79, 70, 229, 0.18)'
                      : isTarget
                      ? 'rgba(251, 191, 36, 0.14)'
                      : 'rgba(255,255,255,0.03)',
                  },
                }}
              >
                {/* Gutter */}
                <Box
                  sx={{
                    minWidth: `${gutterWidth + 2}ch`,
                    px: 1,
                    color: selected
                      ? 'rgba(122, 162, 255, 0.6)'
                      : isTarget
                      ? 'rgba(251, 191, 36, 0.5)'
                      : 'rgba(255,255,255,0.2)',
                    textAlign: 'right',
                    userSelect: 'none',
                    flexShrink: 0,
                    borderRight: '1px solid rgba(255,255,255,0.06)',
                  }}
                >
                  {lineNo}
                </Box>

                {/* Code */}
                <Box
                  component="span"
                  sx={{
                    px: 1.5,
                    whiteSpace: 'pre',
                    color: selected || isTarget ? 'rgba(255,255,255,0.95)' : 'rgba(255,255,255,0.78)',
                    flex: 1,
                    overflow: 'hidden',
                  }}
                >
                  {lineText || ' '}
                </Box>
              </Box>
            </Tooltip>
          );
        })}
      </Box>
    </Box>
  );
}
