import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const backendUrl = env.VITE_BACKEND_URL || 'http://localhost:5050'

  return {
    plugins: [react()],
    build: {
      outDir: 'dist',
      emptyOutDir: true,
    },
    server: {
      proxy: {
        '/api':      { target: backendUrl, changeOrigin: true },
        '/og-image': { target: backendUrl, changeOrigin: true },
        '/cms':      { target: backendUrl, changeOrigin: true },
      },
    },
  }
})
