import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const apiTarget = process.env.services__factory_api__https__0
  || process.env.services__factory_api__http__0
  || process.env.VITE_API_URL
  || 'http://localhost:5180';

export default defineConfig({
  plugins: [react()],
  server: {
    port: Number(process.env.PORT || 5173),
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true, secure: false },
      '/auth': { target: apiTarget, changeOrigin: true, secure: false },
      '/webhooks': { target: apiTarget, changeOrigin: true, secure: false },
      '/hubs': { target: apiTarget, ws: true, changeOrigin: true, secure: false },
      '/openapi': { target: apiTarget, changeOrigin: true, secure: false }
    }
  },
  test: {
    environment: 'jsdom',
    globals: true
  }
});
