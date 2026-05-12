import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Vite dev server proxies API + SignalR to the .NET backend.
// In production, the .NET app serves the built React bundle from wwwroot.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': {
        target: 'http://localhost:5000',
        changeOrigin: true,
      },
      '/hubs': {
        target: 'http://localhost:5000',
        changeOrigin: true,
        ws: true, // critical for SignalR WebSocket upgrade
      },
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
  },
});
