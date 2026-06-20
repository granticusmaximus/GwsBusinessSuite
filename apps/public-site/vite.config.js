import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: 'dist',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': {
        target: process.env.VITE_BACKEND_URL || 'http://localhost:5050',
        changeOrigin: true,
      },
      '/og-image': {
        target: process.env.VITE_BACKEND_URL || 'http://localhost:5050',
        changeOrigin: true,
      },
    },
  },
})
