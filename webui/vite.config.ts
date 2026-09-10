import { fileURLToPath, URL } from 'node:url';
import vue from '@vitejs/plugin-vue';
import { defineConfig } from 'vitest/config';
import { viteSingleFile } from 'vite-plugin-singlefile';

const controllerBase = process.env.VITE_DEV_CONTROLLER_BASE || 'http://127.0.0.1:48123';

export default defineConfig(({ mode }) => {
  const embedded = mode === 'embedded';
  return {
    base: './',
    plugins: [vue(), ...(embedded ? [viteSingleFile()] : [])],
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
      outDir: embedded ? 'dist-embedded' : 'dist',
    },
    test: {
      environment: 'happy-dom',
    },
  };
});
