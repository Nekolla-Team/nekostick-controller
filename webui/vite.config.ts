import { fileURLToPath, URL } from 'node:url';
import vue from '@vitejs/plugin-vue';
import { defineConfig } from 'vitest/config';

const controllerBase = process.env.VITE_DEV_CONTROLLER_BASE || 'http://127.0.0.1:48123';

// One build serves both deployments: the controller embeds the whole output directory as
// assembly resources and serves it from its Web UI root, while the same directory works as a
// standalone static site. Chunks stay separate files so the shell never carries Monaco and
// the hashed names stay cacheable.
export default defineConfig({
  base: './',
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    proxy: {
      '/v1': controllerBase,
    },
  },
  build: {
    outDir: 'dist',
    // Flat output: the shell reaches its chunks by relative path from either hosting root.
    assetsDir: '',
    chunkSizeWarningLimit: 100000000,
  },
  test: {
    environment: 'happy-dom',
  },
});
