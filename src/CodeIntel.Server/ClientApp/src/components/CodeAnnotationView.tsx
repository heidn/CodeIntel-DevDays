import { useState, useCallback } from 'react';
import {
  Box,
  Stack,
  Typography,
  Chip,
  Paper,
  CircularProgress,
  Alert,
  Tooltip,
  Link,
} from '@mui/material';
import { useWorkspaceStore } from '../stores/workspaceStore';
import BugReportIcon from '@mui/icons-material/BugReportOutlined';
import WarningAmberIcon from '@mui/icons-material/WarningAmberOutlined';
import LightbulbIcon from '@mui/icons-material/LightbulbOutlined';
import InfoIcon from '@mui/icons-material/InfoOutlined';
import DeleteSweepIcon from '@mui/icons-material/DeleteSweepOutlined';
import { useQuery } from '@tanstack/react-query';
import { getFile } from '../api/workspace';
import type { Finding, Severity } from '../types';

interface Props {
  workspaceId: string;
  filePaths: string[];
  findings: Finding[];
}

const severityMeta: Record<Severity, { color: string; bg: string; border: string; icon: React.ReactNode; label: string }> = {
  bug:        { color: '#dc2626', bg: 'rgba(220,38,38,0.07)',  border: '#dc2626', icon: <BugReportIcon sx={{ fontSize: 13 }} />,    label: 'Bug' },
  warning:    { color: '#ca8a04', bg: 'rgba(202,138,4,0.07)',  border: '#ca8a04', icon: <WarningAmberIcon sx={{ fontSize: 13 }} />, label: 'Warning' },
  suggestion: { color: '#16a34a', bg: 'rgba(22,163,74,0.07)', border: '#16a34a', icon: <LightbulbIcon sx={{ fontSize: 13 }} />,    label: 'Suggestion' },
  info:       { color: '#0284c7', bg: 'rgba(2,132,199,0.07)', border: '#0284c7', icon: <InfoIcon sx={{ fontSize: 13 }} />,         label: 'Info' },
  deadCode:   { color: '#64748b', bg: 'rgba(100,116,139,0.07)', border: '#64748b', icon: <DeleteSweepIcon sx={{ fontSize: 13 }} />, label: 'Dead Code' },
};

function fileName(path: string): string {
  return path.replace(/\\/g, '/').split('/').pop() ?? path;
}

function normPath(p: string): string {
  return p.replace(/\\/g, '/').toLowerCase();
}

function findingsForFile(findings: Finding[], absolutePath: string): Finding[] {
  const norm = normPath(absolutePath);
  return findings.filter((f) => {
    if (!f.filePath) return false;
    const fp = normPath(f.filePath);
    return norm.endsWith(fp) || fp.endsWith(norm) || fileName(norm) === fileName(fp);
  });
}

function buildLineMap(findings: Finding[]): Map<number, Finding[]> {
  const map = new Map<number, Finding[]>();
  for (const f of findings) {
    const line = f.lineNumber;
    if (!line) continue;
    if (!map.has(line)) map.set(line, []);
    map.get(line)!.push(f);
  }
  return map;
}

interface FileViewProps {
  workspaceId: string;
  absolutePath: string;
  findings: Finding[];
}

function FileView({ workspaceId, absolutePath, findings }: FileViewProps) {
  const [expandedLines, setExpandedLines] = useState<Set<number>>(new Set());
  const wordWrap = useWorkspaceStore((s) => s.wordWrap);

  const { data, isLoading, error } = useQuery({
    queryKey: ['file', workspaceId, absolutePath],
    queryFn: () => getFile(workspaceId, absolutePath),
    staleTime: Infinity,
  });

  const toggle = useCallback((line: number) => {
    setExpandedLines((prev) => {
      const next = new Set(prev);
      if (next.has(line)) next.delete(line);
      else next.add(line);
      return next;
    });
  }, []);

  if (isLoading) {
    return (
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, p: 3 }}>
        <CircularProgress size={16} />
        <Typography variant="caption" color="text.secondary">Loading…</Typography>
      </Box>
    );
  }

  if (error || !data) {
    return <Alert severity="error" sx={{ m: 2 }}>Could not load file.</Alert>;
  }

  const lines = data.content.split('\n');
  const lineMap = buildLineMap(findings);
  const gutterWidth = String(lines.length).length;

  return (
    <Box
      sx={{
        fontFamily: '"JetBrains Mono", monospace',
        fontSize: '0.8rem',
        lineHeight: 1.6,
        width: wordWrap ? '100%' : 'max-content',
        minWidth: '100%',
      }}
    >
      {lines.map((lineText, idx) => {
        const lineNo = idx + 1;
        const lineFindings = lineMap.get(lineNo) ?? [];
        const hasFindings = lineFindings.length > 0;
        const isExpanded = expandedLines.has(lineNo);
        const primarySeverity = lineFindings[0]?.severity;
        const meta = primarySeverity ? severityMeta[primarySeverity] : null;

        return (
          <Box key={lineNo}>
            {/* Code line */}
            <Box
              sx={{
                display: 'flex',
                alignItems: 'stretch',
                bgcolor: hasFindings ? meta?.bg : 'transparent',
                borderLeft: hasFindings ? `3px solid ${meta?.border}` : '3px solid transparent',
                width: wordWrap ? '100%' : 'max-content',
                minWidth: '100%',
                '&:hover': { bgcolor: hasFindings ? meta?.bg : 'rgba(255,255,255,0.02)' },
              }}
            >
              {/* Line number gutter */}
              <Box
                sx={{
                  minWidth: `${gutterWidth + 2}ch`,
                  px: 1,
                  py: 0,
                  color: 'rgba(255,255,255,0.25)',
                  textAlign: 'right',
                  userSelect: 'none',
                  flexShrink: 0,
                  borderRight: '1px solid rgba(255,255,255,0.06)',
                  mr: 0,
                  position: wordWrap ? 'static' : 'sticky',
                  left: 0,
                  bgcolor: '#1a1a2e',
                  zIndex: 1,
                }}
              >
                {lineNo}
              </Box>

              {/* Finding badge */}
              <Box
                sx={{
                  width: 22,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  flexShrink: 0,
                }}
              >
                {hasFindings && (
                  <Tooltip
                    title={`${lineFindings.length} finding${lineFindings.length > 1 ? 's' : ''} — click to ${isExpanded ? 'hide' : 'show'}`}
                    placement="right"
                  >
                    <Box
                      onClick={() => toggle(lineNo)}
                      sx={{
                        width: 14,
                        height: 14,
                        borderRadius: '50%',
                        bgcolor: meta?.border,
                        display: 'flex',
                        alignItems: 'center',
                        justifyContent: 'center',
                        cursor: 'pointer',
                        color: '#fff',
                        fontSize: '0.55rem',
                        fontWeight: 700,
                        flexShrink: 0,
                        transition: 'transform 0.1s',
                        '&:hover': { transform: 'scale(1.2)' },
                      }}
                    >
                      {lineFindings.length}
                    </Box>
                  </Tooltip>
                )}
              </Box>

              {/* Code text */}
              <Box
                component="span"
                sx={{
                  px: 1,
                  py: 0,
                  whiteSpace: wordWrap ? 'pre-wrap' : 'pre',
                  wordBreak: wordWrap ? 'break-word' : 'normal',
                  color: hasFindings ? '#f8f8f2' : 'rgba(255,255,255,0.78)',
                  flex: 1,
                  userSelect: 'text',
                }}
              >
                {lineText || ' '}
              </Box>
            </Box>

            {/* Inline comment bubbles */}
            {isExpanded && lineFindings.map((finding, fi) => (
              <BubbleFinding key={fi} finding={finding} />
            ))}
          </Box>
        );
      })}
    </Box>
  );
}

function BubbleFinding({ finding }: { finding: Finding }) {
  const fm = severityMeta[finding.severity] ?? severityMeta.info;
  const [expanded, setExpanded] = useState(false);
  return (
    <Paper
      elevation={0}
      sx={{
        mx: 2,
        my: 0.5,
        p: 1.5,
        bgcolor: 'rgba(30,30,46,0.95)',
        border: `1px solid ${fm.border}44`,
        borderLeft: `3px solid ${fm.border}`,
        borderRadius: 1,
        position: 'relative',
      }}
    >
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.5 }}>
        <Box sx={{ color: fm.color, display: 'flex' }}>{fm.icon}</Box>
        <Typography
          variant="caption"
          sx={{ color: fm.color, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.05em', fontSize: '0.65rem' }}
        >
          {fm.label}
        </Typography>
        <Typography variant="caption" sx={{ fontFamily: '"JetBrains Mono", monospace', color: 'rgba(255,255,255,0.4)', fontSize: '0.65rem' }}>
          line {finding.lineNumber}
        </Typography>
      </Stack>
      <Typography variant="body2" sx={{ fontWeight: 600, mb: 0.25, fontSize: '0.8rem', color: 'rgba(255,255,255,0.9)' }}>
        {finding.title}
      </Typography>
      <Typography variant="caption" sx={{ color: 'rgba(255,255,255,0.55)', display: 'block', lineHeight: 1.5 }}>
        {finding.description}
      </Typography>
      {finding.codeSnippet && (
        <>
          <Box
            component="pre"
            sx={{
              mt: 1,
              mb: 0,
              p: 1,
              bgcolor: '#11111b',
              borderRadius: 0.75,
              fontFamily: '"JetBrains Mono", monospace',
              fontSize: '0.7rem',
              color: '#cdd6f4',
              overflow: 'auto',
              maxHeight: expanded ? 'none' : 400,
              border: '1px solid rgba(255,255,255,0.08)',
              whiteSpace: 'pre',
              '&::-webkit-scrollbar':       { width: 8, height: 8 },
              '&::-webkit-scrollbar-thumb': { bgcolor: 'rgba(255,255,255,0.14)', borderRadius: 4 },
            }}
          >
            {finding.codeSnippet}
          </Box>
          <Link
            component="button"
            type="button"
            onClick={() => setExpanded((e) => !e)}
            underline="hover"
            sx={{ mt: 0.5, fontSize: '0.65rem', fontFamily: '"JetBrains Mono", monospace', color: 'text.secondary' }}
          >
            {expanded ? 'Collapse snippet' : 'Expand snippet'}
          </Link>
        </>
      )}
    </Paper>
  );
}

export default function CodeAnnotationView({ workspaceId, filePaths, findings }: Props) {
  const [selectedIndex, setSelectedIndex] = useState(0);

  if (filePaths.length === 0) {
    return (
      <Box sx={{ p: 3, color: 'text.secondary' }}>
        <Typography variant="caption">No files were analyzed.</Typography>
      </Box>
    );
  }

  const currentPath = filePaths[selectedIndex] ?? filePaths[0];
  const fileFindingCounts = filePaths.map((p) => findingsForFile(findings, p).length);
  const currentFindings = findingsForFile(findings, currentPath);

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      {/* File tabs */}
      <Box
        sx={{
          display: 'flex',
          flexShrink: 0,
          overflowX: 'auto',
          borderBottom: '1px solid',
          borderColor: 'divider',
          bgcolor: 'background.default',
          px: 1,
          py: 0.5,
          gap: 0.5,
          '&::-webkit-scrollbar': { height: 4 },
          '&::-webkit-scrollbar-thumb': { bgcolor: 'rgba(255,255,255,0.12)', borderRadius: 2 },
        }}
      >
        {filePaths.map((p, i) => {
          const active = i === selectedIndex;
          const count = fileFindingCounts[i];
          return (
            <Chip
              key={p}
              label={
                <Stack direction="row" spacing={0.75} sx={{ alignItems: 'center' }}>
                  <span>{fileName(p)}</span>
                  {count > 0 && (
                    <Box
                      sx={{
                        bgcolor: active ? 'primary.main' : 'rgba(255,255,255,0.12)',
                        color: active ? '#fff' : 'text.secondary',
                        borderRadius: '10px',
                        px: 0.75,
                        fontSize: '0.6rem',
                        fontWeight: 700,
                        lineHeight: 1.6,
                      }}
                    >
                      {count}
                    </Box>
                  )}
                </Stack>
              }
              onClick={() => setSelectedIndex(i)}
              size="small"
              sx={{
                fontFamily: '"JetBrains Mono", monospace',
                fontSize: '0.72rem',
                bgcolor: active ? 'rgba(79, 70, 229, 0.12)' : 'transparent',
                border: '1px solid',
                borderColor: active ? 'primary.main' : 'divider',
                color: active ? 'primary.main' : 'text.secondary',
                cursor: 'pointer',
                '&:hover': { borderColor: active ? 'primary.main' : 'rgba(255,255,255,0.2)' },
              }}
            />
          );
        })}
      </Box>

      {/* Code view */}
      <Box
        sx={{
          flex: 1,
          minHeight: 0,
          overflow: 'auto',
          bgcolor: '#1a1a2e',
          '&::-webkit-scrollbar':       { width: 10, height: 10 },
          '&::-webkit-scrollbar-thumb': { bgcolor: 'rgba(255,255,255,0.14)', borderRadius: 5 },
          '&::-webkit-scrollbar-thumb:hover': { bgcolor: 'rgba(255,255,255,0.24)' },
          '&::-webkit-scrollbar-corner': { bgcolor: 'transparent' },
        }}
      >
        <FileView
          key={currentPath}
          workspaceId={workspaceId}
          absolutePath={currentPath}
          findings={currentFindings}
        />
      </Box>
    </Box>
  );
}
