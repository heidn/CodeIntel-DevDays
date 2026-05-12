import { useEffect, useRef, useState } from 'react';
import mermaid from 'mermaid';
import {
  Box,
  Alert,
  Stack,
  IconButton,
  Tooltip,
  Dialog,
  DialogContent,
  DialogActions,
  Button,
  Menu,
  MenuItem,
} from '@mui/material';
import FullscreenIcon from '@mui/icons-material/Fullscreen';
import DownloadIcon from '@mui/icons-material/Download';
import CloseIcon from '@mui/icons-material/Close';

mermaid.initialize({
  startOnLoad: false,
  theme: 'dark',
  themeVariables: {
    fontFamily: '"JetBrains Mono", monospace',
    primaryColor: '#4f46e5',
    primaryBorderColor: '#7c3aed',
    primaryTextColor: '#ffffff',
    lineColor: '#94a3b8',
  },
  flowchart: { useMaxWidth: true, htmlLabels: true },
});

interface MermaidDiagramProps {
  source: string;
  /** When true (default), shows the fullscreen + download toolbar overlay. */
  showActions?: boolean;
  /** Base filename for downloaded diagrams (no extension). */
  filenameStem?: string;
}

export default function MermaidDiagram({
  source,
  showActions = true,
  filenameStem = 'trace-diagram',
}: MermaidDiagramProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const [error, setError] = useState<string | null>(null);
  const [fullscreen, setFullscreen] = useState(false);
  const [menuAnchor, setMenuAnchor] = useState<HTMLElement | null>(null);

  useEffect(() => {
    if (!containerRef.current || !source) return;
    let cancelled = false;
    (async () => {
      try {
        const id = `mermaid-${Math.random().toString(36).slice(2)}`;
        const { svg } = await mermaid.render(id, source);
        if (!cancelled && containerRef.current) {
          containerRef.current.innerHTML = svg;
          setError(null);
        }
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to render diagram');
      }
    })();
    return () => { cancelled = true; };
  }, [source]);

  function getSvgElement(): SVGSVGElement | null {
    return (containerRef.current?.querySelector('svg') as SVGSVGElement) ?? null;
  }

  function triggerDownload(blob: Blob, filename: string) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  }

  function serializeSvg(): { xml: string; width: number; height: number } | null {
    const svg = getSvgElement();
    if (!svg) return null;
    // Clone so we can stamp explicit width/height attributes (some renderers need them
    // even though the original viewport is responsive).
    const clone = svg.cloneNode(true) as SVGSVGElement;
    const vb = svg.viewBox.baseVal;
    const w = vb.width || svg.clientWidth || 800;
    const h = vb.height || svg.clientHeight || 600;
    clone.setAttribute('width', String(w));
    clone.setAttribute('height', String(h));
    clone.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
    const xml = new XMLSerializer().serializeToString(clone);
    return { xml, width: w, height: h };
  }

  function downloadSvg() {
    const s = serializeSvg();
    if (!s) return;
    triggerDownload(new Blob([s.xml], { type: 'image/svg+xml' }), `${filenameStem}.svg`);
    setMenuAnchor(null);
  }

  async function downloadPng() {
    const s = serializeSvg();
    if (!s) return;
    setMenuAnchor(null);

    // Encode as data URL so the <img> load is synchronous-relative (no CORS issues).
    const dataUrl = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(s.xml)}`;
    const img = new Image();
    await new Promise<void>((resolve, reject) => {
      img.onload = () => resolve();
      img.onerror = () => reject(new Error('Failed to load SVG for PNG conversion'));
      img.src = dataUrl;
    });

    const scale = 2; // crisp output
    const canvas = document.createElement('canvas');
    canvas.width = Math.round(s.width * scale);
    canvas.height = Math.round(s.height * scale);
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    ctx.fillStyle = '#1e1e2e';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.scale(scale, scale);
    ctx.drawImage(img, 0, 0, s.width, s.height);

    canvas.toBlob((blob) => {
      if (blob) triggerDownload(blob, `${filenameStem}.png`);
    }, 'image/png');
  }

  if (!source) return null;

  return (
    <Box sx={{ p: 2, position: 'relative' }}>
      {showActions && (
        <Stack
          direction="row"
          spacing={0.5}
          sx={{
            position: 'absolute',
            top: 6,
            right: 6,
            zIndex: 1,
            bgcolor: 'background.paper',
            border: '1px solid',
            borderColor: 'divider',
            borderRadius: 1,
            px: 0.5,
            py: 0.25,
          }}
        >
          <Tooltip title="View fullscreen">
            <IconButton size="small" onClick={() => setFullscreen(true)}>
              <FullscreenIcon sx={{ fontSize: 18 }} />
            </IconButton>
          </Tooltip>
          <Tooltip title="Download diagram">
            <IconButton size="small" onClick={(e) => setMenuAnchor(e.currentTarget)}>
              <DownloadIcon sx={{ fontSize: 18 }} />
            </IconButton>
          </Tooltip>
          <Menu anchorEl={menuAnchor} open={!!menuAnchor} onClose={() => setMenuAnchor(null)}>
            <MenuItem onClick={downloadSvg} sx={{ fontSize: '0.8125rem' }}>Download as SVG</MenuItem>
            <MenuItem onClick={downloadPng} sx={{ fontSize: '0.8125rem' }}>Download as PNG</MenuItem>
          </Menu>
        </Stack>
      )}

      {error && (
        <Alert severity="warning" sx={{ mb: 1, fontSize: '0.75rem' }}>
          Mermaid render error: {error}
        </Alert>
      )}

      <Box
        ref={containerRef}
        sx={{
          textAlign: 'center',
          '& svg': { maxWidth: '100%', height: 'auto' },
        }}
      />

      <Dialog
        open={fullscreen}
        onClose={() => setFullscreen(false)}
        maxWidth="xl"
        fullWidth
        slotProps={{ paper: { sx: { bgcolor: 'background.default' } } }}
      >
        <DialogContent sx={{ p: 0, overflow: 'auto', bgcolor: '#1e1e2e', maxHeight: '85vh' }}>
          {/* Re-render in the dialog with no nested toolbar. */}
          <MermaidDiagram source={source} showActions={false} filenameStem={filenameStem} />
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setFullscreen(false)} startIcon={<CloseIcon />} size="small">
            Close
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
