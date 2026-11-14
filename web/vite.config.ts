import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const apiTarget = process.env['services__factory-api__http__0']
  || process.env['services__factory-api__https__0']
  || process.env.services__factory_api__http__0
  || process.env.services__factory_api__https__0
  || process.env.VITE_API_URL
  || 'http://localhost:5180';

export default defineConfig({
  plugins: [react()],
  server: {
    host: true,
    port: Number(process.env.PORT || 5173),
    strictPort: Boolean(process.env.PORT),
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true, secure: false, xfwd: true },
      '/auth': { target: apiTarget, changeOrigin: true, secure: false, xfwd: true },
      '/webhooks': { target: apiTarget, changeOrigin: true, secure: false, xfwd: true },
      '/hubs': { target: apiTarget, ws: true, changeOrigin: true, secure: false, xfwd: true },
      '/openapi': { target: apiTarget, changeOrigin: true, secure: false, xfwd: true }
    }
  },
  test: {
    environment: 'jsdom',
    globals: true
  }
});
