import { useCallback, useEffect, useRef, useState } from 'react';
import mermaid from 'mermaid';
import {
  Box,
  Alert,
  Stack,
  IconButton,
  Tooltip,
  Dialog,
  Menu,
  MenuItem,
  Typography,
} from '@mui/material';
import FullscreenIcon from '@mui/icons-material/Fullscreen';
import FullscreenExitIcon from '@mui/icons-material/FullscreenExit';
import DownloadIcon from '@mui/icons-material/Download';
import AddIcon from '@mui/icons-material/Add';
import RemoveIcon from '@mui/icons-material/Remove';
import CenterFocusStrongIcon from '@mui/icons-material/CenterFocusStrongOutlined';
import FitScreenIcon from '@mui/icons-material/FitScreenOutlined';

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
  flowchart: { useMaxWidth: false, htmlLabels: true },
});

interface MermaidDiagramProps {
  source: string;
  /** When true (default), shows the fullscreen + download + zoom toolbar overlay. */
  showActions?: boolean;
  /** Base filename for downloaded diagrams (no extension). */
  filenameStem?: string;
  /** When true, renders in full-viewport mode (used inside the fullscreen dialog). */
  fullscreenMode?: boolean;
  /** Called when the user clicks the exit-fullscreen button (only used in fullscreenMode). */
  onExitFullscreen?: () => void;
}

const MIN_SCALE = 0.25;
const MAX_SCALE = 6;
const ZOOM_STEP = 1.25;

export default function MermaidDiagram({
  source,
  showActions = true,
  filenameStem = 'trace-diagram',
  fullscreenMode = false,
  onExitFullscreen,
}: MermaidDiagramProps) {
  const containerRef = useRef<HTMLDivElement>(null); // viewport (clips + handles wheel/drag)
  const transformRef = useRef<HTMLDivElement>(null); // gets the CSS transform
  const svgHostRef   = useRef<HTMLDivElement>(null); // holds the rendered <svg>
  const [error, setError] = useState<string | null>(null);
  const [fullscreen, setFullscreen] = useState(false);
  const [menuAnchor, setMenuAnchor] = useState<HTMLElement | null>(null);

  const [scale, setScale]   = useState(1);
  const [offset, setOffset] = useState({ x: 0, y: 0 });
  const [isPanning, setIsPanning] = useState(false);
  const panStart = useRef({ x: 0, y: 0, ox: 0, oy: 0 });

  // Natural SVG dimensions (so "fit to view" can compute the right scale).
  const naturalSize = useRef<{ w: number; h: number } | null>(null);

  // Render mermaid source into svgHostRef.
  useEffect(() => {
    if (!svgHostRef.current || !source) return;
    let cancelled = false;
    (async () => {
      try {
        const id = `mermaid-${Math.random().toString(36).slice(2)}`;
        const { svg } = await mermaid.render(id, source);
        if (cancelled || !svgHostRef.current) return;
        svgHostRef.current.innerHTML = svg;
        setError(null);

        const el = svgHostRef.current.querySelector('svg') as SVGSVGElement | null;
        if (el) {
          const vb = el.viewBox.baseVal;
          const w = vb.width  || el.clientWidth  || 800;
          const h = vb.height || el.clientHeight || 600;
          naturalSize.current = { w, h };
          // Stamp explicit dimensions so the SVG renders at natural size (we drive
          // sizing via CSS transform, not the SVG's responsive defaults).
          el.setAttribute('width', String(w));
          el.setAttribute('height', String(h));
          el.style.maxWidth = 'none';
          el.style.height = 'auto';
          el.style.display = 'block';
        }
        // Auto-fit on first render in fullscreen mode.
        if (fullscreenMode) {
          requestAnimationFrame(() => fitToView());
        }
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to render diagram');
      }
    })();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [source, fullscreenMode]);

  // Ctrl+wheel zooms toward the cursor; plain wheel scrolls in fullscreen mode.
  useEffect(() => {
    const node = containerRef.current;
    if (!node) return;
    function onWheel(e: WheelEvent) {
      if (!e.ctrlKey && !e.metaKey) return;
      e.preventDefault();
      const rect = node!.getBoundingClientRect();
      const cx = e.clientX - rect.left;
      const cy = e.clientY - rect.top;
      setScale((s) => {
        const next = clamp(s * (e.deltaY < 0 ? ZOOM_STEP : 1 / ZOOM_STEP), MIN_SCALE, MAX_SCALE);
        // Zoom around the cursor: shift offset so the point under the cursor stays put.
        const factor = next / s;
        setOffset((o) => ({
          x: cx - (cx - o.x) * factor,
          y: cy - (cy - o.y) * factor,
        }));
        return next;
      });
    }
    node.addEventListener('wheel', onWheel, { passive: false });
    return () => node.removeEventListener('wheel', onWheel);
  }, []);

  // Drag-to-pan.
  useEffect(() => {
    if (!isPanning) return;
    function move(e: MouseEvent) {
      setOffset({
        x: panStart.current.ox + (e.clientX - panStart.current.x),
        y: panStart.current.oy + (e.clientY - panStart.current.y),
      });
    }
    function up() { setIsPanning(false); }
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', up);
    return () => {
      window.removeEventListener('mousemove', move);
      window.removeEventListener('mouseup', up);
    };
  }, [isPanning]);

  const onMouseDown = useCallback((e: React.MouseEvent) => {
    // Only start panning on primary button and not on toolbar/menu clicks.
    if (e.button !== 0) return;
    panStart.current = { x: e.clientX, y: e.clientY, ox: offset.x, oy: offset.y };
    setIsPanning(true);
  }, [offset.x, offset.y]);

  const zoomIn  = useCallback(() => setScale((s) => clamp(s * ZOOM_STEP,     MIN_SCALE, MAX_SCALE)), []);
  const zoomOut = useCallback(() => setScale((s) => clamp(s / ZOOM_STEP,     MIN_SCALE, MAX_SCALE)), []);
  const resetView = useCallback(() => { setScale(1); setOffset({ x: 0, y: 0 }); }, []);

  const fitToView = useCallback(() => {
    const vp = containerRef.current;
    const ns = naturalSize.current;
    if (!vp || !ns || ns.w === 0 || ns.h === 0) { resetView(); return; }
    const pad = 24;
    const sx = (vp.clientWidth  - pad * 2) / ns.w;
    const sy = (vp.clientHeight - pad * 2) / ns.h;
    const next = clamp(Math.min(sx, sy), MIN_SCALE, MAX_SCALE);
    setScale(next);
    // Center the diagram.
    setOffset({
      x: (vp.clientWidth  - ns.w * next) / 2,
      y: (vp.clientHeight - ns.h * next) / 2,
    });
  }, [resetView]);

  function getSvgElement(): SVGSVGElement | null {
    return (svgHostRef.current?.querySelector('svg') as SVGSVGElement) ?? null;
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

    const dataUrl = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(s.xml)}`;
    const img = new Image();
    await new Promise<void>((resolve, reject) => {
      img.onload = () => resolve();
      img.onerror = () => reject(new Error('Failed to load SVG for PNG conversion'));
      img.src = dataUrl;
    });

    const scaleFactor = 2;
    const canvas = document.createElement('canvas');
    canvas.width  = Math.round(s.width  * scaleFactor);
    canvas.height = Math.round(s.height * scaleFactor);
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    ctx.fillStyle = '#1e1e2e';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.scale(scaleFactor, scaleFactor);
    ctx.drawImage(img, 0, 0, s.width, s.height);

    canvas.toBlob((blob) => {
      if (blob) triggerDownload(blob, `${filenameStem}.png`);
    }, 'image/png');
  }

  if (!source) return null;

  const viewportHeight = fullscreenMode ? '100%' : 480;

  return (
    <Box sx={{ p: fullscreenMode ? 0 : 2, position: 'relative', height: fullscreenMode ? '100%' : 'auto' }}>
      {showActions && (
        <Stack
          direction="row"
          spacing={0.25}
          sx={{
            position: 'absolute',
            top: fullscreenMode ? 12 : 14,
            right: fullscreenMode ? 12 : 14,
            zIndex: 2,
            bgcolor: 'background.paper',
            border: '1px solid',
            borderColor: 'divider',
            borderRadius: 1,
            px: 0.5,
            py: 0.25,
            alignItems: 'center',
          }}
        >
          <Tooltip title="Zoom out (Ctrl+scroll)">
            <IconButton size="small" onClick={zoomOut} disabled={scale <= MIN_SCALE + 0.001}>
              <RemoveIcon sx={{ fontSize: 16 }} />
            </IconButton>
          </Tooltip>
          <Typography
            variant="caption"
            sx={{
              minWidth: 38,
              textAlign: 'center',
              fontFamily: '"JetBrains Mono", monospace',
              fontSize: '0.7rem',
              color: 'text.secondary',
              userSelect: 'none',
            }}
          >
            {Math.round(scale * 100)}%
          </Typography>
          <Tooltip title="Zoom in (Ctrl+scroll)">
            <IconButton size="small" onClick={zoomIn} disabled={scale >= MAX_SCALE - 0.001}>
              <AddIcon sx={{ fontSize: 16 }} />
            </IconButton>
          </Tooltip>
          <Tooltip title="Fit to view">
            <IconButton size="small" onClick={fitToView}>
              <FitScreenIcon sx={{ fontSize: 16 }} />
            </IconButton>
          </Tooltip>
          <Tooltip title="Reset (100%)">
            <IconButton size="small" onClick={resetView}>
              <CenterFocusStrongIcon sx={{ fontSize: 16 }} />
            </IconButton>
          </Tooltip>
          <Box sx={{ width: 1, height: 18, bgcolor: 'divider', mx: 0.25 }} />
          {fullscreenMode ? (
            <Tooltip title="Exit fullscreen (Esc)">
              <IconButton size="small" onClick={onExitFullscreen}>
                <FullscreenExitIcon sx={{ fontSize: 18 }} />
              </IconButton>
            </Tooltip>
          ) : (
            <Tooltip title="View fullscreen">
              <IconButton size="small" onClick={() => setFullscreen(true)}>
                <FullscreenIcon sx={{ fontSize: 18 }} />
              </IconButton>
            </Tooltip>
          )}
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
        onMouseDown={onMouseDown}
        sx={{
          position: 'relative',
          width: '100%',
          height: viewportHeight,
          overflow: 'hidden',
          bgcolor: fullscreenMode ? '#15151f' : 'rgba(0,0,0,0.12)',
          border: fullscreenMode ? 'none' : '1px solid',
          borderColor: 'divider',
          borderRadius: fullscreenMode ? 0 : 1,
          cursor: isPanning ? 'grabbing' : (scale > 1 ? 'grab' : 'default'),
          userSelect: 'none',
        }}
      >
        <Box
          ref={transformRef}
          sx={{
            position: 'absolute',
            top: 0,
            left: 0,
            transformOrigin: '0 0',
            transform: `translate(${offset.x}px, ${offset.y}px) scale(${scale})`,
            transition: isPanning ? 'none' : 'transform 0.08s ease-out',
            willChange: 'transform',
          }}
        >
          <Box ref={svgHostRef} />
        </Box>

        {!fullscreenMode && (
          <Typography
            variant="caption"
            sx={{
              position: 'absolute',
              bottom: 6,
              left: 8,
              color: 'text.disabled',
              fontFamily: '"JetBrains Mono", monospace',
              fontSize: '0.65rem',
              pointerEvents: 'none',
              userSelect: 'none',
            }}
          >
            drag to pan · ctrl+scroll to zoom
          </Typography>
        )}
      </Box>

      <Dialog
        open={fullscreen}
        onClose={() => setFullscreen(false)}
        fullScreen
        slotProps={{ paper: { sx: { bgcolor: '#15151f' } } }}
      >
        <Box sx={{ height: '100vh', width: '100vw', display: 'flex', flexDirection: 'column' }}>
          <Box sx={{ flex: 1, minHeight: 0 }}>
            <MermaidDiagram
              source={source}
              filenameStem={filenameStem}
              fullscreenMode
              onExitFullscreen={() => setFullscreen(false)}
            />
          </Box>
        </Box>
      </Dialog>
    </Box>
  );
}

function clamp(n: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, n));
}
