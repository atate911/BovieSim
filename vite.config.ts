import { defineConfig } from 'vite';
import { fileURLToPath, URL } from 'node:url';

export default defineConfig({
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  assetsInclude: ['**/*.wgsl'],
  server: { port: 5173 },
  test: { include: ['tests/**/*.test.ts'], environment: 'node', testTimeout: 120000 },
});
