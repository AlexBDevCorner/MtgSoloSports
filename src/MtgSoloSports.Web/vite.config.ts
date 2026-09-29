import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      // MSS-043: overridable container-to-container API target. Host
      // development keeps the default (backend on localhost:5180); the
      // optional Docker dev service sets VITE_API_PROXY=http://app:8080 so
      // the Vite container reaches the backend via Compose service DNS
      // while browsers keep using the published localhost port.
      '/api': process.env.VITE_API_PROXY ?? 'http://localhost:5180',
    },
  },
  build: {
    outDir: '../MtgSoloSports/wwwroot',
    emptyOutDir: true,
  },
})
