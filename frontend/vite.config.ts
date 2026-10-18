import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
const base = process.env.GITHUB_PAGES === 'true' ? '/PrintBridge/' : '/';

export default defineConfig({
  base,
  plugins: [react()],
  server: { proxy: { '/api': 'http://localhost:3001' } },
});
