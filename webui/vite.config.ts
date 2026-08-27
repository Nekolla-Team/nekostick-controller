import { fileURLToPath, URL } from 'node:url'
import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vitest/config'

const controllerBase = process.env.VITE_DEV_CONTROLLER_BASE || 'http://127.0.0.1:48123'

export default defineConfig({
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
  test: {
    environment: 'happy-dom',
  },
})
